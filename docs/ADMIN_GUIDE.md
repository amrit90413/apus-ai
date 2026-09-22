# Admin guide

For organization admins: sign up, connect your organization's Claude credential,
create users, hand out token allowances and models, and audit what happened.
Everything here is also reachable from the dashboard (`/admin`, `/admin/users`,
`/admin/providers`); the `curl` examples show the same calls the dashboard makes
so you can script them. All admin endpoints take a JWT with the OrgAdmin or
SuperAdmin role.

End-user setup lives in [CLIENT_SETUP.md](CLIENT_SETUP.md); operator configuration
in [DEPLOYMENT.md](DEPLOYMENT.md).

---

## 1. Sign up

Self-service signup exists only when the operator set `Registration:Enabled`
(`REGISTRATION_ENABLED=true`). Otherwise the bootstrap super admin creates your
organization and account for you, and you skip to step 2.

1. Open `/register` on the gateway.
2. Enter organization name (2–80 chars), your email, a password (12+ chars by
   default), a phone number in E.164 digits without `+` (e.g. `919876543210`) and
   the invite code if the deployment requires one.
3. You get one organization, a `Default` workspace and your account as OrgAdmin.
   Log in at `/login`.

`GET /api/v1/auth/register` tells the page what is required:
`{ enabled, inviteCodeRequired, phoneRequired, minPasswordLength }`. The phone
number is required whenever WhatsApp OTP is enabled, because admin logins are
second-factored through it.

| Error | Meaning |
| --- | --- |
| 404 `registration_disabled` | Signup is off; ask the platform administrator |
| 403 `invalid_invite` | Wrong or missing invite code |
| 400 `invalid_organization` / `invalid_email` / `weak_password` / `invalid_phone` / `phone_required` | Validation |
| 409 `email_taken` | Email already has an account (emails are unique across the platform) |

The signup is recorded as `organization_registered` in the audit log.
Registration is rate-limited to 10 requests per minute per IP.

---

## 2. Connect Claude

Open `/admin/providers`. Requests from your users are served with the newest
active credential owned by your organization; if you have none, the gateway falls
back to a platform-wide credential (managed by the super admin) and finally to
the `Anthropic__ApiKey` environment value. Until one of those exists, every
request fails with `provider_not_configured`.

### 2.1 API key (recommended)

1. In the [Claude Console](https://console.anthropic.com) open your
   organization's workspace and create a **new API key dedicated to the gateway**
   (name it e.g. `apus-gateway`). Use a key owned by the organization's Console
   account, not one tied to an individual who might leave. Copy the `sk-ant-...`
   value; the Console shows it once.
2. On `/admin/providers` choose **API key**, provider `anthropic`, paste the key
   and save. The key is encrypted at rest immediately; the page will only ever
   show its last four characters (`...6789`).
3. Press **Test**. The gateway calls Anthropic's `GET /v1/models` with the stored
   key and reports `Credential is valid.`, `Provider rejected the credential (401).`
   or `Could not reach the provider: ...`.

```bash
curl -s -X POST https://ai.example.com/api/v1/admin/provider-credentials/api-key \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"provider":"anthropic","apiKey":"sk-ant-..."}'
# -> { "id": "..." }

curl -s -X POST https://ai.example.com/api/v1/admin/provider-credentials/<id>/test \
  -H "authorization: Bearer $JWT"
# -> { "ok": true, "message": "Credential is valid." }

curl -s https://ai.example.com/api/v1/admin/provider-credentials -H "authorization: Bearer $JWT"
# -> { "credentials": [{ id, provider, kind: "api_key"|"oauth", hint, isActive,
#                        accessExpiresAt, lastRefreshedAt, lastError, createdAt }],
#      "oauth": { "enabled": false, "provider": "anthropic" } }
```

Rotating: add the new key, Test it, then delete the old one
(`DELETE /api/v1/admin/provider-credentials/{id}`). The newest active credential
wins, and the gateway caches the resolved credential for at most 60 seconds per
pod, so a removal takes effect within a minute everywhere.

`provider` may also be `openai`; the Test button only probes `anthropic`.

### 2.2 Browser login (OAuth)

Available only when the operator configured all of `Anthropic__OAuth__ClientId`,
`AuthorizeUrl`, `TokenUrl` and `RedirectUri` (the page shows `oauth.enabled`).
Otherwise the button is hidden and the API answers 409 `oauth_not_configured`.

> **Terms of use.** Connect only an account and an OAuth client that belong to
> your organization. Sharing one person's Claude subscription (Pro/Max) with a
> team through the gateway, or reusing another application's OAuth client id
> (for example the one shipped inside a desktop or CLI app), violates
> Anthropic's terms and gets the account blocked. If you cannot obtain an OAuth
> client issued to your organization, use an API key.

Flow:

1. **Connect with browser login** → `POST /api/v1/admin/provider-credentials/oauth/start`
   returns `authorizeUrl` (PKCE, `state` valid 10 minutes, single use). The browser
   is sent there.
2. The provider redirects to `<RedirectUri>?code=...&state=...`, which is the
   page `/admin/providers/callback`. It posts `{ code, state }` to
   `POST /api/v1/admin/provider-credentials/oauth/callback`.
3. The gateway exchanges the code, stores access + refresh tokens encrypted, and
   labels the credential `OAuth · <your email>`.

The same admin who started the flow must finish it, in the same organization.
Access tokens are refreshed server-side 120 seconds before expiry
(`RefreshSkewSeconds`); refreshes are serialized across pods with a Redis lock.
If Anthropic rejects the refresh (`invalid_grant`), the credential is
deactivated with `lastError` set and users see `provider_auth_failed` until you
reconnect.

| Error | Meaning |
| --- | --- |
| 409 `oauth_not_configured` | Operator has not set the `Anthropic__OAuth__*` values |
| 400 `invalid_state` | Login expired (10 min) or was already used; start again |
| 400 `oauth_rejected` | Provider refused the code; start again |
| 502 `oauth_unreachable` | Token endpoint unreachable; retry |

---

## 3. Create users

`/admin/users` → **Add user**, or:

```bash
curl -s https://ai.example.com/api/v1/admin/workspaces -H "authorization: Bearer $JWT"
# -> { "workspaces": [{ id, name, isActive, memberCount }] }

curl -s -X POST https://ai.example.com/api/v1/admin/users \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"email":"dev@example.com","password":"<temporary, 12+ chars>","workspaceId":"<id>","role":"User"}'
# -> { "userId", "email", "role" }
```

Roles: `User` (uses the proxy, sees own usage) or `OrgAdmin` (everything in this
guide; needs a `phoneNumber` when WhatsApp OTP is enabled). Give the user the
gateway URL and their credentials; they run `npx apus-ai` to mint a personal key
and configure their editor. Nothing else is needed on your side unless you want
a token allowance or a model restriction.

`PATCH /api/v1/admin/users/{id}` with `{ isActive, role, phoneNumber }` updates
the account; `isActive: false` blocks logins and, within 30 seconds, every
personal key the user holds.

---

## 4. Allocate tokens

Each (user, workspace) membership carries a prepaid **token balance**. `null` means
unlimited: the user is bounded only by the rolling windows in section 5. A
number means enforced: every request must fit into the balance, and the balance
never refills on its own.

| Operation | Endpoint | Effect | Ledger row |
| --- | --- | --- | --- |
| Grant | `POST /api/v1/admin/users/{id}/balance/grant` `{ tokens>0, note?, idempotencyKey? }` | Adds to the balance. If the user was unlimited, enforcement starts at `tokens`. | `grant`, `delta = +tokens` |
| Set | `PUT /api/v1/admin/users/{id}/balance` `{ tokens>=0, note? }` | Overwrites the balance (0 blocks the user until the next grant). | `set`, `delta = tokens - previous` |
| Unlimited | `DELETE /api/v1/admin/users/{id}/balance` | Back to `null`. Windows still apply. | `revoke`, `delta = -previous` |
| Allowance | `PUT /api/v1/admin/users/{id}/balance/allowance` `{ tokens>0, rollover, note? }` | Recurring monthly top-up; credits the current period immediately. | `allowance`, `delta = new - previous` |
| Stop allowance | `DELETE /api/v1/admin/users/{id}/balance/allowance` | No further renewals. Tokens already credited stay. | — |
| Inspect | `GET /api/v1/admin/users/{id}/balance?limit=50` | Balance + allowance + history (up to 200 rows). | — |

```bash
curl -s -X POST "https://ai.example.com/api/v1/admin/users/<user-id>/balance/grant" \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"tokens":2000000,"note":"Q4 allowance","idempotencyKey":"q4-2026-dev@example.com"}'
# -> { userId, workspaceId, membershipId, enforced: true, balance: 2000000,
#      history: [{ id, kind: "grant", delta: 2000000, balanceAfter: 2000000, actorUserId, reference: "Q4 allowance", createdAt }] }
```

Add `?workspaceId=` when the user belongs to more than one workspace; the
endpoint refuses to guess (400 `workspace_required`). Grants are capped at
10<sup>12</sup> tokens per call and notes at 256 characters. An `idempotencyKey`
is unique per organization: replaying the same grant returns the original ledger
row instead of adding tokens twice — use it from scripts and retries.

### Monthly allowance

A one-off grant does not refill. An **allowance** does: set one and the balance is
topped up once per calendar month (UTC), which is how you give each employee a
fixed monthly budget without re-granting by hand.

```bash
curl -s -X PUT "https://ai.example.com/api/v1/admin/users/<user-id>/balance/allowance" \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"tokens":500000,"rollover":false,"note":"standard seat"}'
# -> { ..., balance: 500000,
#      allowance: { tokens: 500000, rollover: false, period: "monthly", lastCreditedPeriod: "2026-09" } }
```

`rollover` decides what happens to what is left:

| `rollover` | Behaviour | Use when |
| --- | --- | --- |
| `false` (default) | Balance **resets** to the allowance each month. Unused tokens are lost. | You want a hard, predictable ceiling per person per month. |
| `true` | The allowance is **added** to what is left, so an unused month accumulates. | Usage is bursty and you don't want to penalise a quiet month. |

Notes on behaviour:

- **The top-up is applied lazily**, on the first request or dashboard load in a new
  month — not by a scheduler. The gateway is stateless and horizontally scaled, so
  a timer would need leader election and a missed run would strand users. A user who
  does not log in for a month is credited the moment they return; they never miss a
  period, but they also never accumulate more than one unless `rollover` is on.
- **Concurrency is safe.** The credit runs under a `SELECT … FOR UPDATE` on the
  membership row and carries the idempotency key `allowance:{membershipId}:{YYYY-MM}`,
  so simultaneous requests across replicas cannot double-credit.
- **Editing the amount mid-month re-credits immediately** with the new figure, so
  raising someone's allowance takes effect at once rather than next month.
- Setting an allowance on an unlimited user starts enforcement. Stopping the
  allowance leaves the remaining balance in place and still enforced — use
  `DELETE .../balance` to return them to unlimited.
- Every credit is an `allowance` ledger row with `actorUserId` null for automatic
  renewals and the admin's id when they set or changed it.

### How a request is charged

1. Before the provider is called, the gateway estimates the request
   (input characters / 4 + `max_tokens`) and debits it in **one conditional
   `UPDATE`** on the membership row. A `null` balance passes through; an
   insufficient one changes nothing and the user gets 402 `permission_error`
   `Token balance exhausted (N left, M needed)` — deliberately not a 429, so
   Claude Code does not retry until you top up.
2. After the response, the debit is reconciled to the real usage Anthropic
   reported — input, output and prompt-cache tokens — refunding the
   over-reservation or charging the difference. A large under-estimate can push
   the balance slightly below zero, which simply blocks the next request until
   you grant more.
3. A `usage` ledger row is appended with `delta = -real` and
   `reference = "<model> corr=<correlation id>"`. The correlation id matches the
   gateway's request log line, so a disputed charge can be traced to the exact
   request.

The ledger (`token_ledger`) is append-only; the balance on the membership is the
running total. Rolling windows are checked *after* the balance, so a blocked
request never consumes window quota.

---

## 5. Allowed models and windows

Two layers, resolved per (user, workspace):

- **Workspace policy** — `PUT /api/v1/admin/workspaces/{id}/policy`
  `{ allowedModels?, userWindows?, workspaceWindows?, requestsPerMinute? }`.
  Applies to everyone in the workspace. Omitted fields keep their value.
- **User override** — `PUT /api/v1/admin/users/{id}/quota?workspaceId=`
  `{ allowedModels?, windows? }` (at least one). Replaces the workspace values
  for that user. `DELETE` the override to inherit the workspace again.

```bash
# Only Sonnet and Haiku for this user, 500k tokens per 5 hours
curl -s -X PUT "https://ai.example.com/api/v1/admin/users/<user-id>/quota" \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"allowedModels":["claude-sonnet-5","claude-haiku-4-5"],
       "windows":[{"name":"w5h","tokenLimit":500000,"windowMinutes":300}]}'

curl -s "https://ai.example.com/api/v1/admin/users/<user-id>/quota" -H "authorization: Bearer $JWT"
# -> { userId, workspaceId, hasOverride, override: { userWindows?, allowedModels? },
#      effective: { allowedModels, userWindows, workspaceWindows, requestsPerMinute } }
```

Model ids must be served by a registered provider (`claude-*`, or Ollama models
when Ollama is configured); anything else is rejected with 400 `unknown_model`.
Current Claude ids: `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`. Users
see the effective list at `GET /v1/models`, and the `npx apus-ai` wizard writes
it into their editor config. Claude Code's `[1m]` suffix is stripped before the
check, so allow plain ids.

Policy changes propagate within 30 seconds (per-pod policy cache). Renaming a
window resets its counter; lowering a limit below current usage blocks the user
until the window expires.

---

## 6. Revoke access

| Goal | Action |
| --- | --- |
| Stop a leaked or stale personal key | `GET /api/v1/admin/users/{id}/keys` → `DELETE /api/v1/admin/users/{id}/keys/{keyId}` (204). Takes effect within 30 seconds per API pod (key lookups are cached); the user re-runs `npx apus-ai` for a new one. |
| Log a user out of every device | `POST /api/v1/admin/users/{id}/revoke-sessions` → `{ sessionsRevoked }`. Their refresh tokens die; access tokens expire within 15 minutes. |
| Offboard | `PATCH /api/v1/admin/users/{id}` `{ "isActive": false }` (keys stop working within 30 s), then revoke keys and sessions. |
| Cut everyone off from Claude | `DELETE /api/v1/admin/provider-credentials/{id}` on every active credential. Users see `provider_not_configured` within 60 seconds. |

Keys are stored as a SHA-256 hash plus a 12-character display prefix; a revoked
key cannot be un-revoked, only re-issued. Keys of a deactivated user are rejected
even if not revoked. Users can list and revoke their own keys at
`/api/v1/me/keys` as well.

---

## 7. Audit log

Every admin action and every login lands in the `audit_logs` table
(`organization_id, user_id, action, detail, ip, at`). There is no UI for it yet;
query Postgres:

```sql
SELECT at, action, user_id AS actor, detail, ip
  FROM audit_logs
 WHERE organization_id = '<org-id>'
 ORDER BY at DESC
 LIMIT 100;
```

| Action | Written when | `detail` |
| --- | --- | --- |
| `organization_registered` | Self-service signup | `org=<slug> admin=<email>` |
| `bootstrap_seeded` | First admin seeded at startup | — |
| `login` / `login_failed` | Password login succeeded / failed | device, ip / email |
| `otp_sent` / `login_otp_verified` / `otp_delivery_failed` | Admin OTP flow | device |
| `admin_login_without_otp` | Admin logged in while `WHATSAPP_ENABLED=false` | device |
| `user_created` / `user_updated` | `POST` / `PATCH /admin/users` | email, role, workspace / active, role |
| `sessions_revoked` | `POST /admin/users/{id}/revoke-sessions` | `targetUser=… count=…` |
| `api_key_created` / `api_key_revoked` | User minted / revoked a personal key at `/api/v1/me/keys` | `key=… name=…` / `key=…` |
| `api_key_revoked_by_admin` | `DELETE /admin/users/{id}/keys/{keyId}` | `targetUser=… key=…` |
| `member_added` / `member_removed` / `workspace_created` | Workspace membership changes | — |
| `workspace_policy_set` | `PUT /admin/workspaces/{id}/policy` | — |
| `user_quota_set` / `user_quota_cleared` | `PUT` / `DELETE /admin/users/{id}/quota` | — |
| `tokens_granted` | Grant | `targetUser=… workspace=… tokens=… balance=…` |
| `tokens_set` | Set | `targetUser=… workspace=… tokens=…` |
| `tokens_revoked` | Back to unlimited | `targetUser=… workspace=…` |
| `provider_credential_added` | API key saved or OAuth completed | `id=… provider=… kind=api_key\|oauth` |
| `provider_credential_removed` | Credential deleted | `id=…` |
| `provider_oauth_started` | Browser login begun | `provider=anthropic` |
| `provider_oauth_failed` | Provider rejected the code exchange | `reason=…` |

Balance changes are additionally in `token_ledger` with the acting admin
(`actor_user_id`) and your note (`reference`); usage rows carry the model and
correlation id. For per-user consumption over time use the dashboard
(`/admin/users`, 30-day totals from ClickHouse) or `GET /api/v1/admin/users`.

---

# Setting up AI for your team

Seven steps, about ten minutes.

## 1. Create your organization

Sign up at `/register`, or have your platform operator seed the first admin.

## 2. Invite your team

**Users → + New user.** Roles:

| Role | Can do |
| --- | --- |
| Member | Use AI, see their own usage |
| Viewer | Read every dashboard, change nothing |
| Workspace admin | Allowances, model access, usage export |
| AI admin | The above plus connect and disconnect providers |
| Billing admin | Budgets, pricing and spend, but not providers |
| Org admin | Everything in the organization |

## 3. Connect a provider

**Settings → AI Providers → Connect.** Pick the method for that provider: an
organization API key, AWS or Google Cloud credentials, or browser login where your
operator has configured an OAuth client your organization owns.

APUS validates the credential immediately, so you find out now rather than when someone
hits a failing request. Full details, including what *cannot* be connected and why, are
in [PROVIDERS.md](PROVIDERS.md).

## 4. Choose which models people can use

**Settings → Team → Manage** per member, or set a tenant-wide list in
**AI Overview → Settings**. Layers narrow, never widen: platform → tenant → workspace →
role → member. A member can only reach a model that every layer above allows *and* that
a connected provider serves — which is why `/v1/models` may be shorter than the list you
enabled.

## 5. Set the organization budget

**AI Overview → Settings.** Pick your currency, the monthly budget, and a markup if you
re-bill internally. The budget is a hard ceiling for everyone: once it is exhausted,
requests are refused with `TENANT_ALLOWANCE_EXCEEDED` until it is raised or the period
resets.

## 6. Set per-member allowances

**Settings → Team.** Give each person a monthly figure; leave it unset and they draw
from the shared pool with only the organization budget applying. The page shows what is
allocated, what is left in the pool, and each person's spend.

```
Organization budget   ₹50,000
Allocated to members  ₹20,000
Remaining shared pool ₹30,000

Aman     ₹1,000/month   used ₹412   remaining ₹588   Sonnet ✓  Opus ✕
Ravi     ₹500/month     used ₹96    remaining ₹404
Admin    Unlimited      used ₹1,200                  all models
```

Also per member, when you need it: a daily sub-limit, requests per minute, tokens per
minute, concurrent requests, a daily request cap, an access expiry date, and
suspend/reactivate.

**Top up** adds budget to the current period only. **Reset usage** clears this period's
consumption as a correction — both are audited, and neither rewrites history.

## 7. People start using it

```bash
npx apus-ai
```

Signs them in, mints a personal key and points Claude Code, Cline or Roo Code at the
gateway. They never see a provider credential. See [CLIENT_SETUP.md](CLIENT_SETUP.md).

## Watching it

- **AI Overview** — budget, spend, margin, latency, failures, breakdowns by provider,
  member and model, and the daily trend.
- **Settings → AI Providers** — connection health, this month's traffic and cost per
  provider, and **Test connection**.
- **Settings → Team** — per-member spend and remaining.

Notifications go out at 50%, 75%, 90% and 100% of an allowance — to the member for
their own, and to admins for the organization's — and when a connection needs
reconnecting. Each fires once per period.

From a terminal:

```bash
npx apus-ai usage       # your allowance, what's left, reset date
npx apus-ai models      # what you can use, and which provider serves it
npx apus-ai providers   # admin: connection states and spend
```

## When someone is blocked

The error code says which rule stopped them:

| Code | Meaning | Fix |
| --- | --- | --- |
| `MODEL_NOT_ALLOWED` | Not on their model list | Settings → Team → Manage |
| `PROVIDER_NOT_CONNECTED` | Nothing connected serves that model | Settings → AI Providers |
| `PROVIDER_REAUTHENTICATION_REQUIRED` | The provider rejected the stored credential | Reconnect |
| `USER_ALLOWANCE_EXCEEDED` | Their monthly budget is spent | Top up, or raise the allowance |
| `TENANT_ALLOWANCE_EXCEEDED` | The organization budget is spent | Raise it in AI Overview → Settings |
| `RATE_LIMIT_EXCEEDED` | Too many requests or tokens per minute | Raise their limits, or wait |
| `USER_AI_ACCESS_DISABLED` | Suspended, disabled, or access expired | Settings → Team → Reactivate |

Raising a limit takes effect on their next request — there is no need to wait for the
period to roll over.
