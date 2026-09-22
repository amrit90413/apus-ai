# Security model

## Provider credential isolation
Each organization connects its own Claude credential (API key or OAuth grant) at
`/admin/providers`; a platform-wide credential managed by the super admin and the
`Anthropic__ApiKey` environment value are fallbacks. All of them are used only
server-side: the CLI, the editor clients and the browser never receive a provider
secret, and every provider HTTP call originates from the gateway.

At rest, credentials live in `provider_credentials` encrypted with AES-256-GCM
(`SecretProtector`): a random 96-bit nonce per value, blob = nonce ‖ tag ‖
ciphertext, so a tampered row fails authentication instead of decrypting to
garbage. The key is `Encryption:DataKey` (32 bytes, base64; `openssl rand -base64 32`).
When unset, the key is derived from `Jwt:SigningKey` for backward compatibility
and the gateway warns at startup — set a dedicated key in production so a JWT
rotation cannot brick stored credentials.

The API never returns a stored secret. List and detail responses carry only a
`hint` (last four characters for API keys, `OAuth · <admin email>` for grants).
Decrypted credentials are cached in process memory for at most 60 seconds and
dropped immediately when the provider answers 401/403.

OAuth access tokens are refreshed server-side before expiry. A refresh runs under
an in-process gate and a per-credential Redis lock (`lock:cred-refresh:{id}`,
30 s TTL, compare-and-delete release) so replicas never race the same refresh
token; a pod that loses the lock re-reads the row instead of refreshing again.
A rejected grant (`invalid_grant`) deactivates the credential and records
`lastError`; it is never retried silently. The authorization flow uses PKCE
(S256), a single-use `state` stored in Redis for 10 minutes, and the callback
must be completed by the same admin, in the same organization, that started it.

## Authentication
- Dashboard / CLI API: short-lived (15 min) HS256 JWTs carrying
  user/org/workspace/session claims.
- Refresh tokens: 64 bytes of CSPRNG entropy, stored only as a SHA-256 hash,
  rotated on every refresh so a stolen token can't be reused after the next
  legitimate refresh.
- Personal proxy keys (`apus_...`, 32 random bytes): stored as a SHA-256 hash
  plus a 12-character display prefix; the plaintext is returned exactly once at
  creation. Accepted on `/v1/*` as `x-api-key` or `Authorization: Bearer`.
  Revocable by the user (`DELETE /api/v1/me/keys/{id}`) and by an org admin
  (`DELETE /api/v1/admin/users/{id}/keys/{keyId}`); keys of a deactivated user
  are rejected. Lookups are cached in process memory for 30 seconds (misses
  too, so a flood of bad keys cannot hammer Postgres), which bounds revocation
  latency at 30 s per API pod. `lastUsedAt` is updated at most once a minute per
  key. Creation and revocation are audited as `api_key_created`,
  `api_key_revoked` and `api_key_revoked_by_admin`.
- Passwords: PBKDF2-SHA256, 600k iterations, per-user salt, constant-time
  comparison. Self-service registration enforces a 12-character minimum and
  compares the invite code in constant time.
- Admin logins are second-factored with a WhatsApp OTP unless
  `WHATSAPP_ENABLED=false`, in which case each admin login is written to the
  audit log as `admin_login_without_otp`.

## Rate limiting on public endpoints
`POST /api/v1/auth/login`, `verify-otp` and `register` share a fixed-window
limiter of 10 requests per minute per client IP (429, no queueing). NGINX adds
30 r/s per IP on `/api/` as a second layer. `/v1/messages` is not edge-limited;
the per-user quota engine and balance are the control there.

## Quota and balance ownership
Charged to (UserId, WorkspaceId) — from the JWT or the personal key — never to
an IP. IP is recorded only on sessions/audit logs for anomaly detection, so VPNs
and shared offices don't cause false limits.

Rolling windows are enforced in Redis by an atomic Lua check-and-increment.
Prepaid balances are enforced in Postgres: the reservation is one conditional
`UPDATE memberships ... WHERE token_balance IS NULL OR token_balance >= estimate
RETURNING token_balance`, so two concurrent requests cannot both pass on the
same last tokens. Admin grant/set/revoke lock the membership row (`SELECT ... FOR
UPDATE`) inside a transaction, and every change — including each request's
reconciled usage with its correlation id — is appended to the immutable
`token_ledger`. Grants accept an idempotency key (unique per organization) so a
retried request cannot double-credit. A failed reconcile leaves the estimate
debited (fail closed) and is logged for manual correction.

## Multi-tenant isolation
EF Core global query filters scope every tenant-owned query to the caller's
organization. A super admin runs with no scope for cross-tenant views.
Login/refresh, registration and the credential resolver use `IgnoreQueryFilters`
deliberately because there is no org context yet at that point; each of them
scopes by explicit `WHERE` clauses instead. Org admins can only see, test and
delete credentials owned by their own organization; platform-wide rows
(`organization_id IS NULL`) are super-admin only.

## Forwarded-header trust
The gateway is designed to be reachable only through NGINX (`infra/nginx`) or
the Kubernetes ingress, which set `X-Forwarded-For` / `X-Forwarded-Proto`.
`ForwardedHeaders:TrustAllProxies` therefore defaults to `true`: the gateway
accepts those headers from any upstream so per-IP rate limits and audit IPs
reflect the real client. This is safe only while the API port is not exposed
directly. If it ever is, set `ForwardedHeaders__TrustAllProxies=false`, or a
caller can forge its source address and bypass the per-IP limiter.

## Defense in depth
- Edge rate limiting in NGINX in addition to the Redis quota engine.
- Request bodies on `/v1/messages` capped at 32 MB in both NGINX and the gateway.
- Polly circuit breaker prevents hammering a failing provider on the CLI chat
  path; the `/v1/messages` passthrough deliberately does not retry POSTs, so an
  upstream failure can never double-bill a user.
- Containers run as non-root with health checks.
- Audit log records registration, login, failed login, OTP events, user and
  key lifecycle, credential connect/remove, token grants, quota changes and
  anomalies (see `docs/ADMIN_GUIDE.md` for the action list).
- Personal keys and OAuth `state` values are generated with
  `RandomNumberGenerator`; nothing security-relevant uses `System.Random`.
