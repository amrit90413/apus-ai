# Architecture

## Request flow (enforced path)

1. An editor (Claude Code, Cline, Roo Code) calls `POST /v1/messages` with the
   user's personal `apus_...` key (`x-api-key` or `Authorization: Bearer`); the
   dashboard and CLI call `/api/v1/*` with a JWT instead.
2. NGINX (or the K8s ingress) forwards to the gateway. SSE buffering is OFF so streamed
   tokens reach the CLI immediately.
3. The gateway validates the key (SHA-256 lookup, 30 s cache) or the JWT, then
   `TenantResolutionMiddleware` sets the org scope for EF global query filters
   (super admin = no scope = cross-tenant).
4. `QuotaPolicyResolver` resolves the effective windows + allowed models for this
   (user, workspace), cached ~30s so the hot path rarely hits Postgres.
5. `TokenBalanceService.ReserveAsync` debits the estimate from the user's prepaid
   balance in one conditional Postgres `UPDATE` (a `NULL` balance means unlimited),
   then `QuotaEngine.ReserveAsync` runs `quota_check.lua` — an atomic
   check-and-increment across every user and workspace window in one round trip.
   No read-then-write race in either store.
6. If allowed, the gateway resolves the organization's Claude credential
   (`ProviderCredentialService`: org-owned → platform-wide → env, decrypted and
   cached 60 s, OAuth refreshed under a Redis lock) and forwards the request
   verbatim, streaming the response back. The legacy `/api/v1/chat/stream` path
   goes through `ProviderRouter` with Polly retry/circuit-breaker and failover.
7. After the stream completes, `ReconcileAsync` adjusts each counter from the reserved
   estimate to the real token count (`quota_reconcile.lua`, clamped at zero) and
   the balance to the real usage, appending a `token_ledger` row with the
   correlation id.
8. A `UsageEvent` is published to RabbitMQ (`usage.recorded`). No synchronous DB write
   sits in the request path.
9. The analytics worker batch-inserts events into ClickHouse (ReplacingMergeTree dedupes
   on `EventId`). A materialized view maintains hourly rollups for dashboards.

## Why estimate-then-reconcile

A burst of concurrent requests could each read "quota OK" before any of them increments.
Reserving an estimate up front (inside the atomic Lua script) closes that race; reconciling
afterward keeps the counter accurate to the provider's real usage numbers.

## Storage split

- PostgreSQL: source of truth for orgs, workspaces, users, memberships (with the
  prepaid `token_balance`), personal keys, encrypted provider credentials, the
  token ledger, sessions, audit. Strongly consistent, transactional.
- Redis: real-time quota counters and rate limits with TTL-based window resets.
- ClickHouse: high-volume append-only usage logs + analytics aggregates.

## Scaling

The gateway is stateless — all shared state is in Redis/Postgres — so it scales
horizontally (HPA on CPU, min 3 / max 20 replicas). The analytics worker scales as
competing consumers on the durable queue.
