# apus-ai Gateway

An Anthropic-compatible AI gateway that lets an organization's engineers use
Claude Code, the VS Code Claude extension, Cline and Roo Code through one
controlled endpoint — so the organization can **connect its own provider accounts
once, give each person a monthly budget and a model list, track every request and
what it cost, and keep provider secrets server-side**.

Providers: Anthropic, OpenAI, Google Gemini, AWS Bedrock and Google Vertex AI.
A client that speaks the Anthropic Messages API reaches any of them — the gateway
translates.

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

## Per-organization connections (read this first)

Each organization connects **its own** provider accounts; the platform can hold a
fallback. The gateway fans those credentials out to many people safely:

- An org admin connects a provider at `/settings/ai-providers` — an API key, AWS or
  Google Cloud credentials, or a browser login where the operator has an OAuth client
  the organization owns. Secrets are AES-256-GCM encrypted under a versioned data key
  and are never sent to the CLI or the browser; only a hint is.
  See [docs/PROVIDERS.md](docs/PROVIDERS.md).
- Each person gets a personal `apus_...` key (stored hashed) that carries their
  `UserId` + `WorkspaceId`; dashboards and the CLI use short-lived JWTs.
- Admins give each person a **monthly budget** in the organization's currency, plus
  optional token balances, model and provider allowlists, and RPM/TPM/concurrency
  limits. Budgets are held per calendar period, reserved worst-case before a call and
  settled to the real cost after, so concurrent requests cannot collectively overspend.
- Every request appends an immutable ledger row carrying **both** what the provider
  charged and what the tenant is charged, so margin, spend and billing reconciliation
  all come from one table.
- Quotas are charged against that identity, **never against IP**. IP is recorded only for
  anomaly detection and audit logs (people may be on VPNs, shared offices, etc.).
- Redis enforces token windows, hierarchical rate limits and concurrency atomically, so
  concurrent requests can't race past a limit.

## Proxy endpoints

| Endpoint | Auth | Notes |
| --- | --- | --- |
| `POST /v1/messages` | `x-api-key: apus_...` or `Authorization: Bearer apus_...` | Anthropic Messages API, streamed or not, against any connected provider; `[1m]` model suffix stripped; extra headers `X-Allowance-Remaining`, `X-Quota-Remaining`, `X-Quota-Reset-Seconds`, `X-Balance-Remaining`, `X-Apus-Provider` |
| `POST /v1/messages/count_tokens` | same | Passthrough, not charged |
| `GET /v1/models` | same | Only the caller's allocated models |

Current Claude model ids: `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`.
Also routable: `gpt-4o`, `gpt-4.1`, `gemini-2.5-pro`, `gemini-2.5-flash`. Pin a
specific route with a prefix, e.g. `bedrock/claude-sonnet-5`.

Errors carry a stable `error.code` — `PROVIDER_NOT_CONNECTED`,
`USER_ALLOWANCE_EXCEEDED`, `MODEL_NOT_ALLOWED`, `RATE_LIMIT_EXCEEDED` and friends — so
a script can branch on it.

## Roles

| Role | Sees | Typical action |
| --- | --- | --- |
| User | Own usage, balance, keys, sessions | Run `npx apus-ai`, check remaining tokens |
| Workspace admin | Per-person usage in their workspace | Adjust the workspace quota window |
| Org admin | All workspaces in the org | Connect providers, create users, set budgets and models |
| AI admin | Provider connections, allowances, model access | Run AI operations without billing rights |
| Billing admin | Budgets, pricing, spend | Own the money without provider rights |
| Viewer | Usage and dashboards, read-only | Audit |
| Super admin | All organizations (cross-tenant) | Platform health, fallback credential, anomalies, billing |

Endpoints authorize on named permissions (`Provider.Connect`, `Allowance.Update`,
`Usage.ViewTenant`, …), not role names, so adding a role is a change to one table.

Pages: `/register` (self-service org signup, when `REGISTRATION_ENABLED=true`),
`/login`, `/usage`, `/admin/ai`, `/settings/ai-providers`, `/settings/team`,
`/admin/users`, `/admin/team`, `/super-admin`.
Admin walkthrough: [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md).
Connecting providers: [docs/PROVIDERS.md](docs/PROVIDERS.md).

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

Existing databases need the migrations, in order and idempotent:

```bash
psql "$POSTGRES_URL" -f infra/db/migrations/002_provider_credentials_and_token_balance.sql
psql "$POSTGRES_URL" -f infra/db/migrations/003_provider_connections_and_allowances.sql
```

Details in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## What is and isn't built out

Fully implemented: the Anthropic-compatible proxy with personal keys; provider
connections for five providers with an explicit state machine, versioned encryption at
rest and background rotation; OAuth with PKCE, single-use state and browser binding;
the single enforcement pipeline; currency allowances with reservation accounting;
hierarchical rate limiting and concurrency; the immutable AI usage ledger with
provider-vs-customer cost and versioned pricing; permission-based RBAC; the audit
trail; background workers for health, refresh, period rollover and notifications;
multi-tenant isolation; the dashboards and the CLI.

Extension points left deliberately open rather than half-finished:

- **EF Core migrations.** The schema is raw SQL in `infra/db` plus the entities; run
  `dotnet ef migrations add Initial` if you want EF to own it.
- **Notification channels.** The outbox and threshold engine are complete and deliver
  over the WhatsApp gateway already used for admin OTPs. Email, Slack or a webhook is a
  branch in `NotificationWorker.DeliverAsync`, not a new pipeline.
- **The billing consumer.** `usage.billing` is declared and bound, ready for a consumer;
  the ledger it would read is already written.
- **Provider fallback** is implemented and behind `AI_PROVIDER_FALLBACK`, off by
  default — turn it on per tenant once your model equivalences are agreed.

See `docs/DEPLOYMENT.md` for the full checklist.

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

See [docs/SECURITY.md](docs/SECURITY.md). Highlights: provider secrets AES-256-GCM
encrypted under versioned data keys and never returned (hint only); OAuth with PKCE,
single-use server-side state and a browser-bound flow cookie; an https allowlist per
provider so a tenant-supplied endpoint cannot point the gateway at an internal address;
personal keys stored hashed and revocable; atomic allowance and balance accounting with
immutable ledgers; PBKDF2 constant-time password verification; refresh-token rotation;
per-IP rate limits on auth endpoints; EF global query filters for tenant isolation with
tests that attempt cross-tenant access; permission-based authorization enforced in the
backend; security headers; and audit records with secret fields redacted on the way in.
IP is used strictly for anomaly detection. The API trusts forwarded headers and must sit
behind NGINX or the ingress.
