# Architecture

## Request flow (enforced path)

1. Employee runs the CLI. It attaches the stored JWT as a Bearer token.
2. NGINX (or the K8s ingress) forwards to the gateway. SSE buffering is OFF so streamed
   tokens reach the CLI immediately.
3. The gateway validates the JWT, then `TenantResolutionMiddleware` sets the org scope for
   EF global query filters (super admin = no scope = cross-tenant).
4. `QuotaPolicyResolver` resolves the effective windows + allowed models for this
   (user, workspace), cached ~30s so the hot path rarely hits Postgres.
5. `QuotaEngine.ReserveAsync` runs `quota_check.lua` — an atomic check-and-increment across
   every user and workspace window in one round trip. No read-then-write race.
6. If allowed, the gateway streams from the provider via `ProviderRouter`, which applies
   Polly retry/timeout/circuit-breaker policies and provider failover (only before the
   stream has started emitting).
7. After the stream completes, `ReconcileAsync` adjusts each counter from the reserved
   estimate to the real token count (`quota_reconcile.lua`, clamped at zero).
8. A `UsageEvent` is published to RabbitMQ (`usage.recorded`). No synchronous DB write
   sits in the request path.
9. The analytics worker batch-inserts events into ClickHouse (ReplacingMergeTree dedupes
   on `EventId`). A materialized view maintains hourly rollups for dashboards.

## Why estimate-then-reconcile

A burst of concurrent requests could each read "quota OK" before any of them increments.
Reserving an estimate up front (inside the atomic Lua script) closes that race; reconciling
afterward keeps the counter accurate to the provider's real usage numbers.

## Storage split

- PostgreSQL: source of truth for orgs, workspaces, users, memberships, sessions, audit.
  Strongly consistent, transactional.
- Redis: real-time quota counters and rate limits with TTL-based window resets.
- ClickHouse: high-volume append-only usage logs + analytics aggregates.

## Scaling

The gateway is stateless — all shared state is in Redis/Postgres — so it scales
horizontally (HPA on CPU, min 3 / max 20 replicas). The analytics worker scales as
competing consumers on the durable queue.
