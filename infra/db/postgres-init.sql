-- Reference schema (EF Core migrations are the source of truth in production).
CREATE TABLE organizations (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    slug        text UNIQUE NOT NULL,
    plan_code   text NOT NULL DEFAULT 'free',
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE workspaces (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    name              text NOT NULL,
    quota_policy_json jsonb,
    is_active         boolean NOT NULL DEFAULT true
);

CREATE TABLE users (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations(id),
    email           text NOT NULL,
    password_hash   text NOT NULL,
    phone_number    text,
    phone_verified  boolean NOT NULL DEFAULT false,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, email)
);

CREATE TABLE memberships (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations(id),
    user_id            uuid NOT NULL REFERENCES users(id),
    workspace_id       uuid NOT NULL REFERENCES workspaces(id),
    role               int  NOT NULL DEFAULT 0,
    per_user_quota_json jsonb,
    -- Prepaid token allowance. NULL = not enforced (rolling windows still apply).
    token_balance      bigint,
    UNIQUE (user_id, workspace_id)
);

CREATE TABLE sessions (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations(id),
    user_id            uuid NOT NULL REFERENCES users(id),
    workspace_id       uuid NOT NULL REFERENCES workspaces(id),
    device_name        text NOT NULL,
    device_fingerprint text NOT NULL,
    refresh_token_hash text NOT NULL,
    last_ip            text,
    created_at         timestamptz NOT NULL DEFAULT now(),
    revoked_at         timestamptz,
    expires_at         timestamptz NOT NULL
);
CREATE INDEX ix_sessions_refresh ON sessions(refresh_token_hash);

CREATE TABLE audit_logs (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL,
    user_id         uuid,
    action          text NOT NULL,
    detail          text,
    ip              text,
    at              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_org_at ON audit_logs(organization_id, at DESC);

-- AI provider credentials. organization_id NULL = platform-wide fallback managed by
-- super admins; otherwise owned (and managed) by that organization's admins.
-- kind: 0 = api_key, 1 = oauth. Secrets are AES-256-GCM encrypted (base64).
CREATE TABLE provider_credentials (
    id                      uuid PRIMARY KEY,
    organization_id         uuid REFERENCES organizations(id),
    provider                text NOT NULL,          -- 'anthropic', 'openai'
    kind                    int  NOT NULL DEFAULT 0,
    encrypted_secret        text NOT NULL,          -- API key or OAuth access token
    encrypted_refresh_token text,                   -- OAuth only
    access_expires_at       timestamptz,            -- OAuth only
    scopes                  text,
    hint                    text NOT NULL,          -- "...6789" / account label shown in UI
    is_active               boolean NOT NULL DEFAULT true,
    created_by              uuid,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    last_refreshed_at       timestamptz,
    last_error              text
);
CREATE INDEX ix_provider_credentials_org_provider
    ON provider_credentials(organization_id, provider, is_active);

-- Immutable token-balance history. kind: 0 grant, 1 set, 2 usage, 3 revoke.
CREATE TABLE token_ledger (
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
CREATE INDEX ix_token_ledger_membership_at ON token_ledger(membership_id, created_at DESC);
CREATE UNIQUE INDEX ux_token_ledger_idempotency
    ON token_ledger(organization_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

-- Personal keys for the Anthropic-compatible proxy (/v1/*). Only the hash is stored.
CREATE TABLE api_keys (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations(id),
    user_id         uuid NOT NULL REFERENCES users(id),
    workspace_id    uuid NOT NULL REFERENCES workspaces(id),
    name            text NOT NULL,
    key_hash        text NOT NULL UNIQUE,   -- SHA-256 hex
    prefix          text NOT NULL,          -- "apus_ab12cd3" shown in UI
    created_at      timestamptz NOT NULL DEFAULT now(),
    last_used_at    timestamptz,
    expires_at      timestamptz,
    revoked_at      timestamptz
);
CREATE INDEX ix_api_keys_user ON api_keys(user_id);
