import pc from "picocolors";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";

export async function usage(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);

  const res = await fetch(`${creds.apiBase}/api/v1/me/usage`, {
    headers: { authorization: `Bearer ${token}` },
  });
  if (!res.ok) { console.error(pc.red("Could not fetch usage.")); process.exit(1); }

  const data = await res.json() as {
    windows: { name: string; used: number; limit: number; resetInSeconds: number }[];
  };
  console.log(pc.bold("\nYour quota windows:\n"));
  for (const w of data.windows) {
    const pct = Math.round((w.used / w.limit) * 100);
    const mins = Math.ceil(w.resetInSeconds / 60);
    const bar = "█".repeat(Math.round(pct / 5)).padEnd(20, "░");
    const color = pct > 90 ? pc.red : pct > 70 ? pc.yellow : pc.green;
    console.log(`  ${w.name.padEnd(12)} ${color(bar)} ${pct}%  ` +
      pc.dim(`${w.used.toLocaleString()}/${w.limit.toLocaleString()} · resets in ${mins}m`));
  }
  console.log();
}

export async function sessions(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await fetch(`${creds.apiBase}/api/v1/me/sessions`, {
    headers: { authorization: `Bearer ${token}` },
  });
  const data = await res.json() as { sessions: { deviceName: string; lastIp: string; createdAt: string; current: boolean }[] };
  console.log(pc.bold("\nActive sessions:\n"));
  for (const s of data.sessions) {
    console.log(`  ${s.current ? pc.green("●") : "○"} ${s.deviceName.padEnd(20)} ` +
      pc.dim(`${s.lastIp} · since ${new Date(s.createdAt).toLocaleString()}`));
  }
  console.log();
}

export async function models(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);
  const res = await fetch(`${creds.apiBase}/api/v1/me/models`, {
    headers: { authorization: `Bearer ${token}` },
  });
  const data = await res.json() as { models: string[] };
  console.log(pc.bold("\nModels enabled for your workspace:\n"));
  data.models.forEach(m => console.log(`  • ${m}`));
  console.log();
}
