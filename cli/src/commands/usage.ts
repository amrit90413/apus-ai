import pc from "picocolors";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";
import { request } from "../lib/http.js";

interface UsageResponse {
  windows?: { name: string; used: number; limit: number; resetInSeconds: number }[];
  balance?: { enforced: boolean; remaining: number | null };
}

export async function usage(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);

  const res = await request<UsageResponse>(`${creds.apiBase}/api/v1/me/usage`, { token });
  if (!res.ok) { console.error(pc.red(`Could not fetch usage (HTTP ${res.status}).`)); process.exit(1); }

  const windows = res.data?.windows ?? [];
  const balance = res.data?.balance;

  console.log(pc.bold("\nYour quota windows:\n"));
  if (windows.length === 0) console.log(pc.dim("  No rolling windows configured."));
  for (const w of windows) {
    const pct = w.limit > 0 ? Math.min(100, Math.round((w.used / w.limit) * 100)) : 0;
    const mins = Math.ceil(w.resetInSeconds / 60);
    const bar = "█".repeat(Math.round(pct / 5)).padEnd(20, "░");
    const color = pct > 90 ? pc.red : pct > 70 ? pc.yellow : pc.green;
    console.log(`  ${w.name.padEnd(12)} ${color(bar)} ${pct}%  ` +
      pc.dim(`${w.used.toLocaleString()}/${w.limit.toLocaleString()} · resets in ${mins}m`));
  }

  if (balance?.enforced) {
    const remaining = balance.remaining ?? 0;
    const color = remaining <= 0 ? pc.red : remaining < 100_000 ? pc.yellow : pc.green;
    console.log(pc.bold("\nPrepaid balance:\n"));
    console.log(`  ${color(`${remaining.toLocaleString()} tokens remaining`)}` +
      (remaining <= 0 ? pc.dim("  — ask your admin to top up") : ""));
  }
  console.log();
}

export async function sessions(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await request<{ sessions: { deviceName: string; lastIp: string; createdAt: string; current: boolean }[] }>(
    `${creds.apiBase}/api/v1/me/sessions`, { token });
  if (!res.ok) { console.error(pc.red(`Could not fetch sessions (HTTP ${res.status}).`)); process.exit(1); }
  console.log(pc.bold("\nActive sessions:\n"));
  for (const s of res.data?.sessions ?? []) {
    console.log(`  ${s.current ? pc.green("●") : "○"} ${s.deviceName.padEnd(28)} ` +
      pc.dim(`${s.lastIp} · since ${new Date(s.createdAt).toLocaleString()}`));
  }
  console.log();
}

export async function models(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await request<{ models: string[] }>(`${creds.apiBase}/api/v1/me/models`, { token });
  if (!res.ok) { console.error(pc.red(`Could not fetch models (HTTP ${res.status}).`)); process.exit(1); }
  const list = res.data?.models ?? [];
  console.log(pc.bold("\nModels allocated to you:\n"));
  if (list.length === 0) console.log(pc.yellow("  None yet — ask your admin to allocate models."));
  list.forEach(m => console.log(`  • ${m}`));
  console.log();
}
