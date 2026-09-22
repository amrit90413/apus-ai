import pc from "picocolors";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";
import { request } from "../lib/http.js";
import { money } from "../lib/money.js";

interface Connection {
  id: string;
  provider: string;
  providerDisplayName: string;
  connectionType: string;
  status: string;
  hint: string;
  lastValidatedAt: string | null;
  lastFailureReason: string | null;
}

interface ProviderRow {
  provider: string;
  displayName: string;
  connection: Connection | null;
  month: { requests: number; tokens: number; customerCostMinor: number; failures: number };
}

const STATUS_TEXT: Record<string, (s: string) => string> = {
  connected: pc.green,
  refreshing: pc.green,
  expired: pc.yellow,
  error: pc.yellow,
  reauthentication_required: pc.red,
  disabled: pc.dim,
  revoked: pc.dim,
  disconnected: pc.dim,
};

/**
 * `apus-ai providers` — what the organization has connected, for an admin who
 * lives in a terminal. Shows state and spend; never a credential.
 */
export async function providersList(): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);

  const res = await request<{ currency: string; providers: ProviderRow[] }>(
    `${creds.apiBase}/api/v1/admin/ai/providers`, { token });

  if (res.status === 403) {
    console.error(pc.red("You need provider admin rights to see this."));
    process.exit(1);
  }
  if (!res.ok) {
    console.error(pc.red(`Could not fetch providers (${res.error?.message ?? `HTTP ${res.status}`}).`));
    process.exit(1);
  }

  const { currency, providers } = res.data!;
  console.log(pc.bold("\nAI providers\n"));

  for (const row of providers) {
    const connection = row.connection;
    const status = connection?.status ?? "disconnected";
    const paint = STATUS_TEXT[status] ?? pc.dim;

    console.log(`  ${row.displayName.padEnd(18)} ${paint(status.replace(/_/g, " "))}`);
    if (connection) {
      console.log(pc.dim(`    ${connection.connectionType} · ${connection.hint}` +
        (connection.lastValidatedAt ? ` · checked ${new Date(connection.lastValidatedAt).toLocaleString()}` : "")));
      if (connection.lastFailureReason && status !== "connected")
        console.log(pc.yellow(`    ${connection.lastFailureReason}`));
    }
    if (row.month.requests > 0) {
      console.log(pc.dim(`    this month: ${row.month.requests.toLocaleString()} requests · ` +
        `${row.month.tokens.toLocaleString()} tokens · ${money(row.month.customerCostMinor, currency)}` +
        (row.month.failures > 0 ? ` · ${row.month.failures} failed` : "")));
    }
    console.log();
  }

  const connected = providers.filter(p => p.connection?.status === "connected").length;
  if (connected === 0)
    console.log(pc.yellow("  Nothing connected yet. Connect one at /settings/ai-providers.\n"));
}

/** `apus-ai providers test <id>` — the same probe the dashboard's Test button runs. */
export async function providersTest(id: string): Promise<void> {
  const creds = await requireCredentials();
  const token = await getValidAccessToken(creds);

  const res = await request<{ ok: boolean; message: string }>(
    `${creds.apiBase}/api/v1/provider-connections/${encodeURIComponent(id)}/validate`,
    { method: "POST", body: {}, token, timeoutMs: 30_000 });

  if (!res.ok) {
    console.error(pc.red(`Could not test the connection (${res.error?.message ?? `HTTP ${res.status}`}).`));
    process.exit(1);
  }

  const { ok, message } = res.data!;
  console.log(ok ? pc.green(`✓ ${message}`) : pc.red(`✕ ${message}`));
  if (!ok) process.exit(1);
}
