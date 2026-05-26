-- Raw usage events. ReplacingMergeTree dedupes on EventId (idempotent consumer).
CREATE TABLE IF NOT EXISTS usage_events
(
    event_id        UUID,
    organization_id UUID,
    workspace_id    UUID,
    user_id         UUID,
    session_id      UUID,
    provider        LowCardinality(String),
    model           LowCardinality(String),
    input_tokens    UInt32,
    output_tokens   UInt32,
    total_tokens    UInt32,
    latency_ms      UInt32,
    cost_usd        Decimal(12, 6),
    occurred_at     DateTime64(3, 'UTC'),
    correlation_id  String,
    client_ip       String
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (organization_id, workspace_id, user_id, occurred_at, event_id)
TTL toDateTime(occurred_at) + INTERVAL 13 MONTH;

-- Pre-aggregated per-user hourly rollup powering dashboards without scanning raw rows.
CREATE MATERIALIZED VIEW IF NOT EXISTS usage_hourly_mv
ENGINE = SummingMergeTree
PARTITION BY toYYYYMM(hour)
ORDER BY (organization_id, workspace_id, user_id, model, hour)
AS SELECT
    organization_id, workspace_id, user_id, model,
    toStartOfHour(occurred_at) AS hour,
    sum(input_tokens)  AS input_tokens,
    sum(output_tokens) AS output_tokens,
    sum(total_tokens)  AS total_tokens,
    sum(cost_usd)      AS cost_usd,
    count()            AS requests
FROM usage_events
GROUP BY organization_id, workspace_id, user_id, model, hour;
