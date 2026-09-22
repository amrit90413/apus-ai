import pc from "picocolors";
import ora from "ora";
import { hostname } from "node:os";
import { saveCredentials, clearCredentials, loadCredentials } from "../lib/credentials.js";
import { login as apiLogin, verifyOtp } from "../lib/auth.js";
import { ask, isValidEmail } from "../lib/prompt.js";
import { normalizeApiBase, NetworkError } from "../lib/http.js";

// Plain sign-in (no client configuration). `npx apus-ai` (setup) does this and more.
export async function login(rawApiBase: string): Promise<void> {
  const apiBase = normalizeApiBase(rawApiBase);
  const email = (await ask<string>({
    type: "text", name: "email", message: "Work email",
    validate: v => isValidEmail(v) || "Enter a valid email address",
  })).trim();
  const password = await ask<string>({ type: "password", name: "password", message: "Password" });

  const spin = ora(`Signing in to ${apiBase}`).start();
  let result;
  try {
    result = await apiLogin(apiBase, email, password, hostname());
  } catch (err) {
    spin.fail(err instanceof NetworkError ? err.message : String(err));
    process.exit(1);
  }

  let tokens;
  if (result.kind === "ok") {
    tokens = result.tokens;
  } else if (result.kind === "otp_required") {
    spin.succeed("Password accepted — one-time code required");
    if (result.message) console.log(pc.dim(`  ${result.message}`));
    const otp = (await ask<string>({ type: "text", name: "otp", message: "One-time code" })).trim();
    const v = await verifyOtp(apiBase, result.pendingToken, otp);
    if (v.kind !== "ok") { console.error(pc.red(v.message)); process.exit(1); }
    tokens = v.tokens;
  } else if (result.kind === "invalid_credentials") {
    spin.fail("Invalid email or password."); process.exit(1);
  } else if (result.kind === "rate_limited") {
    spin.fail("Too many attempts — wait a minute and try again."); process.exit(1);
  } else {
    spin.fail(`Login failed (HTTP ${result.status}): ${result.message}`); process.exit(1);
  }

  // Keep a previously minted key when it belongs to the same account on the same gateway.
  const previous = await loadCredentials();
  const keepKey = previous?.apiKey && previous.apiBase === apiBase && previous.email === email;
  await saveCredentials({
    apiBase, email, ...tokens,
    ...(keepKey ? { apiKey: previous!.apiKey, apiKeyPrefix: previous!.apiKeyPrefix } : {}),
  });
  if (spin.isSpinning) spin.succeed(`Signed in as ${email}`);
  console.log(pc.green("Logged in. Session stored in your OS keychain.") +
    pc.dim(" Run `npx apus-ai` to configure Claude Code / Cline / Roo Code."));
}

export async function logout(): Promise<void> {
  await clearCredentials();
  console.log(pc.green("Logged out. Local credentials cleared.") +
    pc.dim(" Client config files (~/.claude/settings.json, VS Code settings) were left as-is."));
}
