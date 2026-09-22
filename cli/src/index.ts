#!/usr/bin/env node
import { Command } from "commander";
import { login, logout } from "./commands/login.js";
import { chat } from "./commands/chat.js";
import { usage, sessions, models } from "./commands/usage.js";
import { setup } from "./commands/setup.js";
import { keysList, keysRevoke } from "./commands/keys.js";
import { providersList, providersTest } from "./commands/providers.js";

// Gateway URL resolves from flag → env → default. `APUS_AI_API` is the env var;
// `YOURCOMPANY_AI_API` is still honoured for installs configured before the rename.
const DEFAULT_API = "https://ai-gateway.yourcompany.com";
const ENV_API = process.env.APUS_AI_API ?? process.env.YOURCOMPANY_AI_API;

const program = new Command();
program
  .name("apus-ai")
  .description("Connect Claude Code, Cline and Roo Code to your organization's AI gateway.")
  .version("0.2.0")
  .option("--api <url>", "Gateway base URL (env: APUS_AI_API)", ENV_API ?? DEFAULT_API)
  .configureHelp({ showGlobalOptions: true });

// The gateway URL the user *explicitly* chose (flag or env); undefined → the wizard prompts.
function explicitApi(): string | undefined {
  return program.getOptionValueSource("api") === "cli" || ENV_API ? program.opts().api : undefined;
}

program.command("setup", { isDefault: true })
  .description("Sign in, mint a personal key and configure Claude Code / Cline / Roo Code (default)")
  .option("--email <email>", "Account email (skips the prompt)")
  .option("--password-stdin", "Read the password from stdin (never pass it as a flag)")
  .option("--clients <list>", "Comma-separated: claude,cline,roo (default: claude)")
  .option("--model <id>", "Primary model to configure (default: first allocated)")
  .option("--new-key", "Always mint a new key instead of offering to reuse the stored one")
  .option("-y, --yes", "Non-interactive: never prompt, use provided values and defaults")
  .addHelpText("after", `
Examples:
  $ npx apus-ai
  $ npx apus-ai setup --api https://ai.example.com --clients claude,cline
  $ echo "$PASSWORD" | npx apus-ai setup --api https://ai.example.com --email me@example.com --password-stdin --clients claude --yes`)
  .action((o) => setup({
    api: explicitApi(),
    apiDefault: program.opts().api,
    email: o.email,
    passwordStdin: o.passwordStdin,
    clients: o.clients,
    model: o.model,
    newKey: o.newKey,
    yes: o.yes,
  }));

program.command("login").description("Sign in only (no client configuration) and store the session securely")
  .action(() => login(program.opts().api));

program.command("logout").description("Clear local credentials (client config files are left in place)")
  .action(() => logout());

const keys = program.command("keys").description("Manage your personal gateway keys");
keys.command("list", { isDefault: true }).description("List your keys")
  .option("--json", "Output raw JSON")
  .action((o) => keysList({ json: o.json }));
keys.command("revoke <id>").description("Revoke a key by id (see `keys list`)")
  .action((id: string) => keysRevoke(id));

program.command("chat").description("Interactive AI chat (streamed through the gateway)")
  .option("-m, --model <model>", "Model to use", "claude-opus-5")
  .option("-p, --prompt <text>", "One-shot prompt (non-interactive)")
  .action((o) => chat({ model: o.model, once: o.prompt }));

program.command("usage").description("Show your monthly allowance, quota windows and prepaid balance")
  .action(() => usage());

program.command("models").description("List the models you can use and which provider serves each")
  .action(() => models());

const providers = program.command("providers")
  .description("Admin: the AI providers your organization has connected");
providers.command("list", { isDefault: true }).description("Show every provider, its connection state and this month's spend")
  .action(() => providersList());
providers.command("test <connectionId>").description("Run the connection test against a provider (see `providers list`)")
  .action((id: string) => providersTest(id));

program.command("sessions").description("List your active device sessions")
  .action(() => sessions());

program.parseAsync();
