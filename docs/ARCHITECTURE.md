# Architecture

## Shape

```
        Admin ──► Web dashboard ──► Provider connection ──► Credential vault
                                         (Anthropic, OpenAI, Gemini,        (AES-256-GCM,
                                          Bedrock, Vertex)                   versioned keys)
                                                   │
        Members ─────────────────────────────► APUS AI Gateway ◄────────────┘
        (APUS token only)                     authn · authz · tenant isolation
                                              allowance · quota · rate limit
                                              routing · fallback · usage · cost
                                              audit
```

A member authenticates to APUS and nothing else. The organization's provider
credential lives server-side, encrypted, and is resolved per request from the caller's
tenant — it never reaches a browser, a CLI, or a log.

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
6. If allowed, the gateway resolves the organization's connection for the chosen
   provider (`ProviderConnectionService`: org-owned → platform-wide → env, decrypted
   and cached 60 s, OAuth refreshed under a Redis lock) and calls it through that
   provider's upstream adapter. Anthropic is forwarded verbatim; OpenAI and Gemini are
   translated to and from the Anthropic Messages protocol; Bedrock's event-stream
   framing is re-framed as SSE. The legacy `/api/v1/chat/stream` path goes through
   `ProviderRouter` with Polly retry/circuit-breaker and failover.
7. After the stream completes, `ReconcileAsync` adjusts each counter from the reserved
   estimate to the real token count (`quota_reconcile.lua`, clamped at zero) and
   the balance to the real usage, appending a `token_ledger` row with the
   correlation id.
8. A `UsageEvent` is published to RabbitMQ (`usage.recorded`). No synchronous DB write
   sits in the request path.
9. The analytics worker batch-inserts events into ClickHouse (ReplacingMergeTree dedupes
   on `EventId`). A materialized view maintains hourly rollups for dashboards.

## The enforcement pipeline

Every AI request goes through `GatewayPipeline` in one fixed order:

```
authenticate → tenant → membership → member status → subscription → model →
provider policy → provider connection → rate limits (platform/tenant/provider/
user/model) → concurrency → token windows → prepaid tokens → cost estimate →
allowance reservation → provider call → real usage → settle → ledger
```

Controllers call `Admit` and `Settle`; they never re-implement a check. A rule added
here applies to every surface at once, and two endpoints cannot drift into enforcing
different things. Anything a refused request had already taken — a rate-limit
increment, a concurrency slot, a window reservation, an allowance hold — is released
in reverse before the error is returned.

## Money

Currency allowances are separate from the token quotas above and live in Postgres,
because money must survive a cache flush.

- Amounts are integer **minor units** (paise, cents) plus a currency. No floats.
- A period is a calendar month, one row per owner. A "reset" opens the next period; it
  never zeroes a counter, so history stays queryable and an in-flight request cannot be
  silently re-credited.
- A request reserves its **worst-case** cost in a single conditional `UPDATE` — the row
  is the lock — then settles to the real cost, releasing what it over-held. Fifty
  concurrent requests can only hold fifty worst cases, never fifty best cases.
- `provider_cost` (what the upstream bills) and `customer_cost` (what APUS charges) are
  separate columns on every ledger row. Collapsing them destroys margin and
  reconciliation reporting.
- Prices are versioned; a ledger row records the price version it used, so a past
  month's cost stays reproducible after a price change.

## Why estimate-then-reconcile

A burst of concurrent requests could each read "quota OK" before any of them increments.
Reserving an estimate up front (inside the atomic Lua script) closes that race; reconciling
afterward keeps the counter accurate to the provider's real usage numbers.

## Storage split

- PostgreSQL: source of truth for orgs, workspaces, users, memberships, allowance
  periods, the AI usage ledger, provider connections (encrypted), provider pricing,
  personal keys, the token ledger, sessions, audit, notifications. Strongly consistent,
  transactional.
- Redis: real-time quota counters, hierarchical rate limits, concurrency slots,
  credential-refresh locks, OAuth flow state. All TTL-bounded.
- ClickHouse: high-volume append-only usage logs + analytics aggregates.

The AI usage ledger is in Postgres, not ClickHouse, because it is what billing
reconciles against. ClickHouse is the analytics copy; losing it costs reporting, not
correctness — which is why an unreachable RabbitMQ degrades the gateway instead of
stopping it.

## Background workers

Expensive work is never done on the request path. In-process hosted services handle
provider health checks and pre-expiry token refresh, allowance period rollover and
abandoned-reservation sweeps, threshold notifications, and re-sealing stored secrets
after a key rotation.

## Scaling

The gateway is stateless — all shared state is in Redis/Postgres — so it scales
horizontally (HPA on CPU, min 3 / max 20 replicas). The analytics worker scales as
competing consumers on the durable queue.
