-- Migration 003: recurring monthly token allowance per (user, workspace).
-- Idempotent; safe to re-run.
--   psql "$POSTGRES_URL" -f infra/db/migrations/003_token_allowance.sql
--
-- An allowance tops the prepaid balance up once per calendar month (UTC). The
-- top-up is applied lazily on the first balance touch of a new period rather than
-- by a scheduler: the gateway is stateless and horizontally scaled, so a timer
-- would need leader election and a missed run would strand users. Concurrency is
-- handled by the membership row lock plus ux_token_ledger_idempotency.

BEGIN;

ALTER TABLE memberships
    -- Tokens credited each period. NULL = no recurring allowance (a one-off
    -- balance set by an admin still works exactly as before).
    ADD COLUMN IF NOT EXISTS allowance_tokens     bigint,
    -- true: add to whatever is left. false: reset to allowance_tokens.
    ADD COLUMN IF NOT EXISTS allowance_rollover   boolean NOT NULL DEFAULT false,
    -- Last period credited, as 'YYYY-MM'. NULL = never credited.
    ADD COLUMN IF NOT EXISTS allowance_period_key text;

-- Finds memberships still awaiting this period's top-up. Partial: rows without an
-- allowance are the overwhelming majority and never need scanning.
CREATE INDEX IF NOT EXISTS ix_memberships_allowance_due
    ON memberships(allowance_period_key)
    WHERE allowance_tokens IS NOT NULL;

COMMIT;
