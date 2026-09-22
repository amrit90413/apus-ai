import pc from "picocolors";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";
import { request } from "../lib/http.js";

interface KeyRecord {
  id: string; name: string; prefix: string;
  createdAt: string; lastUsedAt: string | null; expiresAt: string | null; revokedAt: string | null;
}

const fmtDate = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "—");

export async function keysList(opts: { json?: boolean }): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await request<{ keys: KeyRecord[] }>(`${creds.apiBase}/api/v1/me/keys`, { token });
  if (!res.ok) { console.error(pc.red(`Could not list keys (HTTP ${res.status}): ${res.error?.message ?? ""}`)); process.exit(1); }

  const keys = res.data?.keys ?? [];
  if (opts.json) { console.log(JSON.stringify(keys, null, 2)); return; }

  if (keys.length === 0) { console.log(pc.dim("\nNo personal keys. Run `npx apus-ai` to create one.\n")); return; }
  console.log(pc.bold("\nYour personal keys:\n"));
  for (const k of keys) {
    const mine = creds.apiKey && k.prefix && creds.apiKey.startsWith(k.prefix);
    const state = k.revokedAt ? pc.red("revoked") : k.expiresAt && new Date(k.expiresAt) < new Date() ? pc.yellow("expired") : pc.green("active");
    console.log(`  ${mine ? pc.green("●") : "○"} ${k.prefix.padEnd(14)} ${state}  ${k.name}`);
    console.log(pc.dim(`    id ${k.id} · created ${fmtDate(k.createdAt)} · last used ${fmtDate(k.lastUsedAt)}` +
      (k.expiresAt ? ` · expires ${fmtDate(k.expiresAt)}` : "")));
  }
  console.log(pc.dim("\n  ● = the key this machine was set up with\n"));
}

export async function keysRevoke(id: string): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await request<null>(`${creds.apiBase}/api/v1/me/keys/${encodeURIComponent(id)}`, { method: "DELETE", token });
  if (res.status === 404) { console.error(pc.red(`No key with id ${id}.`)); process.exit(1); }
  if (!res.ok) { console.error(pc.red(`Could not revoke key (HTTP ${res.status}): ${res.error?.message ?? ""}`)); process.exit(1); }
  console.log(pc.green(`Key ${id} revoked.`) + pc.dim(" Clients using it will get 401 until you run `npx apus-ai` again."));
}
