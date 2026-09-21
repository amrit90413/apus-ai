# Deployment Guide

Covers local (Docker Compose), first-admin bootstrap, and Kubernetes. Read
**Preflight blockers** first — three of them stop a fresh deploy from serving a
single request.

---

## 0. Preflight blockers

These are real gaps in the current tree, not warnings. Fix 0.1 and 0.2 before any
environment will work end to end.

### 0.1 The schema does not match what EF Core queries

`infra/db/postgres-init.sql` creates **snake_case** tables (`organizations`,
`memberships.per_user_quota_json`). `GatewayDbContext` configures no naming
convention, so EF Core maps to its defaults — **PascalCase**, quoted by Npgsql:
`"Organizations"`, `"Memberships"."PerUserQuotaJson"`. Quoted identifiers are
case-sensitive in Postgres, so every query fails with
`42P01: relation "Users" does not exist`.

There are also **no EF migrations committed** (no `Migrations/` directory) and
`Program.cs` never calls `Migrate()` or `EnsureCreated()` — nothing creates the
schema EF expects.

Pick one fix:

**Option A — generate migrations (recommended for production).** The init SQL
becomes reference-only documentation.

```bash
cd backend/src/Gateway.Api
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial
dotnet ef database update --connection "Host=localhost;Port=5436;Database=gateway;Username=gateway;Password=<pw>"
```

Then either run `dotnet ef database update` as a deploy step / K8s Job, or add
migrate-on-boot to `Program.cs` (fine at 3 replicas; EF takes a Postgres advisory
lock, so concurrent pods serialize):

```csharp
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.MigrateAsync();
```

**Option B — make EF speak snake_case**, matching the existing init SQL. Add the
`EFCore.NamingConventions` package and one call:

```csharp
o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
 .UseSnakeCaseNamingConvention();
```

Option B keeps `docker compose up` working with zero migration steps. Option A is
the right answer once the schema starts changing.

### 0.2 There is no way to create the first user

`AuthController` exposes only `login`, `verify-otp`, `refresh`, `logout` — no
registration. `AdminUsersController` requires an `OrgAdmin` JWT, and no seed data
exists. So on a fresh database nobody can log in and nobody can be created.

Insert the first org + workspace + user + membership by hand. Generate the
password hash in `PasswordHasher`'s exact format (`{iterations}.{saltB64}.{hashB64}`,
PBKDF2-SHA256, 600k iterations, 16-byte salt, 32-byte key):

```bash
python3 -c "
import hashlib, os, base64
salt = os.urandom(16)
key  = hashlib.pbkdf2_hmac('sha256', b'CHANGE-ME', salt, 600000, 32)
print(f'600000.{base64.b64encode(salt).decode()}.{base64.b64encode(key).decode()}')
"
```

Then seed (table/column casing per the option you chose in 0.1 — shown here for
Option A / PascalCase):

```sql
INSERT INTO "Organizations" ("Id","Name","Slug","PlanCode","IsActive","CreatedAt")
VALUES ('00000000-0000-0000-0000-000000000001','YourCompany','yourcompany','free',true,now());

INSERT INTO "Workspaces" ("Id","OrganizationId","Name","IsActive")
VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Default',true);

INSERT INTO "Users" ("Id","OrganizationId","Email","PasswordHash","PhoneVerified","IsActive","CreatedAt")
VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001',
        'admin@yourcompany.com','<hash from above>',true,true,now());

-- Role 3 = SuperAdmin (see Domain/Entities.cs: User=0, WorkspaceAdmin=1, OrgAdmin=2, SuperAdmin=3)
INSERT INTO "Memberships" ("Id","OrganizationId","UserId","WorkspaceId","Role")
VALUES ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002',3);
```

A proper fix is a one-shot bootstrap endpoint or a seeder that runs only when
`Users` is empty.

### 0.3 Rotate the committed secret

`WHATSAPP_BOT_API_KEY=sahil90413` is committed in `.env.example`, and
`docker-compose.yml` falls back to the same literal
(`WhatsApp__ApiKey: ${WHATSAPP_BOT_API_KEY:-sahil90413}`). Treat it as leaked:
rotate it at the bot, and replace the compose default with an empty fallback.

---

## 1. Local — Docker Compose

```bash
cp .env.example .env
```

Fill in, at minimum:

| Variable | Notes |
| --- | --- |
| `POSTGRES_PASSWORD` | any strong value |
| `RABBITMQ_PASSWORD` | any strong value |
| `JWT_SIGNING_KEY` | `openssl rand -base64 48` — must be ≥32 bytes |
| `ANTHROPIC_API_KEY` | optional; can be added later via `/super-admin` |
| `WHATSAPP_BOT_API_KEY` | required for OTP login |

```bash
docker compose up --build
```

Brings up Postgres, Redis, RabbitMQ, ClickHouse, the gateway, the analytics
worker, the Next.js dashboards, and NGINX.

### Ports — the README is wrong here

`infra/nginx/nginx.conf` defines a unified entry on port 80, but
`docker-compose.yml` publishes **only 9001 and 9002**. Port 80 is never mapped, so
`http://localhost:8080` does not resolve. Use:

| URL | What |
| --- | --- |
| `http://localhost:9001` | Gateway API (chat lives at `/v1/chat/`, not `/api/v1/chat/`) |
| `http://localhost:9002` | Dashboards (`/usage`, `/admin`, `/super-admin`) |
| `http://localhost:15672` | RabbitMQ management UI |
| `localhost:5436` | Postgres |

Point the CLI at the published port:

```bash
cd cli && npm install && npm run build
YOURCOMPANY_AI_API=http://localhost:9002 node dist/index.js login
```

Use `:9002` — the 9002 server block proxies `/api/...` to the gateway and matches
the CLI's path expectations. The 9001 block strips the `/api` prefix and suits
direct API clients.

To restore the documented `:8080` entry instead, publish it in `docker-compose.yml`:

```yaml
  nginx:
    ports:
      - "8080:80"
```

### ClickHouse schema

The compose file mounts `clickhouse-init.sql` into
`/docker-entrypoint-initdb.d/`, so it applies automatically on a **fresh** volume.
If you added it later, apply it by hand:

```bash
docker compose exec -T clickhouse clickhouse-client --multiquery < infra/db/clickhouse-init.sql
```

### Known compose nit

The gateway's `HEALTHCHECK` shells out to `wget`, which is not present in
`mcr.microsoft.com/dotnet/aspnet:9.0`. The container runs fine but reports
`unhealthy` forever. Either install wget in `backend/Dockerfile` or drop the
HEALTHCHECK line and rely on the K8s probes.

---

## 2. Kubernetes

### 2.1 Namespace and secrets

```bash
kubectl apply -f infra/k8s/namespace.yaml

kubectl -n ai-gateway create secret generic gateway-secrets \
  --from-literal=Jwt__SigningKey="$(openssl rand -base64 48)" \
  --from-literal=Anthropic__ApiKey="sk-ant-..." \
  --from-literal=ConnectionStrings__Postgres="Host=...;Database=gateway;Username=gateway;Password=..." \
  --from-literal=ConnectionStrings__Redis="redis:6379" \
  --from-literal=ConnectionStrings__RabbitMq="amqp://gateway:...@rabbitmq:5672" \
  --from-literal=ConnectionStrings__ClickHouseHttp="http://clickhouse:8123" \
  --from-literal=ConnectionStrings__ClickHouse="Host=clickhouse;Port=8123;Database=default" \
  --from-literal=WhatsApp__BotUrl="http://whatsapp-bot:3000" \
  --from-literal=WhatsApp__ApiKey="..."
```

`infra/k8s/secrets.example.yaml` omits the last four keys. Without
`ConnectionStrings__ClickHouseHttp` the admin dashboards return errors; without
`ConnectionStrings__ClickHouse` the analytics worker cannot write.

### 2.2 Images

Both deployments reference `ghcr.io/yourcompany/*:latest`. Build, tag by commit SHA
(not `latest` — it defeats rollout tracking and rollback), and push:

```bash
docker build -t ghcr.io/<you>/gateway-api:$(git rev-parse --short HEAD) -f backend/Dockerfile backend
docker build -t ghcr.io/<you>/analytics-worker:$(git rev-parse --short HEAD) -f backend/Dockerfile.worker backend
docker build -t ghcr.io/<you>/frontend:$(git rev-parse --short HEAD) frontend
docker push ghcr.io/<you>/gateway-api:$(git rev-parse --short HEAD)   # and the other two
```

Update the `image:` fields in `infra/k8s/*.yaml` to your registry and tag.

### 2.3 Apply

```bash
kubectl apply -f infra/k8s/gateway-deployment.yaml
kubectl apply -f infra/k8s/worker-deployment.yaml
kubectl apply -f infra/k8s/ingress.yaml
```

**Missing manifest:** `ingress.yaml` routes `/` to a Service named `frontend`, but
no frontend Deployment or Service exists in `infra/k8s/`. Either add one (mirror
`gateway-deployment.yaml`, container port 3000, env `GATEWAY_URL=http://gateway-api:8080`)
or remove the `/` rule and serve the dashboards elsewhere.

### 2.4 Backing services

Use managed Postgres, Redis, RabbitMQ, and ClickHouse rather than in-cluster
StatefulSets. If you do run Redis yourself, keep the compose settings:
`--appendonly yes --maxmemory-policy noeviction`. **Redis is correctness-critical
here, not a cache** — it holds every live quota counter. Eviction or a wipe resets
everyone's usage window to zero; `noeviction` makes the gateway fail loudly instead
of silently granting free tokens.

### 2.5 Scaling

The gateway is stateless (all shared state is in Redis/Postgres), so the HPA scales
it 3→20 on 65% CPU. The analytics worker runs 2 replicas as competing consumers on
`usage.analytics`; scale it on RabbitMQ queue depth, not CPU. Note the HPA targets
CPU while the workload is long-lived SSE streams — if you see thrash, switch to a
custom concurrent-connections metric.

---

## 3. Verify a deploy

```bash
# 1. Liveness / readiness (readiness checks Postgres + Redis)
curl -fsS http://<host>/health/live
curl -fsS http://<host>/health/ready

# 2. Log in as the seeded admin, capture the JWT
curl -fsS -X POST http://<host>/api/v1/auth/login \
  -H 'content-type: application/json' \
  -d '{"email":"admin@yourcompany.com","password":"CHANGE-ME"}'

# 3. Quota windows resolve (proves Redis + Lua scripts loaded)
curl -fsS http://<host>/api/v1/me/usage -H "authorization: Bearer $JWT"

# 4. End-to-end token spend, then confirm the counter moved
curl -fsS -X POST http://<host>/api/v1/chat/stream -H "authorization: Bearer $JWT" \
  -H 'content-type: application/json' \
  -d '{"model":"claude-sonnet-4-6","messages":[{"role":"user","content":"hi"}],"maxTokens":64}'
curl -fsS http://<host>/api/v1/me/usage -H "authorization: Bearer $JWT"

# 5. Analytics landed in ClickHouse (worker + RabbitMQ healthy)
clickhouse-client -q "SELECT user_id, sum(total_tokens) FROM usage_events GROUP BY user_id"
```

If step 3 fails at startup with a Lua error, confirm the `.lua` files were copied
next to the binary — `Gateway.Api.csproj` marks them `CopyToOutputDirectory`, and
`QuotaEngine.LoadScriptsAsync` reads them from `{AppContext.BaseDirectory}/Quota`.

---

## 4. Observability

- `/metrics` — Prometheus scrape endpoint (`MapPrometheusScrapingEndpoint`).
- `/health/live` — always-200 liveness; `/health/ready` — Postgres + Redis probes.
- OTLP traces export to `OTEL_EXPORTER_OTLP_ENDPOINT`; set it or the exporter
  retries against localhost and adds latency.
- Serilog writes structured console logs with a correlation id; ship stdout.

Worth alerting on: 429 rate from `ChatController` (quota exhaustion), Redis
connection failures (quota enforcement is down), `usage.analytics` queue depth
(worker falling behind), and provider circuit-breaker trips.

---

## 5. Rollback

```bash
kubectl -n ai-gateway rollout undo deployment/gateway-api
kubectl -n ai-gateway rollout undo deployment/analytics-worker
```

Quota counters live in Redis with TTLs and are unaffected by an app rollback.
Migrations are not auto-reverted — roll schema changes forward.

---

## 6. Extension checklist

- [ ] EF migrations + a schema-creation step (blocker 0.1)
- [ ] Bootstrap/seed path for the first super admin (blocker 0.2)
- [ ] Admin write endpoints for quota policy JSON — `Membership.PerUserQuotaJson`
      and `Workspace.QuotaPolicyJson` are read by `QuotaPolicyResolver` but nothing
      writes them, so per-user limits can only be set with direct SQL
- [ ] Per-user model allow-lists — `AllowedModels` is resolved from the workspace
      policy only; the per-user blob's copy is discarded
- [ ] Enforce `RequestsPerMinute` — resolved into `EffectivePolicy`, never used
- [ ] Honor `WindowKind.Fixed` — the Lua only implements rolling TTL windows
- [ ] Frontend K8s Deployment + Service (referenced by `ingress.yaml`)
- [ ] OpenAI provider — mirror `AnthropicProvider`, register in `Program.cs`
- [ ] Web login route that sets an httpOnly cookie for the dashboard
- [ ] Billing consumer on the `usage.billing` queue (declared and bound already)
- [ ] Anomaly detector over audit logs / session IPs
