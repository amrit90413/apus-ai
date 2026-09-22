import pc from "picocolors";
import ora from "ora";
import { hostname } from "node:os";
import { request, normalizeApiBase, NetworkError } from "../lib/http.js";
import { login as apiLogin, verifyOtp, type Tokens } from "../lib/auth.js";
import { loadCredentials, saveCredentials, type Credentials } from "../lib/credentials.js";
import { ask, isValidEmail, maskKey, readLineFromStdin } from "../lib/prompt.js";
import {
  CLIENTS, clientTitle, parseClientList, pickModelDefaults,
  writeClaudeSettings, writeVsCodeSettings,
  type ClientId, type GatewayConfig, type WriteResult,
} from "../lib/clients.js";

export interface SetupOptions {
  api?: string;          // explicit gateway URL from --api / env (undefined → prompt or default)
  apiDefault: string;    // placeholder shown in the prompt
  email?: string;
  passwordStdin?: boolean;
  clients?: string;      // "claude,cline,roo"
  model?: string;        // primary model override
  yes?: boolean;         // never prompt; use provided values + defaults
  newKey?: boolean;      // always mint a fresh key even if one is stored
}

const MAX_ATTEMPTS = 3;

class SetupError extends Error {
  constructor(message: string, public readonly exitCode = 1) { super(message); }
}

interface KeyRecord { id: string; name: string; prefix: string; createdAt: string; lastUsedAt: string | null; expiresAt: string | null; revokedAt: string | null }
interface MintedKey { id: string; name: string; key: string; prefix: string; createdAt: string; expiresAt: string | null }

export async function setup(opts: SetupOptions): Promise<void> {
  try {
    await runSetup(opts);
  } catch (err) {
    if (err instanceof SetupError) { console.error(pc.red(`\n${err.message}`)); process.exit(err.exitCode); }
    if (err instanceof NetworkError) { console.error(pc.red(`\n${err.message}`)); process.exit(1); }
    throw err;
  }
}

async function runSetup(opts: SetupOptions): Promise<void> {
  const interactive = !opts.yes && Boolean(process.stdin.isTTY) && !opts.passwordStdin;

  console.log(pc.bold("\napus-ai setup") + pc.dim("  ·  connect your AI tools to your organization's gateway\n"));

  if (!interactive) {
    if (!opts.email) throw new SetupError("Non-interactive mode: --email is required.", 2);
    if (!opts.passwordStdin) throw new SetupError("Non-interactive mode: pass the password on stdin with --password-stdin.", 2);
  }
  // Validate flag values before touching the network so mistakes fail fast.
  let presetClients: ClientId[] | undefined;
  if (opts.clients) {
    try { presetClients = parseClientList(opts.clients); } catch (e) { throw new SetupError((e as Error).message, 2); }
  }

  // a. Gateway ----------------------------------------------------------------
  const gateway = await resolveGateway(opts, interactive);
  await checkReachable(gateway);

  // b. Login ------------------------------------------------------------------
  const email = (opts.email ?? await ask<string>({
    type: "text", name: "email", message: "Work email",
    validate: v => isValidEmail(v) || "Enter a valid email address",
  })).trim();
  if (!isValidEmail(email)) throw new SetupError(`"${email}" is not a valid email address.`, 2);

  const previous = await loadCredentials();
  const tokens = await performLogin(gateway, email, opts, interactive);
  let creds: Credentials = {
    apiBase: gateway, email, ...tokens,
    // keep a previously minted key only when it clearly belongs to this account
    ...(previous?.apiKey && previous.apiBase === gateway && previous.email === email
      ? { apiKey: previous.apiKey, apiKeyPrefix: previous.apiKeyPrefix } : {}),
  };
  await saveCredentials(creds);

  // d. Models -----------------------------------------------------------------
  const models = await fetchModels(gateway, creds.accessToken);
  const primary = await choosePrimaryModel(models, opts, interactive);
  const modelDefaults = pickModelDefaults(models, primary);

  // e. Clients ----------------------------------------------------------------
  const clients = presetClients ?? await chooseClients(interactive);

  // c. Personal key (after clients so the key name can describe where it's used)
  const { key, prefix } = await obtainKey(gateway, creds, clients, opts, interactive);
  creds = { ...creds, apiKey: key, apiKeyPrefix: prefix ?? undefined };
  await saveCredentials(creds);

  // f. Write configs ----------------------------------------------------------
  const cfg: GatewayConfig = { gateway, apiKey: key, models: modelDefaults };
  const written = await writeConfigs(cfg, clients);

  // g. Verify -----------------------------------------------------------------
  const ok = await verifyKey(gateway, key);

  // h. Summary ----------------------------------------------------------------
  printSummary(gateway, email, clients, modelDefaults, written, maskKey(key, prefix));
  process.exit(ok ? 0 : 1);
}

// ---------------------------------------------------------------------------

async function resolveGateway(opts: SetupOptions, interactive: boolean): Promise<string> {
  if (opts.api) return normalizeApiBase(opts.api);
  if (!interactive) return normalizeApiBase(opts.apiDefault);
  const url = await ask<string>({
    type: "text", name: "gateway", message: "Gateway URL", initial: opts.apiDefault,
    validate: v => /^(https?:\/\/)?[^\s/]+/.test(v.trim()) || "Enter the gateway URL, e.g. https://ai.example.com",
  });
  return normalizeApiBase(url);
}

async function checkReachable(gateway: string): Promise<void> {
  const spin = ora(`Checking ${gateway}`).start();
  try {
    const res = await request<{ enabled?: boolean }>(`${gateway}/api/v1/auth/register`, { timeoutMs: 10_000 });
    if (res.status >= 500) spin.warn(`Gateway reachable but returned HTTP ${res.status} — continuing`);
    else spin.succeed(`Gateway reachable: ${gateway}`);
  } catch (err) {
    spin.fail(err instanceof NetworkError ? err.message : `Could not reach ${gateway}`);
    throw new SetupError("Check the URL (or --api / APUS_AI_API) and your network, then run `npx apus-ai` again.");
  }
}

async function performLogin(gateway: string, email: string, opts: SetupOptions, interactive: boolean): Promise<Tokens> {
  const deviceName = `${hostname()} (apus-ai setup)`;
  let password = opts.passwordStdin ? await readLineFromStdin() : "";
  if (opts.passwordStdin && !password) throw new SetupError("No password received on stdin.", 2);

  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    if (!opts.passwordStdin) {
      password = await ask<string>({ type: "password", name: "password", message: "Password" });
      if (!password) throw new SetupError("Password is required.", 2);
    }

    const spin = ora(`Signing in as ${email}`).start();
    const result = await apiLogin(gateway, email, password, deviceName);
    password = opts.passwordStdin ? password : ""; // don't keep a typed password around longer than needed

    switch (result.kind) {
      case "ok":
        spin.succeed(`Signed in as ${email}`);
        return result.tokens;
      case "otp_required":
        spin.succeed(`Password accepted — one-time code required`);
        if (!interactive) throw new SetupError("This account requires a one-time code. Run `npx apus-ai` interactively.");
        return verifyOtpLoop(gateway, result.pendingToken, result.message);
      case "invalid_credentials":
        spin.fail("Invalid email or password");
        if (!interactive) throw new SetupError("Login failed: invalid credentials.");
        if (attempt < MAX_ATTEMPTS) console.log(pc.dim(`  Try again (${MAX_ATTEMPTS - attempt} attempt${MAX_ATTEMPTS - attempt === 1 ? "" : "s"} left).`));
        break;
      case "rate_limited": {
        const wait = result.retryAfterSeconds ? `${result.retryAfterSeconds}s` : "a minute";
        spin.fail(`Too many login attempts — wait ${wait} and try again`);
        throw new SetupError("Rate limited by the gateway.");
      }
      case "error":
        spin.fail(`Login failed (HTTP ${result.status}): ${result.message}`);
        throw new SetupError("The gateway rejected the login request. Contact your admin if this persists.");
    }
  }
  throw new SetupError("Too many failed attempts. Ask your admin to reset your password if needed.");
}

async function verifyOtpLoop(gateway: string, pendingToken: string, message: string): Promise<Tokens> {
  if (message) console.log(pc.dim(`  ${message}`));
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    const otp = (await ask<string>({ type: "text", name: "otp", message: "One-time code" })).trim();
    if (!otp) continue;
    const spin = ora("Verifying code").start();
    const result = await verifyOtp(gateway, pendingToken, otp);
    if (result.kind === "ok") { spin.succeed("Code accepted"); return result.tokens; }
    if (result.kind === "invalid_otp") { spin.fail(result.message); continue; }
    spin.fail(`Verification failed (HTTP ${result.status}): ${result.message}`);
    throw new SetupError("Could not verify the one-time code.");
  }
  throw new SetupError("Too many incorrect codes. Start again with `npx apus-ai`.");
}

async function fetchModels(gateway: string, token: string): Promise<string[]> {
  const spin = ora("Fetching your allocated models").start();
  const res = await request<{ models: string[] }>(`${gateway}/api/v1/me/models`, { token });
  if (!res.ok) {
    spin.fail(`Could not fetch models (HTTP ${res.status}): ${res.error?.message ?? ""}`);
    throw new SetupError("Ask your admin to check your account.");
  }
  const models = (res.data?.models ?? []).filter(m => typeof m === "string" && m.length > 0);
  if (models.length === 0) {
    spin.warn("Your admin has not allocated any models to you yet — config will be written without model defaults.");
  } else {
    spin.succeed(`Allocated models: ${models.join(", ")}`);
  }
  return models;
}

async function choosePrimaryModel(models: string[], opts: SetupOptions, interactive: boolean): Promise<string | undefined> {
  if (opts.model) {
    if (models.length > 0 && !models.includes(opts.model)) {
      console.log(pc.yellow(`  ! ${opts.model} is not in your allocated list — requests to it may be rejected.`));
    }
    return opts.model;
  }
  if (models.length <= 1 || !interactive) return models[0];
  return ask<string>({
    type: "select", name: "model", message: "Default model",
    choices: models.map(m => ({ title: m, value: m })),
    initial: 0,
  });
}

async function chooseClients(interactive: boolean): Promise<ClientId[]> {
  if (!interactive) return ["claude"];
  const picked = await ask<ClientId[]>({
    type: "multiselect", name: "clients", message: "Which tools should use the gateway?",
    instructions: false,
    hint: "space to toggle · enter to confirm",
    choices: CLIENTS.map((c, i) => ({ title: c.title, value: c.id, description: c.hint, selected: i === 0 })),
    min: 1,
  });
  return picked.length > 0 ? picked : ["claude"];
}

async function obtainKey(
  gateway: string, creds: Credentials, clients: ClientId[], opts: SetupOptions, interactive: boolean,
): Promise<{ key: string; prefix: string | null }> {
  const token = creds.accessToken;

  if (creds.apiKey && !opts.newKey) {
    const reusable = await findActiveKey(gateway, token, creds.apiKey);
    if (reusable) {
      const label = `${reusable.prefix}… (${reusable.name}, created ${new Date(reusable.createdAt).toLocaleDateString()})`;
      const reuse = !interactive || await ask<boolean>({
        type: "confirm", name: "reuse", message: `Reuse your existing key ${label}?`, initial: true,
      });
      if (reuse) { console.log(pc.green("✔") + ` Reusing key ${reusable.prefix}…`); return { key: creds.apiKey, prefix: reusable.prefix }; }
    }
  }

  const name = `${hostname()} · ${clients.map(clientTitle).join(", ")}`;
  const spin = ora("Creating a personal key for this machine").start();
  const res = await request<MintedKey>(`${gateway}/api/v1/me/keys`, { method: "POST", token, body: { name } });
  if (!res.ok || !res.data?.key) {
    spin.fail(`Could not create a key (HTTP ${res.status}): ${res.error?.message ?? "unexpected response"}`);
    throw new SetupError("Ask your admin to check your account.");
  }
  const prefix = res.data.prefix ?? null;
  spin.succeed(`Key created: ${maskKey(res.data.key, prefix)}  ${pc.dim("(stored in your OS keychain; never shown in full)")}`);
  return { key: res.data.key, prefix };
}

// Confirms a stored key still belongs to this account and hasn't been revoked.
async function findActiveKey(gateway: string, token: string, apiKey: string): Promise<KeyRecord | null> {
  const res = await request<{ keys: KeyRecord[] }>(`${gateway}/api/v1/me/keys`, { token });
  if (!res.ok) return null;
  return (res.data?.keys ?? []).find(k => k.prefix && apiKey.startsWith(k.prefix) && !k.revokedAt) ?? null;
}

async function writeConfigs(cfg: GatewayConfig, clients: ClientId[]): Promise<WriteResult[]> {
  const results: WriteResult[] = [];
  const report = (label: string, r: WriteResult, warnings: string[] = []) => {
    console.log(pc.green("✔") + ` ${label}: ${r.created ? "created" : "updated"} ${pc.cyan(r.path)}`);
    if (r.backup) {
      const why = r.backupReason === "invalid"
        ? "existing file was not valid JSON — started fresh"
        : "existing file had comments/trailing commas which can't be preserved";
      console.log(pc.yellow(`  ! ${why}; original saved to ${r.backup}`));
    }
    for (const w of warnings) console.log(pc.yellow(`  ! ${w}`));
  };

  if (clients.includes("claude")) {
    const r = await writeClaudeSettings(cfg);
    report("Claude Code / VS Code Claude extension", r, r.warnings);
    results.push(r);
  }
  const vscode = clients.filter(c => c === "cline" || c === "roo");
  if (vscode.length > 0) {
    const r = await writeVsCodeSettings(cfg, vscode);
    report(vscode.map(clientTitle).join(" + "), r);
    results.push(r);
  }
  return results;
}

async function verifyKey(gateway: string, key: string): Promise<boolean> {
  const spin = ora("Verifying the key against the gateway").start();
  try {
    const res = await request<{ data: { id: string }[] }>(`${gateway}/v1/models`, { apiKey: key });
    if (res.ok) {
      const ids = (res.data?.data ?? []).map(m => m.id);
      spin.succeed(ids.length > 0 ? `Gateway accepted the key · models: ${ids.join(", ")}` : "Gateway accepted the key (no models allocated yet)");
      return true;
    }
    if (res.status === 401) { spin.fail("Key rejected by the gateway (401). Re-run `npx apus-ai` to mint a new one."); return false; }
    spin.fail(`Verification failed (HTTP ${res.status})${res.error?.message ? `: ${res.error.message}` : ""}`);
    return false;
  } catch (err) {
    spin.fail(err instanceof NetworkError ? err.message : String(err));
    return false;
  }
}

function printSummary(
  gateway: string, email: string, clients: ClientId[],
  models: ReturnType<typeof pickModelDefaults>, written: WriteResult[], keyLabel: string,
): void {
  console.log(pc.bold("\nDone.\n"));
  console.log(`  Gateway   ${gateway}`);
  console.log(`  Account   ${email}`);
  console.log(`  Key       ${keyLabel}`);
  if (models.primary) {
    const aliases = [
      models.opus && `opus ${models.opus}`,
      models.sonnet && `sonnet ${models.sonnet}`,
      models.haiku && `haiku ${models.haiku}`,
    ].filter(Boolean);
    console.log(`  Model     ${models.primary}` + (aliases.length ? pc.dim(`  (${aliases.join(" · ")})`) : ""));
  } else {
    console.log(`  Model     ${pc.yellow("none allocated yet — ask your admin")}`);
  }
  console.log(`  Clients   ${clients.map(clientTitle).join(", ")}`);
  for (const w of written) console.log(pc.dim(`            ${w.path}`));
  console.log();
  if (clients.includes("claude")) console.log(`  Start ${pc.cyan("claude")} (or restart VS Code) to use the gateway.`);
  else console.log(`  Restart VS Code to pick up the new settings.`);
  console.log(`  Check your usage any time with ${pc.cyan("npx apus-ai usage")}; manage keys with ${pc.cyan("npx apus-ai keys list")}.`);
  console.log();
}
