# apus-ai Gateway

An Anthropic-compatible AI gateway that lets an organization's engineers use
Claude Code, the VS Code Claude extension, Cline and Roo Code through one
controlled endpoint — so the organization can **connect its own Claude
credential once, hand each person a prepaid token allowance and a model list,
track every request, and keep provider secrets server-side**.

Engineers run one command instead of pasting a shared key into their editor:

```bash
npx apus-ai
```

It logs them in, mints a personal `apus_...` key and points their editor's
`ANTHROPIC_BASE_URL` at the gateway. See [docs/CLIENT_SETUP.md](docs/CLIENT_SETUP.md).

Every request flows: **editor → NGINX → Gateway API (`/v1/messages`) → personal key
→ model allow-list → prepaid balance (Postgres) → Redis quota windows → the
organization's Claude credential → reconcile real usage → RabbitMQ → ClickHouse
analytics → dashboards.**

## Per-organization credentials (read this first)

Each organization connects **its own** Claude credential; the platform can hold a
fallback. The gateway fans that credential out to many people safely:

- An org admin connects a Claude Console API key (or, when the operator has
  configured an OAuth client the org owns, a browser login) at `/admin/providers`.
  It is stored AES-256-GCM encrypted and never sent to the CLI or the browser.
  A super admin may add a platform-wide fallback at `/super-admin`;
  `Anthropic__ApiKey` is the last resort.
- Each person gets a personal `apus_...` key (stored hashed) that carries their
  `UserId` + `WorkspaceId`; dashboards and the CLI use short-lived JWTs.
- Admins allocate a prepaid **token balance** per person (`null` = unlimited) and
  an allowed-model list. The balance is debited atomically in Postgres and every
  change lands in an immutable ledger; rolling windows still apply on top.
- Quotas are charged against that identity, **never against IP**. IP is recorded only for
  anomaly detection and audit logs (people may be on VPNs, shared offices, etc.).
- Redis enforces per-user and per-workspace token windows atomically, so concurrent
  requests can't race past a limit.

## Proxy endpoints

| Endpoint | Auth | Notes |
| --- | --- | --- |
| `POST /v1/messages` | `x-api-key: apus_...` or `Authorization: Bearer apus_...` | Anthropic Messages API, streamed or not; `[1m]` model suffix stripped; extra headers `X-Quota-Remaining`, `X-Quota-Reset-Seconds`, `X-Balance-Remaining` |
| `POST /v1/messages/count_tokens` | same | Passthrough, not charged |
| `GET /v1/models` | same | Only the caller's allocated models |

Current Claude model ids: `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`.

## Roles

| Role | Sees | Typical action |
| --- | --- | --- |
| User | Own usage, balance, keys, sessions | Run `npx apus-ai`, check remaining tokens |
| Workspace admin | Per-person usage in their workspace | Adjust the workspace quota window |
| Org admin | All workspaces in the org | Connect Claude, create users, grant tokens, set models |
| Super admin | All organizations (cross-tenant) | Platform health, fallback credential, anomalies, billing |

Pages: `/register` (self-service org signup, when `REGISTRATION_ENABLED=true`),
`/login`, `/usage`, `/admin`, `/admin/users`, `/admin/providers`, `/super-admin`.
Admin walkthrough: [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md).

## Repository layout

```
backend/      ASP.NET Core 9 gateway + analytics worker (Clean-ish layering)
  src/Gateway.Api/        Auth, Quota, Providers, Gateway, Messaging, Persistence, Domain
  src/Analytics.Worker/   RabbitMQ -> ClickHouse consumer
cli/          Node.js + TypeScript setup wizard (Commander.js), published as `apus-ai`
frontend/     Next.js dashboards (register / usage / admin / providers / super-admin)
infra/        nginx config, db init SQL + migrations, Kubernetes manifests
.github/      CI/CD pipeline
docs/         architecture, deployment, security, client setup, admin guide
```

## Run it locally

Requires Docker + Docker Compose.

```bash
cp .env.example .env        # fill in the passwords, JWT_SIGNING_KEY, DATA_ENCRYPTION_KEY
docker compose up --build
```

This starts Postgres, Redis, RabbitMQ, ClickHouse, the gateway, the analytics worker,
the Next.js dashboards, and NGINX. Dashboards and the proxy are on
`http://localhost:9002`; the bare API on `http://localhost:9001`.

Then: log in as the bootstrap admin (`BOOTSTRAP_ADMIN_*` in `.env`) — or set
`REGISTRATION_ENABLED=true` and sign up your own org at
`http://localhost:9002/register` — connect a Claude API key at
`/admin/providers`, create a user, and run the wizard against the local gateway:

```bash
cd cli && npm install && npm run build
APUS_AI_API=http://localhost:9002 node dist/index.js      # or: --api http://localhost:9002
```

Existing databases need one migration:
`psql "$POSTGRES_URL" -f infra/db/migrations/002_provider_credentials_and_token_balance.sql`
(idempotent). Details in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## What is and isn't built out

This repo gives you a **correct, runnable core** with the load-bearing pieces fully
implemented: the Anthropic-compatible proxy with personal keys, per-organization
provider credentials (API key or OAuth, encrypted at rest, server-side refresh),
prepaid token balances with an immutable ledger, the atomic Redis quota engine
(Lua), JWT auth with refresh-token rotation, the provider abstraction with
failover, the RabbitMQ→ClickHouse analytics pipeline, multi-tenant isolation,
self-service registration, and the dashboards.

Some pieces are intentionally left as clearly-marked extension points rather than
half-finished: EF Core migrations (the schema is in `infra/db` + entities; run
`dotnet ef migrations add Initial`), the OpenAI provider (mirror `AnthropicProvider`),
the web login route that sets the dashboard cookie, and the billing consumer
(`usage.billing` queue is declared and bound, ready for a consumer). See
`docs/DEPLOYMENT.md` for the full checklist.

## Push this to your own GitHub

I can't push for you, but here's exactly how. Create an empty repo on GitHub first
(no README), then from the project root:

```bash
git init
git add .
git commit -m "Initial commit: enterprise AI gateway"
git branch -M main
git remote add origin https://github.com/<your-username>/<your-repo>.git
git push -u origin main
```

Before pushing, double-check `.env` is **not** staged (it's in `.gitignore`) so your
API key never lands in git history.

## Security notes

See [docs/SECURITY.md](docs/SECURITY.md). Highlights: per-organization provider
credentials AES-256-GCM encrypted under a dedicated data key and never returned
(hint only), personal keys stored hashed and revocable, atomic balance debits with
an immutable ledger, PBKDF2 constant-time password verification, refresh-token
rotation with theft detection, per-IP rate limits on auth endpoints, EF global
query filters for tenant isolation, and IP used strictly for anomaly detection.
The API trusts forwarded headers and must sit behind NGINX or the ingress.
