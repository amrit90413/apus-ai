-- Migration 003: provider connections (multi-provider, explicit state machine),
-- currency allowance periods, the AI usage ledger, versioned provider pricing,
-- richer audit records and the notification outbox.
--
-- Idempotent and backward compatible: every column is added nullable or with a
-- default, no column is dropped or retyped, and the gateway keeps running against
-- a database that has only migration 002 applied (new columns simply read as
-- their defaults). Apply with:
--   psql "$POSTGRES_URL" -f infra/db/migrations/003_provider_connections_and_allowances.sql
--
-- The one data change: where an organization has several active credentials for the
-- same provider and purpose, all but the newest are marked disabled. Credential
-- resolution has always picked the newest row, so the others were already dead
-- weight; this makes that explicit and lets the unique index below exist.

BEGIN;

-- ---------------------------------------------------------------- organizations

ALTER TABLE organizations
    ADD COLUMN IF NOT EXISTS currency              text   NOT NULL DEFAULT 'USD',
    -- Units of `currency` per 1 USD. NULL = use the platform rate table (Billing:UsdRates).
    ADD COLUMN IF NOT EXISTS usd_rate              numeric(18,6),
    -- Customer price = provider cost * (1 + markup_bps / 10000). 0 = bill at cost.
    ADD COLUMN IF NOT EXISTS markup_bps            int    NOT NULL DEFAULT 0,
    -- Monthly organization budget in minor units (paise/cents). NULL = unlimited.
    ADD COLUMN IF NOT EXISTS monthly_budget_minor  bigint,
    ADD COLUMN IF NOT EXISTS ai_enabled            boolean NOT NULL DEFAULT true,
    -- Tenant-level provider/model restrictions: { "allowedProviders": [], "allowedModels": [] }
    ADD COLUMN IF NOT EXISTS ai_policy_json        jsonb;

-- ------------------------------------------------------------------ memberships

ALTER TABLE memberships
    -- Monthly AI allowance in minor units. NULL = inherit the workspace/org default.
    ADD COLUMN IF NOT EXISTS monthly_allowance_minor bigint,
    ADD COLUMN IF NOT EXISTS daily_allowance_minor   bigint,
    ADD COLUMN IF NOT EXISTS unlimited_allowance     boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS rpm_limit               int,
    ADD COLUMN IF NOT EXISTS tpm_limit               int,
    ADD COLUMN IF NOT EXISTS concurrency_limit       int,
    ADD COLUMN IF NOT EXISTS daily_request_limit     int,
    -- 0 active, 1 suspended (admin hold), 2 disabled (no AI access)
    ADD COLUMN IF NOT EXISTS ai_status               int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS access_expires_at       timestamptz,
    -- ["anthropic","openai"] — NULL means every provider the tenant allows.
    ADD COLUMN IF NOT EXISTS allowed_providers_json  jsonb;

-- ---------------------------------------------------- provider connections

-- provider_credentials IS the connection table; 003 gives it the full model.
ALTER TABLE provider_credentials
    -- 0 api_key, 1 oauth, 2 aws_bedrock, 3 google_vertex
    ADD COLUMN IF NOT EXISTS connection_type         int  NOT NULL DEFAULT 0,
    -- 0 disconnected, 1 connecting, 2 connected, 3 refreshing, 4 expired,
    -- 5 reauthentication_required, 6 revoked, 7 disabled, 8 error
    ADD COLUMN IF NOT EXISTS status                  int  NOT NULL DEFAULT 2,
    -- Lets one tenant hold several connections to the same provider for different
    -- uses (e.g. 'default' and 'eu-residency') without losing the unique guarantee.
    ADD COLUMN IF NOT EXISTS connection_purpose      text NOT NULL DEFAULT 'default',
    ADD COLUMN IF NOT EXISTS display_name            text,
    ADD COLUMN IF NOT EXISTS provider_account_id     text,
    ADD COLUMN IF NOT EXISTS provider_organization_id text,
    ADD COLUMN IF NOT EXISTS provider_workspace_id   text,
    -- Second encrypted blob for credentials that are not a single string
    -- (AWS secret access key + session token, GCP service-account JSON).
    ADD COLUMN IF NOT EXISTS encrypted_config        text,
    -- Non-secret connection settings: region, project, location, base url overrides.
    ADD COLUMN IF NOT EXISTS config_json             jsonb,
    ADD COLUMN IF NOT EXISTS encryption_key_version  int  NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS refresh_expires_at      timestamptz,
    ADD COLUMN IF NOT EXISTS last_validated_at       timestamptz,
    ADD COLUMN IF NOT EXISTS failure_count           int  NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_failure_at         timestamptz,
    ADD COLUMN IF NOT EXISTS last_failure_reason     text,
    ADD COLUMN IF NOT EXISTS revoked_at              timestamptz;

-- Backfill status from the legacy is_active flag on first run.
UPDATE provider_credentials SET status = 7 WHERE is_active = false AND status = 2;

-- Only the newest connection per (org, provider, purpose) stays live; older ones
-- were already ignored by resolution (it orders by created_at DESC).
WITH ranked AS (
    SELECT id,
           row_number() OVER (
               PARTITION BY coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid),
                            provider, connection_purpose
               ORDER BY created_at DESC, id DESC) AS rn
    FROM provider_credentials
    WHERE is_active = true AND status NOT IN (0, 6, 7)
)
UPDATE provider_credentials p
   SET status = 7, is_active = false,
       last_failure_reason = coalesce(p.last_failure_reason, 'superseded by a newer connection (migration 003)')
  FROM ranked r
 WHERE p.id = r.id AND r.rn > 1;

-- One live connection per tenant + provider + purpose. Platform-wide rows share the
-- zero uuid so they are constrained too (NULLs would otherwise all be distinct).
CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_connections_live
    ON provider_credentials (
        coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid),
        provider, connection_purpose)
    WHERE status NOT IN (0, 6, 7);

CREATE INDEX IF NOT EXISTS ix_provider_connections_status
    ON provider_credentials (status, provider);
CREATE INDEX IF NOT EXISTS ix_provider_connections_refresh
    ON provider_credentials (access_expires_at)
    WHERE connection_type = 1 AND status IN (2, 3, 4);

-- ------------------------------------------------------------ allowance periods

-- One row per (owner, calendar period). Historical periods are never mutated after
-- their end, so "reset" means opening the next period, not zeroing a counter.
CREATE TABLE IF NOT EXISTS allowance_periods (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations(id),
    -- 0 organization, 1 user (membership)
    scope              int  NOT NULL,
    membership_id      uuid,
    user_id            uuid,
    workspace_id       uuid,
    currency           text NOT NULL,
    period_start       timestamptz NOT NULL,
    period_end         timestamptz NOT NULL,
    allocated_minor    bigint  NOT NULL DEFAULT 0,
    unlimited          boolean NOT NULL DEFAULT false,
    consumed_minor     bigint  NOT NULL DEFAULT 0,
    reserved_minor     bigint  NOT NULL DEFAULT 0,
    adjustment_minor   bigint  NOT NULL DEFAULT 0,
    request_count      bigint  NOT NULL DEFAULT 0,
    token_count        bigint  NOT NULL DEFAULT 0,
    -- Bitmask of thresholds already notified: 1=50%, 2=75%, 4=90%, 8=100%.
    notified_thresholds int NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_allowance_period_owner
    ON allowance_periods (
        organization_id, scope,
        coalesce(membership_id, '00000000-0000-0000-0000-000000000000'::uuid),
        period_start);
CREATE INDEX IF NOT EXISTS ix_allowance_period_org_start
    ON allowance_periods (organization_id, period_start DESC);
CREATE INDEX IF NOT EXISTS ix_allowance_period_membership
    ON allowance_periods (membership_id, period_start DESC);

-- ------------------------------------------------------------ provider pricing

-- Versioned price list. Historical cost stays reproducible because a ledger row
-- records the pricing row it was computed from.
CREATE TABLE IF NOT EXISTS provider_model_pricing (
    id                     uuid PRIMARY KEY,
    provider               text NOT NULL,
    model                  text NOT NULL,
    currency               text NOT NULL DEFAULT 'USD',
    input_per_mtok         numeric(18,6) NOT NULL,
    output_per_mtok        numeric(18,6) NOT NULL,
    cached_input_per_mtok  numeric(18,6) NOT NULL DEFAULT 0,
    cached_write_per_mtok  numeric(18,6) NOT NULL DEFAULT 0,
    effective_from         timestamptz NOT NULL,
    effective_to           timestamptz,
    source                 text,
    created_at             timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pricing_provider_model_from
    ON provider_model_pricing (provider, model, effective_from);
CREATE INDEX IF NOT EXISTS ix_pricing_lookup
    ON provider_model_pricing (provider, model, effective_from DESC);

-- --------------------------------------------------------------- usage ledger

-- Append-only. Corrections are new rows (status = 4 adjustment), never updates.
CREATE TABLE IF NOT EXISTS ai_usage_ledger (
    id                      bigserial PRIMARY KEY,
    request_id              text NOT NULL,
    organization_id         uuid NOT NULL,
    workspace_id            uuid NOT NULL,
    user_id                 uuid NOT NULL,
    provider                text NOT NULL,
    model                   text NOT NULL,
    provider_connection_id  uuid,
    input_tokens            int NOT NULL DEFAULT 0,
    output_tokens           int NOT NULL DEFAULT 0,
    cached_input_tokens     int NOT NULL DEFAULT 0,
    cache_write_tokens      int NOT NULL DEFAULT 0,
    -- What the provider charges us, and what we charge the tenant. Both in the
    -- tenant's currency, minor units. Never collapse the two.
    provider_cost_minor     bigint NOT NULL DEFAULT 0,
    customer_cost_minor     bigint NOT NULL DEFAULT 0,
    currency                text NOT NULL,
    pricing_id              uuid,
    latency_ms              int NOT NULL DEFAULT 0,
    started_at              timestamptz NOT NULL,
    completed_at            timestamptz NOT NULL,
    -- 0 succeeded, 1 failed, 2 blocked, 3 cancelled, 4 adjustment
    status                  int NOT NULL,
    http_status             int,
    failure_category        text,
    -- Set when a fallback provider served the request; names the original provider.
    fallback_from           text,
    billing_period          date NOT NULL,
    metadata_json           jsonb,
    created_at              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_usage_ledger_org_at   ON ai_usage_ledger (organization_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_usage_ledger_user_at  ON ai_usage_ledger (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_usage_ledger_prov_at  ON ai_usage_ledger (provider, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_usage_ledger_request  ON ai_usage_ledger (request_id);
CREATE INDEX IF NOT EXISTS ix_usage_ledger_period   ON ai_usage_ledger (organization_id, billing_period);

-- ------------------------------------------------------------------- audit log

ALTER TABLE audit_logs
    ADD COLUMN IF NOT EXISTS actor_email    text,
    ADD COLUMN IF NOT EXISTS resource_type  text,
    ADD COLUMN IF NOT EXISTS resource_id    text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS user_agent     text,
    ADD COLUMN IF NOT EXISTS before_json    jsonb,
    ADD COLUMN IF NOT EXISTS after_json     jsonb;

CREATE INDEX IF NOT EXISTS ix_audit_resource ON audit_logs (organization_id, resource_type, resource_id);

-- ---------------------------------------------------------- notification outbox

CREATE TABLE IF NOT EXISTS notification_outbox (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL,
    user_id         uuid,
    kind            text NOT NULL,       -- allowance_threshold, connection_reauth, ...
    severity        text NOT NULL DEFAULT 'info',
    subject         text NOT NULL,
    body            text NOT NULL,
    payload_json    jsonb,
    dedupe_key      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    delivered_at    timestamptz,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_dedupe
    ON notification_outbox (organization_id, dedupe_key) WHERE dedupe_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_notification_pending
    ON notification_outbox (created_at) WHERE delivered_at IS NULL;

COMMIT;
