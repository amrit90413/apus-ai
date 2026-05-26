import { homedir } from "node:os";
import { join } from "node:path";
import { mkdir, readFile, writeFile, chmod } from "node:fs/promises";

// keytar is a native, optional module; load it lazily so the CLI still runs (with
// file fallback) on systems where libsecret/Keychain bindings aren't available.
// Typed loosely on purpose so the build doesn't require the native package present.
interface KeytarLike {
  setPassword(service: string, account: string, password: string): Promise<void>;
  getPassword(service: string, account: string): Promise<string | null>;
  deletePassword(service: string, account: string): Promise<boolean>;
}
let keytarPromise: Promise<KeytarLike | null> | undefined;
function getKeytar(): Promise<KeytarLike | null> {
  keytarPromise ??= import("keytar" as string)
    .then((m: any) => (m.default ?? m) as KeytarLike)
    .catch(() => null);
  return keytarPromise;
}

const SERVICE = "yourcompany-ai";
const ACCOUNT = "default";
const FALLBACK_DIR = join(homedir(), ".yourcompany-ai");
const FALLBACK_FILE = join(FALLBACK_DIR, "credentials.json");

export interface Credentials {
  apiBase: string;
  accessToken: string;
  refreshToken: string;
  accessExpiresAt: string; // ISO
}

// Prefer the OS keychain (Keychain / libsecret / Credential Vault). Fall back to a
// 0600 file when keytar's native module is unavailable (e.g. headless CI).
export async function saveCredentials(creds: Credentials): Promise<void> {
  const payload = JSON.stringify(creds);
  const kt = await getKeytar();
  try {
    if (!kt) throw new Error("keytar unavailable");
    await kt.setPassword(SERVICE, ACCOUNT, payload);
  } catch {
    await mkdir(FALLBACK_DIR, { recursive: true });
    await writeFile(FALLBACK_FILE, payload, "utf8");
    await chmod(FALLBACK_FILE, 0o600);
  }
}

export async function loadCredentials(): Promise<Credentials | null> {
  try {
    const kt = await getKeytar();
    const raw = kt ? await kt.getPassword(SERVICE, ACCOUNT) : null;
    if (raw) return JSON.parse(raw);
  } catch { /* fall through to file */ }
  try {
    return JSON.parse(await readFile(FALLBACK_FILE, "utf8"));
  } catch {
    return null;
  }
}

export async function clearCredentials(): Promise<void> {
  try { const kt = await getKeytar(); if (kt) await kt.deletePassword(SERVICE, ACCOUNT); } catch { /* ignore */ }
  try { await writeFile(FALLBACK_FILE, "", "utf8"); } catch { /* ignore */ }
}

// Returns a valid access token, transparently refreshing if it has < 60s left.
export async function getValidAccessToken(creds: Credentials): Promise<string> {
  const expiresIn = new Date(creds.accessExpiresAt).getTime() - Date.now();
  if (expiresIn > 60_000) return creds.accessToken;

  const res = await fetch(`${creds.apiBase}/api/v1/auth/refresh`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken: creds.refreshToken }),
  });
  if (!res.ok) throw new Error("Session expired. Run `yourcompany-ai login` again.");

  const data = (await res.json()) as {
    accessToken: string; refreshToken: string; accessExpiresAt: string;
  };
  const updated: Credentials = { ...creds, ...data };
  await saveCredentials(updated);
  return updated.accessToken;
}

// Convenience: load credentials or exit with a clear message. Returns a non-null
// Credentials so callers don't need their own null checks.
export async function requireCredentials(): Promise<Credentials> {
  const creds = await loadCredentials();
  if (!creds) {
    console.error("Not logged in. Run `yourcompany-ai login`.");
    process.exit(1);
  }
  return creds;
}
