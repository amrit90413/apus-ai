# Deployment Guide

Covers local (Docker Compose), first-admin bootstrap, and Kubernetes. Read
**Preflight blockers** first — three of them stop a fresh deploy from serving a
single request.

---

## 0. Before you deploy

The schema mismatch and the missing bootstrap path described in earlier revisions
of this guide are now fixed in code. What remains is configuration.

### 0.1 Schema

`GatewayDbContext` maps every entity explicitly to the snake_case tables in
`infra/db/postgres-init.sql`, so EF and the init script agree and
`docker compose up` works against a fresh volume with no migration step.

No EF migrations are committed. Once the schema starts changing, generate them and
apply as a deploy step or K8s Job — the explicit mappings carry over unchanged:

```bash
cd backend/src/Gateway.Api
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial
dotnet ef database update
```

### 0.2 First admin

`DatabaseBootstrapper` seeds one organization, workspace, super admin and
membership the first time the gateway starts against an empty `users` table, then
no-ops on every later start. Configure it before the first boot:

| Variable | Required | Notes |
| --- | --- | --- |
| `BOOTSTRAP_ADMIN_EMAIL` | yes | login email for the super admin |
| `BOOTSTRAP_ADMIN_PASSWORD` | yes | hashed with PBKDF2 on seed; warns under 12 chars |
| `BOOTSTRAP_ADMIN_PHONE` | yes, unless OTP is off | E.164, e.g. `919876543210` |
| `BOOTSTRAP_ORG_NAME` / `BOOTSTRAP_ORG_SLUG` | no | defaults to YourCompany / yourcompany |

The phone number is required because admin logins are OTP-gated: an admin seeded
without one gets `422 no_phone` at every login attempt. The seeder refuses to
create an unusable account and logs an error instead.

### 0.3 Admin login without an OTP bot

`WHATSAPP_ENABLED=false` lets OrgAdmin and SuperAdmin log in with a password
alone, for deployments with no WhatsApp bot. This removes the second factor from
every admin account — each such login is recorded as `admin_login_without_otp` in
the audit log, and the gateway logs a warning at startup.

With OTP enabled, a delivery failure now returns `502 otp_delivery_failed`.
Previously the gateway swallowed the error and handed back a pending token that
could never be completed, locking the admin out with no diagnostic.

### 0.4 Rotate the committed secret

`WHATSAPP_BOT_API_KEY=sahil90413` was committed in `.env.example` and duplicated
as a fallback in both `docker-compose.yml` and `WhatsAppOptions`. All three now
default to empty, but the value is in git history — **rotate it at the bot.**

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

## 3a. Managing quota and models

`AdminQuotaController` (policy `OrgAdmin`) is the write path for the fields
`QuotaPolicyResolver` reads. All routes take a JWT with an OrgAdmin or SuperAdmin
role.

| Route | Purpose |
| --- | --- |
| `GET /api/v1/admin/users/{id}/quota` | the user's override plus their effective policy |
| `PUT /api/v1/admin/users/{id}/quota` | set per-user windows and/or model allow-list |
| `DELETE /api/v1/admin/users/{id}/quota` | drop the override, inherit the workspace |
| `GET /api/v1/admin/workspaces/{id}/policy` | current workspace policy |
| `PUT /api/v1/admin/workspaces/{id}/policy` | partial update; omitted fields keep their value |

```bash
# Tighten one employee to 50k per 5h and a 500k monthly ceiling, Sonnet only
curl -X PUT "http://<host>/api/v1/admin/users/<user-id>/quota" \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{
        "windows": [
          {"name":"w5h","tokenLimit":50000,"windowMinutes":300},
          {"name":"monthly","tokenLimit":500000,"windowMinutes":43200}
        ],
        "allowedModels": ["claude-sonnet-4-6"]
      }'
```

Pass `?workspaceId=` when the user belongs to more than one workspace — quota is
owned by `(user, workspace)`, so the endpoint refuses to guess.

Things worth knowing before you change a live policy:

- **Renaming a window orphans its counter.** The window name is part of the Redis
  key (`quota:user:{id}:user:{name}`), so a rename starts a fresh counter at zero
  and the old one expires on its own TTL. Changing only a limit keeps the counter.
- **Lowering a limit below current usage blocks that user** until the window's TTL
  expires. That is the intended behavior, not a bug.
- **Propagation is bounded by the 30s policy cache.** A write evicts the entry on
  the pod that served it; other replicas refresh within 30 seconds.
- **Models are validated against the registered providers** — a model no provider
  serves is rejected with `unknown_model` rather than failing later at request time.
- Every write lands in `audit_logs` as `user_quota_set`, `user_quota_cleared`, or
  `workspace_policy_set`.

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

- [x] Explicit snake_case schema mapping so EF matches `postgres-init.sql`
- [x] Bootstrap/seed path for the first super admin
- [x] Admin write endpoints for quota policy JSON (`AdminQuotaController`)
- [x] Per-user model allow-lists
- [ ] EF migrations, once the schema starts changing
- [ ] Enforce `RequestsPerMinute` — resolved into `EffectivePolicy`, never used
- [ ] Honor `WindowKind.Fixed` — the Lua only implements rolling TTL windows
- [ ] Frontend K8s Deployment + Service (referenced by `ingress.yaml`)
- [ ] OpenAI provider — mirror `AnthropicProvider`, register in `Program.cs`
- [ ] Web login route that sets an httpOnly cookie for the dashboard
- [ ] Billing consumer on the `usage.billing` queue (declared and bound already)
- [ ] Anomaly detector over audit logs / session IPs
