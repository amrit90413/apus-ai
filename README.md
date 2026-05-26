# YourCompany AI Gateway

An enterprise AI gateway that lets employees use Claude/OpenAI through a single,
controlled backend — so the organization can **track every employee's token usage,
enforce fair-usage quotas, and keep its one provider API key server-side**.

Employees run a CLI instead of going to the AI vendor websites directly:

```bash
npx yourcompany-ai login
npx yourcompany-ai chat
npx yourcompany-ai usage
```

Every request flows: **CLI → NGINX → Gateway API → JWT + workspace check → Redis quota
engine → AI provider → reconcile tokens → RabbitMQ → ClickHouse analytics → dashboards.**

## The single-account model (read this first)

You have **one provider account / API key**. This system fans that one key out to many
employees safely:

- The key lives only in a Kubernetes secret read by the gateway (`Anthropic__ApiKey`).
  It is never sent to the CLI or the browser.
- Each employee authenticates and receives a short-lived JWT carrying their
  `UserId` + `WorkspaceId` + `SessionId`.
- Quotas are charged against that identity, **never against IP**. IP is recorded only for
  anomaly detection and audit logs (employees may be on VPNs, shared offices, etc.).
- Redis enforces per-user and per-workspace token windows atomically, so concurrent
  requests can't race past a limit.

## Roles

| Role | Sees | Typical action |
| --- | --- | --- |
| User | Own usage, quota, sessions | Run the CLI, check remaining tokens |
| Workspace admin | Per-employee usage in their workspace | Adjust the workspace quota window |
| Org admin | All workspaces in the org | Manage members, models, quotas |
| Super admin | All organizations (cross-tenant) | Platform health, anomalies, billing |

The dashboards for these live at `/usage`, `/admin`, and `/super-admin`.

## Repository layout

```
backend/      ASP.NET Core 9 gateway + analytics worker (Clean-ish layering)
  src/Gateway.Api/        Auth, Quota, Providers, Gateway, Messaging, Persistence, Domain
  src/Analytics.Worker/   RabbitMQ -> ClickHouse consumer
cli/          Node.js + TypeScript CLI (Commander.js), published as `yourcompany-ai`
frontend/     Next.js dashboards (super-admin / admin / usage)
infra/        nginx config, db init SQL, Kubernetes manifests
.github/      CI/CD pipeline
docs/         architecture + deployment + security notes
```

## Run it locally

Requires Docker + Docker Compose.

```bash
cp .env.example .env        # then fill in ANTHROPIC_API_KEY and the passwords
docker compose up --build
```

This starts Postgres, Redis, RabbitMQ, ClickHouse, the gateway, the analytics worker,
the Next.js dashboards, and NGINX. The stack is reachable at `http://localhost:8080`.

Point the CLI at your local gateway:

```bash
cd cli && npm install && npm run build
YOURCOMPANY_AI_API=http://localhost:8080 node dist/index.js login
```

## What is and isn't built out

This repo gives you a **correct, runnable core** with the load-bearing pieces fully
implemented: the atomic Redis quota engine (Lua), the enforced request flow, JWT auth with
refresh-token rotation, the provider abstraction with failover, the RabbitMQ→ClickHouse
analytics pipeline, multi-tenant isolation, and the three dashboards.

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

See `docs/SECURITY.md`. Highlights: provider keys server-side only, PBKDF2 constant-time
password verification, refresh-token rotation with theft detection, EF global query filters
for tenant isolation, and IP used strictly for anomaly detection.
