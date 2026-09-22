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

---

## Provider connections

### Secrets at rest

Every provider secret is AES-256-GCM sealed with a random 96-bit nonce under a
**versioned** data key, and the version is stored on the row. Rotation is therefore an
operational change, not an outage: add the new key, point `Encryption:CurrentKeyVersion`
at it, and `CredentialRotationWorker` re-seals rows in the background while old rows keep
decrypting under the key that wrote them. Retire the old key once nothing reports the old
version.

Data keys come from `IDataKeyProvider`. The shipped implementation reads them from
configuration, which in production is a Kubernetes secret / Secrets Manager / Key Vault
projection — the master key is never in the database. A KMS-backed envelope scheme means
implementing that one interface; nothing else changes.

A secret that cannot be decrypted (a retired key, a tampered row) is **refused and
logged, never served**. Verified in the live run: the gateway answered
`PROVIDER_NOT_CONNECTED` with an admin-facing log line rather than passing garbage
upstream.

### Where secrets are not

Plaintext exists only in a local variable for the duration of one call. It is not in any
API response (only a hint — last four characters, an AWS access key id, or a
service-account email), not in browser storage, not in a URL, not in logs, and not in
exception messages. Audit `before`/`after` payloads are scrubbed on the way in: any field
whose name looks like a secret is replaced before the record is written.

Upstream error bodies are **not relayed verbatim** on a credential failure — provider
errors name accounts, organization ids and key fragments. They are translated to the
Anthropic error shape with a generic message.

### OAuth

- PKCE (S256) on every flow; only the challenge leaves the gateway.
- State is generated server-side, held in Redis for 10 minutes, and consumed with
  `GETDEL` — a replayed callback cannot exchange twice.
- A browser-bound flow cookie (HttpOnly, SameSite=Lax, Secure over HTTPS) is required by
  the redirect callback. Without it a callback forged elsewhere could attach an
  attacker's provider account to the tenant; with it, that is refused as `flow_mismatch`.
  The SPA variant requires the admin's own session and matches the originating actor.
- Authorize/token URLs come from operator configuration only. A tenant can never supply
  them, which would otherwise point the gateway's OAuth flow at an endpoint they control.
- The completion redirect is built from the configured redirect URI's own origin plus a
  relative path, so it cannot become an open redirect.
- Refresh runs under a Redis lock so replicas cannot refresh the same grant concurrently;
  a rotated refresh token is written in the same save as the new access token.

### SSRF

Each provider descriptor carries an allowlist of host suffixes. A configured base URL
must be `https` and land on an allowed host, so a tenant-supplied endpoint cannot aim the
gateway — and its credentials — at `169.254.169.254`, an internal service, or a
lookalike domain. Region, project and location values are restricted to `[A-Za-z0-9_-]`
so they cannot inject a path segment.

### Tenant isolation

Enforced at the repository boundary by EF global query filters, not in controllers, so a
missing `WHERE` cannot leak. Integration tests deliberately attempt cross-tenant reads,
writes and aggregates with valid ids from another tenant, and assert they resolve to
nothing.

### Authorization

Endpoints authorize on named permissions via a policy provider that builds policies on
demand; an unknown permission yields no policy and the request is refused. The dashboard
hiding a button is a courtesy, never a control.

### Money

Financial amounts are integer minor units end to end — no floating point anywhere in
allowances, costs or the ledger. Allowance reservation is a single conditional `UPDATE`,
so concurrent requests cannot collectively overspend a budget; a test admits exactly ten
of fifty concurrent requests against a ₹100 budget. Historical ledger rows are never
mutated; corrections are adjustment rows.

### Response headers

Every response carries `Content-Security-Policy: default-src 'none'; frame-ancestors
'none'`, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy:
no-referrer`, COOP/CORP and a `Permissions-Policy`. HSTS is added over HTTPS.
