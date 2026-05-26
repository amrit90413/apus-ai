# Security model

## Provider key isolation
The single provider API key is read from a K8s secret by the gateway only. The CLI and
the browser never receive it. All provider HTTP calls happen server-side.

## Authentication
- Access tokens: short-lived (15 min) HS256 JWTs carrying user/org/workspace/session claims.
- Refresh tokens: 64 bytes of CSPRNG entropy, stored only as a SHA-256 hash, rotated on every
  refresh so a stolen token can't be reused after the next legitimate refresh.
- Passwords: PBKDF2-SHA256, 600k iterations, per-user salt, constant-time comparison.

## Quota ownership
Charged to (UserId, WorkspaceId). IP is recorded only on sessions/audit logs for anomaly
detection — never used as a quota key, so VPNs and shared offices don't cause false limits.

## Multi-tenant isolation
EF Core global query filters scope every tenant-owned query to the caller's organization.
A super admin runs with no scope for cross-tenant views. Login/refresh use
`IgnoreQueryFilters` deliberately because there's no org context yet at that point.

## Defense in depth
- Edge rate limiting in NGINX in addition to the Redis quota engine.
- Polly circuit breaker prevents hammering a failing provider.
- Containers run as non-root with health checks.
- Audit log records login, failed login, quota blocks, and anomalies.
