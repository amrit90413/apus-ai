# Connecting AI providers

An organization admin connects the AI accounts the company pays for. Everyone on the
team then reaches those accounts through APUS — they authenticate to APUS with their
own credentials and **never receive a provider key**.

Page: **Settings → AI Providers** (`/settings/ai-providers`).

## What can be connected

| Provider | Methods | Needs |
| --- | --- | --- |
| Anthropic | API key, browser login (OAuth, when configured) | Console API key |
| OpenAI | API key | Platform API key |
| Google Gemini | API key | AI Studio key |
| AWS Bedrock | AWS credentials | Access key id + secret, region |
| Google Vertex AI | Service account | Service-account key (JSON), project, location |

Adding another vendor is a catalog entry plus one upstream adapter. The quota,
allowance, billing, RBAC and user systems do not change — see
[ARCHITECTURE.md](ARCHITECTURE.md).

### What cannot be connected, and why

A personal Claude Pro/Max (or equivalent consumer) subscription. Those plans
authenticate the provider's own apps, not third-party gateways, and fanning one out
across a team is subscription sharing. APUS never asks for provider website
credentials, never reads provider cookies, and never calls a private endpoint. If a
provider offers no third-party OAuth for inference, its supported API credential is
the way in — the connection model is identical either way.

## Browser login (OAuth)

Available for a provider once the operator configures an OAuth client **that provider
issued to your organization**. Reusing another application's client id violates
provider terms.

```
APUS dashboard
  → Settings → AI Providers → [ Connect Anthropic ]
  → GET /api/v1/provider-connections/anthropic/oauth/start
      · generates state (single-use, 10 min, server-side in Redis)
      · generates a PKCE verifier; sends only the S256 challenge
      · sets a browser-bound flow cookie (HttpOnly, SameSite=Lax,
        Secure over HTTPS)
  → redirect to the provider's authorize endpoint
  → admin signs in at the provider and approves APUS
  → provider redirects to
      https://<your-apus-domain>/api/provider-connections/anthropic/oauth/callback
        ?code=…&state=…
  → APUS validates state (consumed with GETDEL — a replay cannot exchange twice)
    and the flow cookie (a callback forged elsewhere is refused)
  → exchanges the code with the PKCE verifier and the client secret
  → receives access_token, refresh_token, expires_in, scope
  → encrypts both tokens (AES-256-GCM, versioned data key)
  → stores the connection, status = Connected
  → redirect to /settings/ai-providers?connected=anthropic
```

### Registering the redirect URI

Register this **exact** URL with the provider and set the same value in configuration:

```
https://<your-apus-domain>/api/provider-connections/<provider>/oauth/callback
```

Redirect URIs are matched character for character and are painful to change once an
application is approved, so the un-versioned path is a first-class route. The
versioned form (`/api/v1/provider-connections/...`) works too.

```bash
PROVIDER_OAUTH_ANTHROPIC_CLIENT_ID=…
PROVIDER_OAUTH_ANTHROPIC_CLIENT_SECRET=…        # omit for a public PKCE-only client
PROVIDER_OAUTH_ANTHROPIC_AUTHORIZE_URL=…
PROVIDER_OAUTH_ANTHROPIC_TOKEN_URL=…
PROVIDER_OAUTH_ANTHROPIC_REVOKE_URL=…           # optional; used on disconnect
PROVIDER_OAUTH_ANTHROPIC_SCOPES=…
PROVIDER_OAUTH_ANTHROPIC_REDIRECT_URI=https://ai.example.com/api/provider-connections/anthropic/oauth/callback
```

Leave them blank and the browser-login option simply does not appear; admins connect
with a credential instead.

If your deployment's registered redirect URI instead points at a dashboard page
(`/admin/providers/callback`), that page posts the code back to the same exchange —
both shapes are supported.

### After connecting

The access token is refreshed server-side before it expires, under a Redis lock so
several gateway replicas cannot refresh the same grant at once. When a provider
rotates the refresh token, the replacement is written in the same transaction as the
new access token. If the provider repudiates the grant, the connection moves to
**Reconnect required**, the dashboard says so, and an admin is notified — user
requests get `PROVIDER_REAUTHENTICATION_REQUIRED` rather than a confusing 500.

## Connecting with a credential

Post it once over HTTPS to the APUS backend. It is validated against the provider,
encrypted, and the plaintext is discarded. It is never written to browser storage, a
URL, a log, an error message, or any API response — only a hint is (the last four
characters, an AWS access key id, or a service-account email).

- **Anthropic / OpenAI / Gemini** — a service-account API key owned by the
  organization, billed to it directly.
- **AWS Bedrock** — an access key id, secret access key, optional session token, and a
  region. Every call is SigV4-signed with *that tenant's* credential; the gateway never
  falls back to ambient host credentials.
- **Google Vertex AI** — a service-account key with the Vertex AI User role, plus
  project and location. APUS exchanges it for short-lived access tokens (RFC 7523) and
  caches only those.

## Connection states

| State | Meaning | What to do |
| --- | --- | --- |
| Connected | Serving traffic | — |
| Refreshing | Renewing an OAuth token | — |
| Expired | Token past its expiry, renewal in progress | Nothing, unless it persists |
| Reconnect required | The provider rejected the credential | Connect again |
| Error | Recent calls failed; still being retried | Test connection |
| Disabled | Paused by an admin | Enable |
| Disconnected | Revoked; the secret has been destroyed | Connect again |

A boolean would collapse "renewing" and "revoked" into the same thing, which is
exactly when an admin needs to know the difference.

One connection per (tenant, provider, purpose) is live at a time. Reconnecting
supersedes the previous one inside a single transaction, so a reconnect is never a
window with nothing connected.

## Disconnecting

Requires confirmation, then: revoke upstream where the provider supports it, destroy
the stored secrets, invalidate the resolution cache, mark the connection Revoked, and
write an audit record. New calls stop immediately.

**Usage history is kept.** Accounting rows reference the connection, and deleting them
would make a past month's invoice unreproducible.

## Model routing

A model id routes to the providers that serve it, in preference order — `claude-*` to
Anthropic first, then Bedrock and Vertex; `gemini-*` to Gemini then Vertex; `gpt-*` to
OpenAI. Pin one explicitly with a prefix: `bedrock/claude-sonnet-5`.

Cloud resellers rename models (`anthropic.claude-…-v1:0`, `claude-…@date`) and those
ids change faster than a release cycle, so the mapping is per-connection configuration:
set `modelMap` on the connection to a JSON object of canonical → native id. Unmapped
ids pass through unchanged.

## Testing a connection

**Test connection** makes one cheap authenticated call per provider — listing models,
or for Bedrock a control-plane call — proving the credential works without spending
tokens. The result updates the connection state, and a background worker runs the same
probe periodically so a credential revoked upstream is discovered by the dashboard
rather than by a developer's failing request.

From a terminal:

```bash
npx apus-ai providers          # states and this month's spend
npx apus-ai providers test <connection-id>
```
