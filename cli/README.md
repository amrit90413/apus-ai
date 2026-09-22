# apus-ai CLI

One-command setup for Claude Code, Cline and Roo Code through your organization's
apus-ai gateway. You never handle an API key yourself: sign in with the email and
password your admin created, and the wizard mints a personal key and writes the client
config for you.

```sh
npx apus-ai
```

## What `npx apus-ai` does

Running the CLI with no subcommand starts the `setup` wizard:

1. **Gateway** – uses `--api` / `APUS_AI_API` if set, otherwise prompts (default shown).
   Checks the gateway is reachable and exits with a clear error if not.
2. **Sign in** – asks for your email and password (masked). Handles one-time codes
   (WhatsApp OTP) for accounts that require them. Three attempts on a wrong password.
3. **Models** – fetches the models your admin allocated to you and lets you pick the
   default when there is more than one. Warns (and still continues) if none are
   allocated yet.
4. **Clients** – choose which tools to configure: Claude Code CLI / VS Code Claude
   extension, Cline, Roo Code.
5. **Personal key** – mints a key (`POST /api/v1/me/keys`) named after this machine and
   the chosen clients. The key is shown once by the gateway; the wizard only ever prints
   its prefix (`apus_ab12…`) and stores it in your OS keychain. Re-running `setup` offers
   to reuse the stored key if it is still active.
6. **Write config** – merges the gateway settings into the client config files listed
   below, preserving everything else in them.
7. **Verify** – calls `GET <gateway>/v1/models` with the new key and lists what it
   returns. Exit code 0 on success, 1 on failure.

Then start `claude` (or restart VS Code) and you are on the gateway.

## Commands

| Command | Description |
| --- | --- |
| `apus-ai` / `apus-ai setup` | Interactive setup wizard (default command). |
| `apus-ai login` | Sign in only (no client configuration). Stores the session securely. |
| `apus-ai logout` | Clear the stored session and key. Client config files are left in place. |
| `apus-ai keys list [--json]` | List your personal keys. `●` marks the key this machine was set up with. |
| `apus-ai keys revoke <id>` | Revoke a key (ids come from `keys list`). Clients using it get 401 afterwards. |
| `apus-ai usage` | Rolling quota windows plus your prepaid token balance when the admin enforces one. |
| `apus-ai models` | Models allocated to you. |
| `apus-ai sessions` | Active device sessions. |
| `apus-ai chat [-m model] [-p prompt]` | Chat through the gateway (default model `claude-opus-5`). |

Global option: `--api <url>` sets the gateway base URL for any command.

### `setup` flags and non-interactive use

```
apus-ai setup [--api <url>] [--email <email>] [--password-stdin]
              [--clients claude,cline,roo] [--model <id>] [--new-key] [-y|--yes]
```

| Flag | Meaning |
| --- | --- |
| `--api <url>` | Gateway URL (skips the prompt). |
| `--email <email>` | Account email (skips the prompt). |
| `--password-stdin` | Read the password from the first line of stdin. The password is **never** accepted as a flag (it would show in the process list). |
| `--clients <list>` | Comma-separated subset of `claude`, `cline`, `roo`. Default `claude`. |
| `--model <id>` | Primary model to configure. Default: first allocated model. |
| `--new-key` | Always mint a new key instead of offering to reuse the stored one. |
| `-y, --yes` | Never prompt; use the provided values and defaults. Requires `--email` and `--password-stdin`. Accounts that need a one-time code must run interactively. |

Fully scripted example (CI, onboarding scripts, MDM):

```sh
echo "$APUS_PASSWORD" | npx apus-ai setup \
  --api https://ai.example.com \
  --email me@example.com --password-stdin \
  --clients claude,cline,roo --model claude-opus-5 --yes
```

Exit codes: `0` success, `1` gateway/login/verification failure, `2` invalid arguments.

## Environment variables

| Variable | Purpose |
| --- | --- |
| `APUS_AI_API` | Gateway base URL. Overridden by `--api`; falls back to the built-in default. |
| `YOURCOMPANY_AI_API` | Legacy name for the same setting, still honoured. |

## Files it writes

All writes are **merges**: the file is read, only the keys below are set or replaced,
everything else is preserved, and the result is written with 2-space indentation.
Files containing the key are set to mode `0600`. If an existing file is not valid JSON
(VS Code's `settings.json` often has comments or trailing commas), it is backed up to
`<file>.bak-<timestamp>` first and the wizard tells you.

### Claude Code CLI / VS Code Claude extension — `~/.claude/settings.json`

```jsonc
{
  "env": {
    "ANTHROPIC_AUTH_TOKEN": "apus_...",          // your personal key
    "ANTHROPIC_BASE_URL": "https://<gateway>",
    "ANTHROPIC_MODEL": "<primary model>",
    "ANTHROPIC_DEFAULT_OPUS_MODEL": "<allocated opus, if any>",
    "ANTHROPIC_DEFAULT_SONNET_MODEL": "<allocated sonnet, if any>",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL": "<allocated haiku, else first model>",
    "ANTHROPIC_SMALL_FAST_MODEL": "<allocated haiku, else first model>",
    "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1"
  },
  "hasCompletedOnboarding": true
}
```

Model keys that do not apply (e.g. no opus allocated) are removed if a previous run set
them. If the file also has `env.ANTHROPIC_API_KEY`, the wizard warns you to remove it.

### Cline and Roo Code — VS Code user `settings.json`

| OS | Path |
| --- | --- |
| macOS | `~/Library/Application Support/Code/User/settings.json` |
| Linux | `~/.config/Code/User/settings.json` (respects `XDG_CONFIG_HOME`) |
| Windows | `%APPDATA%\Code\User\settings.json` |

If the stable file does not exist but a VS Code Insiders one does (`Code - Insiders`),
the Insiders file is used instead.

Keys written: `cline.apiProvider = "anthropic"`, `cline.anthropicBaseUrl`, `cline.apiKey`
for Cline; `roo-cline.apiProvider`, `roo-cline.anthropicBaseUrl`, `roo-cline.apiKey` for
Roo Code.

### Credentials store

Session tokens and the personal key are stored in the OS keychain (macOS Keychain,
libsecret, Windows Credential Vault) under the service `apus-ai`. Where no keychain is
available (headless CI), they go to `~/.apus-ai/credentials.json` with mode `0600`.

## Undo

1. `npx apus-ai keys list`, then `npx apus-ai keys revoke <id>` for the key this machine
   uses (marked `●`). Everything configured with that key stops working immediately.
2. Remove the keys listed above from `~/.claude/settings.json` (the `env.ANTHROPIC_*` and
   `env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` entries) and the `cline.*` /
   `roo-cline.*` entries from VS Code's `settings.json`. If the wizard made a
   `.bak-<timestamp>` copy you can restore that instead.
3. `npx apus-ai logout` to clear the stored session and key.

## Development

```sh
npm install
npm run build          # tsc → dist/
node dist/index.js --help
npm run dev -- setup   # run from source with tsx
```
