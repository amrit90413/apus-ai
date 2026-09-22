import pc from "picocolors";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";
import { request } from "../lib/http.js";
import { money, bar } from "../lib/money.js";

interface UsageResponse {
  windows?: { name: string; used: number; limit: number; resetInSeconds: number }[];
  balance?: { enforced: boolean; remaining: number | null };
}

interface MyAiResponse {
  currency: string;
  allowance: {
    unlimited: boolean;
    budgetMinor: number;
    usedMinor: number;
    remainingMinor: number | null;
    resetsAt: string;
  };
  usage: { requests: number; tokens: number };
  access: { status: string; organizationAiEnabled: boolean };
  models: { id: string; providers: string[] }[];
}

export async function usage(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);

  const res = await request<UsageResponse>(`${creds.apiBase}/api/v1/me/usage`, { token });
  if (!res.ok) { console.error(pc.red(`Could not fetch usage (HTTP ${res.status}).`)); process.exit(1); }

  // The monthly money allowance is the headline number; a gateway that predates it
  // simply 404s here and the rest of the output is unaffected.
  const ai = await request<MyAiResponse>(`${creds.apiBase}/api/v1/me/ai`, { token });
  if (ai.ok && ai.data) printAllowance(ai.data);

  const windows = res.data?.windows ?? [];
  const balance = res.data?.balance;

  console.log(pc.bold("\nYour quota windows:\n"));
  if (windows.length === 0) console.log(pc.dim("  No rolling windows configured."));
  for (const w of windows) {
    const pct = w.limit > 0 ? Math.min(100, Math.round((w.used / w.limit) * 100)) : 0;
    const mins = Math.ceil(w.resetInSeconds / 60);
    const color = pct > 90 ? pc.red : pct > 70 ? pc.yellow : pc.green;
    console.log(`  ${w.name.padEnd(12)} ${color(bar(pct))} ${pct}%  ` +
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

  // /me/ai knows which provider actually serves each model; /me/models is the
  // fallback for a gateway that predates multi-provider routing.
  const ai = await request<MyAiResponse>(`${creds.apiBase}/api/v1/me/ai`, { token });
  if (ai.ok && ai.data) {
    const list = ai.data.models;
    console.log(pc.bold("\nModels you can use:\n"));
    if (list.length === 0) {
      console.log(pc.yellow("  None reachable — either no models are allocated to you,"));
      console.log(pc.yellow("  or your organization has not connected a provider that serves them."));
    }
    for (const m of list) console.log(`  • ${m.id.padEnd(30)} ${pc.dim(`via ${m.providers.join(", ")}`)}`);
    console.log();
    return;
  }

  const res = await request<{ models: string[] }>(`${creds.apiBase}/api/v1/me/models`, { token });
  if (!res.ok) { console.error(pc.red(`Could not fetch models (HTTP ${res.status}).`)); process.exit(1); }
  const list = res.data?.models ?? [];
  console.log(pc.bold("\nModels allocated to you:\n"));
  if (list.length === 0) console.log(pc.yellow("  None yet — ask your admin to allocate models."));
  list.forEach(m => console.log(`  • ${m}`));
  console.log();
}

/** The monthly allowance, what is left, and when it resets. */
function printAllowance(ai: MyAiResponse): void {
  const { allowance, usage: used, access, currency } = ai;

  console.log(pc.bold("\nMonthly AI allowance:\n"));

  if (allowance.unlimited) {
    console.log(`  ${pc.green("Unlimited")}  ${pc.dim(`${money(allowance.usedMinor, currency)} used this month`)}`);
  } else {
    const pct = allowance.budgetMinor > 0
      ? Math.min(100, Math.round((allowance.usedMinor / allowance.budgetMinor) * 100))
      : 0;
    const color = pct >= 100 ? pc.red : pct >= 90 ? pc.yellow : pc.green;
    console.log(`  ${color(bar(pct))} ${pct}%`);
    console.log(`  ${money(allowance.usedMinor, currency)} of ${money(allowance.budgetMinor, currency)} used · ` +
      `${color(`${money(allowance.remainingMinor, currency)} left`)}`);
  }

  console.log(pc.dim(`  ${used.requests.toLocaleString()} requests · ${used.tokens.toLocaleString()} tokens · ` +
    `resets ${new Date(allowance.resetsAt).toLocaleDateString(undefined, { day: "numeric", month: "long" })}`));

  if (!allowance.unlimited && allowance.remainingMinor === 0)
    console.log(pc.red("  Allowance used up — ask your admin to top it up, or wait for the reset."));
  if (access.status !== "active")
    console.log(pc.yellow(`  Your AI access is ${access.status}. Contact your administrator.`));
  if (!access.organizationAiEnabled)
    console.log(pc.yellow("  AI access is turned off for your organization."));
}
