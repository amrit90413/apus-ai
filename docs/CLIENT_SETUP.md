# Client setup

Use Claude Code, the VS Code Claude extension, Cline or Roo Code through your
organization's gateway. The gateway speaks the Anthropic Messages API, so the
clients need nothing more than a base URL and a personal key.

For admins (connecting Claude, creating users, allocating tokens) see
[ADMIN_GUIDE.md](ADMIN_GUIDE.md).

---

## 1. Prerequisites

| Need | Notes |
| --- | --- |
| Node.js 20+ | `node --version`. Only needed for the `npx apus-ai` wizard; manual setup needs nothing. |
| A gateway account | Your admin creates it (email + password) and tells you the gateway URL, e.g. `https://ai.example.com`. |
| A supported client | Claude Code CLI, VS Code Claude extension, Cline, Roo Code, or anything that takes an Anthropic base URL + API key. |

Admins log in with a WhatsApp OTP as a second factor when the deployment has it
enabled; regular users log in with a password only.

---

## 2. Quick install

```bash
npx apus-ai
```

The wizard does four things:

1. **Logs you in** — asks for the gateway URL (or pass `--api https://ai.example.com`
   / set `APUS_AI_API`), your email and password, and stores the session in your
   OS keychain (file fallback: `~/.apus-ai/credentials.json`, mode 0600).
2. **Mints a personal key** — `POST /api/v1/me/keys` returns an `apus_...` key
   once. It never expires by default and can be revoked by you or your admin.
   On a re-run the wizard offers to reuse the key it stored; `--new-key` forces
   a fresh one.
3. **Writes client config** — detects Claude Code / VS Code / Cline / Roo Code and
   merges the blocks below into their settings files (other keys are left alone).
   Models are filled from `GET /api/v1/me/models`, i.e. what your admin allocated.
4. **Verifies** — calls `GET /v1/models` with the new key and prints the models
   you can use.

Re-run `npx apus-ai` any time to re-write config, or `npx apus-ai setup --new-key`
to rotate the key. Non-interactive (CI, dotfiles):

```bash
echo "$PASSWORD" | npx apus-ai setup --api https://ai.example.com \
  --email me@example.com --password-stdin --clients claude,cline --yes
```

---

## 3. Manual configuration

Replace `https://ai.example.com` with your gateway URL and `apus_...` with a key
from the **Your API keys** section of the `/usage` dashboard — which also prints a
ready-to-paste Claude Code block filled in with your gateway URL and allocated
models — or from the wizard, or `POST /api/v1/me/keys`. Use only model ids your
admin allocated (`GET /api/v1/me/models` or `GET /v1/models`).

### Claude Code CLI

`~/.claude/settings.json` — merge into the existing file, do not replace it:

```json
{
  "env": {
    "ANTHROPIC_AUTH_TOKEN": "apus_...",
    "ANTHROPIC_BASE_URL": "https://ai.example.com",
    "ANTHROPIC_MODEL": "claude-sonnet-5",
    "ANTHROPIC_DEFAULT_OPUS_MODEL": "claude-opus-5",
    "ANTHROPIC_DEFAULT_SONNET_MODEL": "claude-sonnet-5",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL": "claude-haiku-4-5",
    "ANTHROPIC_SMALL_FAST_MODEL": "claude-haiku-4-5",
    "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1"
  },
  "hasCompletedOnboarding": true
}
```

> **If Claude Code shows a login screen, stop.** A prompt asking how you want to
> log in — *Claude.ai Subscription* / *Anthropic Console* — means it is not reading
> the settings above. Do not pick either option: both authenticate you straight to
> Anthropic and bypass the gateway, so nothing is metered against your quota and
> the usage lands on whatever account you signed in with. Quit, fix the config
> (see below), and re-run `claude` — it should start with no login prompt at all.
>
> Usual causes, in order:
> 1. `ANTHROPIC_API_KEY` is exported in your shell. It takes precedence over the
>    settings file — `unset ANTHROPIC_API_KEY` (and remove it from `~/.zshrc`,
>    `~/.bashrc`, or your dotfiles).
> 2. You are already logged in from an earlier session — run `claude /logout`.
> 3. The JSON was written to the wrong file. It must be `~/.claude/settings.json`,
>    valid JSON, with the keys inside an `"env"` object.

Omit the `*_OPUS_MODEL` / `*_HAIKU_MODEL` lines if those models are not
allocated to you. `ANTHROPIC_AUTH_TOKEN` is sent as `Authorization: Bearer`;
the gateway accepts it exactly like `x-api-key`.

Claude Code may request a model as `claude-sonnet-5[1m]` (its 1M-context hint).
The gateway strips the `[1m]` suffix before the allow-list check and before
forwarding, so allocate and configure plain model ids.

### VS Code Claude extension

Same file, same block: the extension reads `~/.claude/settings.json`. Restart VS
Code (or reload the window) after editing.

### Cline

VS Code `settings.json` (`Cmd/Ctrl+Shift+P` → *Preferences: Open User Settings (JSON)*):

```json
{
  "cline.apiProvider": "anthropic",
  "cline.anthropicBaseUrl": "https://ai.example.com",
  "cline.apiKey": "apus_..."
}
```

### Roo Code

```json
{
  "roo-cline.apiProvider": "anthropic",
  "roo-cline.anthropicBaseUrl": "https://ai.example.com",
  "roo-cline.apiKey": "apus_..."
}
```

VS Code user settings live at:

| OS | Path |
| --- | --- |
| macOS | `~/Library/Application Support/Code/User/settings.json` |
| Linux | `~/.config/Code/User/settings.json` |
| Windows | `%APPDATA%\Code\User\settings.json` |

Pick the model inside the extension's provider settings from the list your
admin allocated.

---

## 4. Authentication

Two mechanisms, deliberately separate:

| Surface | Credential | Header |
| --- | --- | --- |
| Proxy `/v1/*` | Personal key `apus_...` | `x-api-key: apus_...` **or** `Authorization: Bearer apus_...` |
| Dashboard / CLI API `/api/v1/*` | JWT from `POST /api/v1/auth/login` (15 min, refresh rotates) | `Authorization: Bearer <jwt>` |

A personal key carries your user + workspace identity, so every proxy request is
charged to your quota and balance, not to an IP. Keys are stored as a SHA-256
hash; the gateway can show you only the first 12 characters (`apus_ab12cd3`)
afterwards. A revoked key, or the key of a deactivated user, stops working
within 30 seconds (per-pod lookup cache).

Keys are easiest to manage on the `/usage` dashboard (create, copy, revoke). The
same operations over HTTP, with a JWT:

```bash
curl -s -X POST https://ai.example.com/api/v1/me/keys \
  -H "authorization: Bearer $JWT" -H 'content-type: application/json' \
  -d '{"name":"laptop"}'
# -> 201 { "id", "name", "key": "apus_...", "prefix", "createdAt", "expiresAt" }   (key shown once)

curl -s https://ai.example.com/api/v1/me/keys -H "authorization: Bearer $JWT"
curl -s -X DELETE https://ai.example.com/api/v1/me/keys/<id> -H "authorization: Bearer $JWT"   # 204
```

---

## 5. API reference (proxy)

Base URL: your gateway. Bodies and headers are the Anthropic Messages API;
the gateway forwards them verbatim and streams the response back unchanged.

### `POST /v1/messages`

```bash
curl -s https://ai.example.com/v1/messages \
  -H "x-api-key: apus_..." \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
    "model": "claude-sonnet-5",
    "max_tokens": 256,
    "messages": [{"role": "user", "content": "Say hello in five words."}]
  }'
```

```json
{
  "id": "msg_01...",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-5",
  "content": [{"type": "text", "text": "Hello there, nice to meet you."}],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": {"input_tokens": 14, "output_tokens": 9}
}
```

- `"stream": true` returns server-sent events exactly as Anthropic emits them
  (`message_start` … `message_stop`). NGINX and the gateway do not buffer.
- Tools, `system`, images, PDFs and extended thinking pass through. Request bodies
  up to 32 MB.
- Before forwarding, the gateway checks the model allow-list, reserves an estimate
  against your prepaid balance (if one is set) and your rolling windows, then
  reconciles both to the real token usage from the response.

Extra response headers:

| Header | Meaning |
| --- | --- |
| `X-Quota-Remaining` | Tokens left in your tightest rolling window |
| `X-Quota-Reset-Seconds` | Seconds until that window resets |
| `X-Balance-Remaining` | Prepaid tokens left; only present when a balance is enforced |

### `POST /v1/messages/count_tokens`

Passthrough, not charged against quota or balance. The model must still be one
allocated to you (403 `permission_error` otherwise).

```bash
curl -s https://ai.example.com/v1/messages/count_tokens \
  -H "x-api-key: apus_..." -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-5","messages":[{"role":"user","content":"hi"}]}'
# -> { "input_tokens": 8 }
```

### `GET /v1/models`

Only the models allocated to you.

```bash
curl -s https://ai.example.com/v1/models -H "x-api-key: apus_..."
```

```json
{
  "data": [
    {"id": "claude-sonnet-5", "type": "model", "display_name": "claude-sonnet-5", "created_at": "..."}
  ],
  "has_more": false,
  "first_id": "claude-sonnet-5",
  "last_id": "claude-sonnet-5"
}
```

### Check my status (JWT)

```bash
curl -s https://ai.example.com/api/v1/me/usage -H "authorization: Bearer $JWT"
# -> { "windows": [{ "name", "used", "limit", "resetInSeconds" }],
#      "balance": { "enforced": true, "remaining": 480000 } }

curl -s https://ai.example.com/api/v1/me/balance -H "authorization: Bearer $JWT"
# -> { "enforced": true, "remaining": 480000 }      (enforced:false, remaining:null = unlimited)
```

`GET /api/v1/me/models` lists your allocated model ids; `GET /api/v1/me/sessions`
lists your logged-in devices. The same numbers are on the dashboard at `/usage`.

---

## 6. Troubleshooting

Proxy errors use Anthropic's shape: `{"type":"error","error":{"type":"...","message":"..."}}`.

| HTTP | `error.type` | Message contains | Cause | Fix |
| --- | --- | --- | --- | --- |
| 401 | `authentication_error` | `Invalid or revoked API key. Run \`npx apus-ai\` to reconnect.` | Key missing, mistyped, revoked, expired, or your account was deactivated | Re-run `npx apus-ai` (mints a new key and rewrites config). Check the header is `x-api-key` or `Authorization: Bearer`. |
| 403 | `permission_error` | `Model '<id>' is not enabled for your account.` | Model not in your allocation | Use a model from `GET /v1/models`, or ask your admin to allocate it. `[1m]` is stripped, so the base id must be allowed. |
| 429 | `rate_limit_error` | `Quota '<window>' exhausted. Resets in Ns.` | Rolling window full | Wait; `Retry-After` / `X-Quota-Reset-Seconds` say how long. The window refills by itself. |
| 402 | `permission_error` | `Token balance exhausted (N left, M needed). Ask your admin to add tokens.` | Prepaid balance empty | Does **not** reset, and clients do not retry it. Ask your admin for a top-up (`/admin/users` → grant tokens). Check `GET /api/v1/me/balance`. |
| 503 | `api_error` | `No AI provider is connected for your organization...` | Your organization has no Claude credential | Admin must connect one at `/admin/providers`. |
| 503 | `api_error` | `The AI provider rejected your organization's credential...` | Anthropic rejected the org credential (revoked key, expired OAuth grant) | Admin must reconnect the credential at `/admin/providers` and press **Test**. |
| 502 | `api_error` | `Upstream AI provider is unreachable.` | Gateway could not reach Anthropic | Retry; if it persists, tell your admin. |
| 400 / 529 / … | Anthropic's own type | as sent by Anthropic | Upstream error (`invalid_request_error`, `overloaded_error`, …) | Relayed verbatim; fix the request or retry as you would against Anthropic directly. |
| 413 | — | request entity too large | Body over 32 MB (large images/PDFs) | Reduce attachments. |

Window vs balance, in one line: a **window** (`X-Quota-Remaining`, 429) refills
on a timer; a **balance** (`X-Balance-Remaining`, 402) is prepaid and only goes
up when an admin grants more. Balance exhaustion is deliberately not a 429 so
Claude Code does not back off and retry against an empty balance.

Other checks:

- `GET /v1/models` with your key is the fastest smoke test; it needs no quota.
- If `ANTHROPIC_API_KEY` is also exported in your shell, unset it so Claude Code
  authenticates with the gateway key only.
- The dashboard login (`/login`) is JWT-based and unrelated to proxy keys; a
  revoked key does not log you out of the dashboard and vice versa.
