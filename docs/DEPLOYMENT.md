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

**Existing databases** created before per-organization credentials and token
balances need one SQL migration. It is idempotent (safe to re-run) and wraps
everything in a transaction; it renames `provider_keys` → `provider_credentials`
(existing rows become platform-wide `api_key` rows), adds
`memberships.token_balance`, and creates `token_ledger`:

```bash
psql "$POSTGRES_URL" -f infra/db/migrations/002_provider_credentials_and_token_balance.sql
# compose:
docker compose exec -T postgres psql -U gateway -d gateway \
  < infra/db/migrations/002_provider_credentials_and_token_balance.sql
```

Run it before rolling out the new gateway image; the old image keeps working
against the migrated schema. Later schema changes go in
`infra/db/migrations/NNN_*.sql` the same way, or as EF migrations:

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

### 0.5 Data encryption key

Provider credentials (org API keys, OAuth access/refresh tokens) are stored
AES-256-GCM encrypted under `Encryption:DataKey` (`DATA_ENCRYPTION_KEY` in
`.env`, `Encryption__DataKey` in K8s). Generate one and set it before the first
credential is saved:

```bash
openssl rand -base64 32
```

It must decode to exactly 32 bytes or the gateway refuses to start. When it is
unset, the key is derived from `Jwt:SigningKey` (SHA-256) so rows written by
earlier versions still decrypt, and the gateway logs a warning at startup. Do not
run production that way: rotating the JWT key would then make every stored
credential undecryptable. Rotating the data key is not automatic — admins must
re-enter credentials afterwards.

### 0.6 API exposure

The gateway trusts `X-Forwarded-For` / `X-Forwarded-Proto` from any upstream
(`ForwardedHeaders:TrustAllProxies`, default `true` in `appsettings.json`) because
it expects to sit only behind `infra/nginx/nginx.conf` or the K8s ingress, both
of which set those headers. Per-IP rate limiting on the auth endpoints and the
IP in audit logs depend on it. **Never publish the API container's port
directly.** If you must, set `ForwardedHeaders__TrustAllProxies=false` so a
client cannot spoof its address.

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
| `DATA_ENCRYPTION_KEY` | `openssl rand -base64 32` — see 0.5; empty falls back to the JWT-derived key |
| `ANTHROPIC_API_KEY` | optional platform-wide fallback; org admins connect their own credential at `/admin/providers` |
| `WHATSAPP_BOT_API_KEY` | required for OTP login |

Optional:

| Variable | Config key | Notes |
| --- | --- | --- |
| `REGISTRATION_ENABLED` | `Registration__Enabled` | `true` opens self-service org signup at `/register` (`POST /api/v1/auth/register`). Default `false`: the bootstrap admin creates everyone. |
| `REGISTRATION_INVITE_CODE` | `Registration__InviteCode` | When set, signups must present it (403 `invalid_invite` otherwise). |
| `ANTHROPIC_OAUTH_CLIENT_ID` | `Anthropic__OAuth__ClientId` | OAuth client Anthropic issued to **your** organization. Never reuse another application's client id. |
| `ANTHROPIC_OAUTH_CLIENT_SECRET` | `Anthropic__OAuth__ClientSecret` | Omit for public (PKCE-only) clients. |
| `ANTHROPIC_OAUTH_AUTHORIZE_URL` / `_TOKEN_URL` | `Anthropic__OAuth__AuthorizeUrl` / `TokenUrl` | Absolute URLs. |
| `ANTHROPIC_OAUTH_SCOPES` | `Anthropic__OAuth__Scopes` | Space-separated. |
| `ANTHROPIC_OAUTH_REDIRECT_URI` | `Anthropic__OAuth__RedirectUri` | `https://<gateway host>/admin/providers/callback`, registered with the provider. |

Browser login on `/admin/providers` appears only when `ClientId`, `AuthorizeUrl`,
`TokenUrl` and `RedirectUri` are all set; otherwise admins paste an API key.
`Anthropic:OAuth:RefreshSkewSeconds` (default 120, `appsettings.json`) controls
how early access tokens are refreshed.

```bash
docker compose up --build
```

Brings up Postgres, Redis, RabbitMQ, ClickHouse, the gateway, the analytics
worker, the Next.js dashboards, and NGINX.

### Ports

`infra/nginx/nginx.conf` defines a unified entry on port 80, but
`docker-compose.yml` publishes **only 9001 and 9002** (8080 is reserved by
Jenkins on the build host). Use:

| URL | What |
| --- | --- |
| `http://localhost:9001` | Gateway API (`/api/v1/...`) and the Anthropic-compatible proxy (`/v1/messages`, `/v1/models`) |
| `http://localhost:9002` | Dashboards (`/register`, `/login`, `/usage`, `/admin`, `/admin/providers`, `/super-admin`) plus `/api/` and `/v1/` proxied to the gateway |
| `http://localhost:15672` | RabbitMQ management UI |
| `localhost:5436` | Postgres |

Point the setup wizard at the published port (`--api` flag or `APUS_AI_API`):

```bash
cd cli && npm install && npm run build
APUS_AI_API=http://localhost:9002 node dist/index.js        # = `npx apus-ai setup`
```

Use `:9002` — the 9002 server block proxies `/api/...` and `/v1/...` to the
gateway, so one origin serves the dashboards, the CLI and the editor clients.
The 9001 block exposes the gateway alone for direct API clients. Both proxy the
`/v1/messages` stream with buffering off and a 600 s read timeout, and accept
bodies up to 32 MB (`client_max_body_size`) for images and PDFs.

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
  --from-literal=Encryption__DataKey="$(openssl rand -base64 32)" \
  --from-literal=Anthropic__ApiKey="" \
  --from-literal=Registration__Enabled="false" \
  --from-literal=Registration__InviteCode="" \
  --from-literal=Anthropic__OAuth__ClientId="" \
  --from-literal=Anthropic__OAuth__ClientSecret="" \
  --from-literal=Anthropic__OAuth__AuthorizeUrl="" \
  --from-literal=Anthropic__OAuth__TokenUrl="" \
  --from-literal=Anthropic__OAuth__Scopes="" \
  --from-literal=Anthropic__OAuth__RedirectUri="" \
  --from-literal=ConnectionStrings__Postgres="Host=...;Database=gateway;Username=gateway;Password=..." \
  --from-literal=ConnectionStrings__Redis="redis:6379" \
  --from-literal=ConnectionStrings__RabbitMq="amqp://gateway:...@rabbitmq:5672" \
  --from-literal=ConnectionStrings__ClickHouseHttp="http://clickhouse:8123" \
  --from-literal=ConnectionStrings__ClickHouse="Host=clickhouse;Port=8123;Database=default" \
  --from-literal=WhatsApp__BotUrl="http://whatsapp-bot:3000" \
  --from-literal=WhatsApp__ApiKey="..."
```

`infra/k8s/secrets.example.yaml` lists every key with a placeholder. Without
`ConnectionStrings__ClickHouseHttp` the admin dashboards return errors; without
`ConnectionStrings__ClickHouse` the analytics worker cannot write. Keep
`Encryption__DataKey` in a backed-up secret store: losing it loses every stored
provider credential. Leave the `Anthropic__OAuth__*` values empty unless you hold
an OAuth client issued to your organization.

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

The ingress must route `/v1/` and `/api/` to `gateway-api` and disable response
buffering for `/v1/messages` (nginx-ingress:
`nginx.ingress.kubernetes.io/proxy-buffering: "off"`,
`proxy-read-timeout: "600"`, `proxy-body-size: "32m"`). The gateway Service must
not be exposed outside the cluster (see 0.6).

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

# 4. Mint a personal key and hit the Anthropic-compatible proxy (what Claude Code does)
KEY=$(curl -fsS -X POST http://<host>/api/v1/me/keys -H "authorization: Bearer $JWT" \
  -H 'content-type: application/json' -d '{"name":"smoke"}' | jq -r .key)
curl -fsS http://<host>/v1/models -H "x-api-key: $KEY"
curl -fsS -X POST http://<host>/v1/messages -H "x-api-key: $KEY" \
  -H 'anthropic-version: 2023-06-01' -H 'content-type: application/json' \
  -d '{"model":"claude-sonnet-5","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}'
curl -fsS http://<host>/api/v1/me/usage -H "authorization: Bearer $JWT"   # counter moved

# 5. Analytics landed in ClickHouse (worker + RabbitMQ healthy)
clickhouse-client -q "SELECT user_id, sum(total_tokens) FROM usage_events GROUP BY user_id"
```

Step 4 needs a Claude credential: connect one at `/admin/providers` (org-owned)
or `/super-admin` (platform-wide fallback), or set `ANTHROPIC_API_KEY`. A 503
`api_error` "No AI provider is connected" means none of those is in place.

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
        "allowedModels": ["claude-sonnet-5"]
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
  `workspace_policy_set`. Personal-key lifecycle is audited as `api_key_created`,
  `api_key_revoked` (by the user) and `api_key_revoked_by_admin`.
- **Key revocation propagates within 30 seconds per API pod.** The
  `ApiKeyAuthenticationHandler` caches each key lookup (hits and misses) in
  memory for 30 s, so a revoked or deactivated key may be accepted by a pod that
  validated it in the last half minute. Rolling deploys reset the cache.
- Prepaid balances are separate from windows: `AdminBalanceController`
  (`/api/v1/admin/users/{id}/balance`, grant / set / delete) writes
  `memberships.token_balance` and `token_ledger`. `null` = unlimited. See
  `docs/ADMIN_GUIDE.md` for semantics.

---

## 4. Observability

- `/metrics` — Prometheus scrape endpoint (`MapPrometheusScrapingEndpoint`).
- `/health/live` — always-200 liveness; `/health/ready` — Postgres + Redis probes.
- OTLP traces export to `OTEL_EXPORTER_OTLP_ENDPOINT`; set it or the exporter
  retries against localhost and adds latency.
- Serilog writes structured console logs with a correlation id; ship stdout.

Worth alerting on: 429/402 rate from `/v1/messages` (window / balance
exhaustion), Redis connection failures (quota enforcement is down),
`usage.analytics` queue depth (worker falling behind), provider circuit-breaker
trips, and the log line `Provider credential {Id} ... deactivated` — an OAuth
refresh was rejected and every user of that org gets 503 until an admin
reconnects at `/admin/providers`.

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
- [x] Per-organization provider credentials (API key / OAuth) encrypted at rest
- [x] Prepaid token balances with an immutable ledger
- [x] Anthropic-compatible proxy (`/v1/messages`, `/v1/models`) with personal keys
- [x] Self-service registration behind `Registration__Enabled`
- [x] SQL migration path (`infra/db/migrations/`); EF migrations still optional
- [ ] Enforce `RequestsPerMinute` — resolved into `EffectivePolicy`, never used
- [ ] Honor `WindowKind.Fixed` — the Lua only implements rolling TTL windows
- [ ] Frontend K8s Deployment + Service (referenced by `ingress.yaml`)
- [ ] OpenAI provider — mirror `AnthropicProvider`, register in `Program.cs`
- [ ] Web login route that sets an httpOnly cookie for the dashboard
- [ ] Billing consumer on the `usage.billing` queue (declared and bound already)
- [ ] Anomaly detector over audit logs / session IPs

---

## Upgrading to multi-provider connections and allowances (migration 003)

### Apply

```bash
psql "$POSTGRES_URL" -f infra/db/migrations/003_provider_connections_and_allowances.sql
```

Idempotent and backward compatible: every column is added nullable or with a default,
nothing is dropped or retyped, and a gateway running the previous build keeps working
against the upgraded schema (the new columns simply read as their defaults). Apply it
**before** rolling the new image, so the deploy is a normal rolling update.

CI proves this on every push: a job builds the schema fresh, builds it again by applying
003 to the previous schema, and runs 003 a second time.

### The one data change

Where an organization has several *active* credentials for the same provider, all but
the newest are marked `disabled`. Credential resolution has always picked the newest row
(`ORDER BY created_at DESC`), so the others were already dead weight; this makes it
explicit and lets the one-live-connection unique index exist. Check first if you want to
see what will move:

```sql
SELECT organization_id, provider, count(*)
  FROM provider_credentials WHERE is_active
 GROUP BY 1, 2 HAVING count(*) > 1;
```

### Rollback

Redeploy the previous image. No schema rollback is required — the old build ignores the
new columns and tables. If you must reverse the schema, the destructive step is the
unique index and the new tables:

```sql
DROP INDEX IF EXISTS ux_provider_connections_live;
DROP TABLE IF EXISTS ai_usage_ledger, allowance_periods, notification_outbox, provider_model_pricing;
-- The added columns are harmless; leave them unless you have a reason.
```

Connections disabled by the collapse step stay disabled. Note that rolling back after
traffic has run loses the allowance and cost history recorded since the upgrade — the
token ledger and ClickHouse analytics are unaffected.

### Configure

Minimum for the new features:

```bash
# Tenants are billed in their own currency; provider list prices are USD.
BILLING_DEFAULT_CURRENCY=INR
BILLING_USD_RATE_INR=83
BILLING_DEFAULT_MARKUP_BPS=0

# A dedicated at-rest key (this was already recommended; it is now load-bearing).
DATA_ENCRYPTION_KEY="$(openssl rand -base64 32)"
```

Browser login is optional — see [PROVIDERS.md](PROVIDERS.md) for the client settings and
the exact redirect URI to register.

### Roll out

Flags are off, on, or on for a named list of organization ids:

```bash
Features__Flags__AI_PROVIDER_FALLBACK__Enabled=false
Features__Flags__AI_PROVIDER_FALLBACK__Organizations__0=<pilot-org-id>
```

Defaults: connections, OAuth, child allowances and usage billing on; provider fallback
off. Enable fallback per tenant once your model equivalences are agreed — it must respect
the member's model permissions, the tenant's provider permissions and data-residency
constraints, and every fallback is recorded in the usage ledger.

### Verify

```bash
# Prices seeded on first start
psql "$POSTGRES_URL" -c "select count(*) from provider_model_pricing"

# Readiness: rabbitmq reports Degraded, not Unhealthy, when the broker is down
curl -s localhost:8080/health/ready

# The whole flow
npx apus-ai providers
```

### Rotating the at-rest key

```bash
Encryption__Keys__2="$(openssl rand -base64 32)"
Encryption__CurrentKeyVersion=2
```

New writes use version 2 immediately; `CredentialRotationWorker` re-seals existing rows
in the background. Keep version 1 until nothing reports it:

```sql
SELECT encryption_key_version, count(*) FROM provider_credentials GROUP BY 1;
```

### Operational notes

- **RabbitMQ is no longer required to start.** The publisher connects lazily and retries;
  the readiness check reports `Degraded` so the pod stays in service. Accounting is
  unaffected — the usage ledger is written to Postgres on the request path.
- **Workers** run in-process on every replica and are safe to run concurrently (period
  rollover uses `ON CONFLICT DO NOTHING`, threshold notifications are claimed with a
  conditional bitmask update). Set `Workers__Enabled=false` to disable them on a replica.
- **Abandoned reservations** from a pod killed mid-request are swept after
  `Workers__ReservationStaleMinutes` (default 30, which must exceed the longest possible
  request; the proxy client times out at 10 minutes).
