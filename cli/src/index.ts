#!/usr/bin/env node
import { Command } from "commander";
import { login, logout } from "./commands/login.js";
import { chat } from "./commands/chat.js";
import { usage, sessions, models } from "./commands/usage.js";

// API base resolves from flag → env → default. Lets enterprises point the same
// published package at their own gateway without forking.
const API_BASE = process.env.YOURCOMPANY_AI_API ?? "https://ai-gateway.yourcompany.com";

const program = new Command();
program
  .name("yourcompany-ai")
  .description("Controlled, tracked, quota-enforced AI access for your organization.")
  .version("0.1.0")
  .option("--api <url>", "Gateway base URL", API_BASE);

program.command("login").description("Authenticate and store a session token securely")
  .action(() => login(program.opts().api));

program.command("logout").description("Clear local credentials")
  .action(() => logout());

program.command("chat").description("Interactive AI chat (streamed through the gateway)")
  .option("-m, --model <model>", "Model to use", "claude-sonnet-4-6")
  .option("-p, --prompt <text>", "One-shot prompt (non-interactive)")
  .action((o) => chat({ model: o.model, once: o.prompt }));

program.command("usage").description("Show your token usage and remaining quota")
  .action(() => usage());

program.command("models").description("List models enabled for your workspace")
  .action(() => models());

program.command("sessions").description("List your active device sessions")
  .action(() => sessions());

program.parseAsync();
