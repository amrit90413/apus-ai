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
