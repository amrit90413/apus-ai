-- Migration 002: per-organization provider credentials (API key or OAuth) and
-- prepaid token balances. Idempotent; safe to re-run. Apply to databases created
-- from postgres-init.sql before this change:
--   psql "$POSTGRES_URL" -f infra/db/migrations/002_provider_credentials_and_token_balance.sql

BEGIN;

-- provider_keys -> provider_credentials (existing rows become platform-wide api_key rows).
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'provider_keys')
     AND NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'provider_credentials') THEN
    ALTER TABLE provider_keys RENAME TO provider_credentials;
    ALTER TABLE provider_credentials RENAME COLUMN encrypted_key TO encrypted_secret;
    ALTER TABLE provider_credentials RENAME COLUMN key_hint TO hint;
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS provider_credentials (
    id                      uuid PRIMARY KEY,
    organization_id         uuid REFERENCES organizations(id),
    provider                text NOT NULL,
    kind                    int  NOT NULL DEFAULT 0,
    encrypted_secret        text NOT NULL,
    encrypted_refresh_token text,
    access_expires_at       timestamptz,
    scopes                  text,
    hint                    text NOT NULL,
    is_active               boolean NOT NULL DEFAULT true,
    created_by              uuid,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    last_refreshed_at       timestamptz,
    last_error              text
);

ALTER TABLE provider_credentials
    ADD COLUMN IF NOT EXISTS organization_id         uuid REFERENCES organizations(id),
    ADD COLUMN IF NOT EXISTS kind                    int  NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS encrypted_refresh_token text,
    ADD COLUMN IF NOT EXISTS access_expires_at       timestamptz,
    ADD COLUMN IF NOT EXISTS scopes                  text,
    ADD COLUMN IF NOT EXISTS created_by              uuid,
    ADD COLUMN IF NOT EXISTS updated_at              timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS last_refreshed_at       timestamptz,
    ADD COLUMN IF NOT EXISTS last_error              text;

CREATE INDEX IF NOT EXISTS ix_provider_credentials_org_provider
    ON provider_credentials(organization_id, provider, is_active);

ALTER TABLE memberships ADD COLUMN IF NOT EXISTS token_balance bigint;

CREATE TABLE IF NOT EXISTS token_ledger (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations(id),
    membership_id   uuid NOT NULL REFERENCES memberships(id),
    user_id         uuid NOT NULL,
    workspace_id    uuid NOT NULL,
    kind            int  NOT NULL,
    delta           bigint NOT NULL,
    balance_after   bigint,
    actor_user_id   uuid,
    reference       text,
    idempotency_key text,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_token_ledger_membership_at ON token_ledger(membership_id, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_token_ledger_idempotency
    ON token_ledger(organization_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS api_keys (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations(id),
    user_id         uuid NOT NULL REFERENCES users(id),
    workspace_id    uuid NOT NULL REFERENCES workspaces(id),
    name            text NOT NULL,
    key_hash        text NOT NULL UNIQUE,
    prefix          text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    last_used_at    timestamptz,
    expires_at      timestamptz,
    revoked_at      timestamptz
);
CREATE INDEX IF NOT EXISTS ix_api_keys_user ON api_keys(user_id);

COMMIT;
