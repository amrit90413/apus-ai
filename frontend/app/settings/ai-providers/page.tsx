"use client";
import { Suspense, useCallback, useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import { providerApi, aiAdminApi, usePolling, errorMessage, money, relativeTime, fmt } from "@/lib/api";
import type { ConnectionStatus, ProviderConnection, ProviderDescriptor, ProviderUsageRow } from "@/lib/api";
import AdminNav from "@/components/AdminNav";
import ConnectForm from "./connect-form";

/** What each connection state means for the admin, and what it looks like. */
const STATUS: Record<ConnectionStatus, { label: string; tone: string; action?: string }> = {
  connected: { label: "Connected", tone: "text-emerald-600" },
  refreshing: { label: "Refreshing", tone: "text-emerald-600" },
  connecting: { label: "Connecting", tone: "text-neutral-500" },
  expired: { label: "Expired", tone: "text-amber-600", action: "Renewing automatically — reconnect if it persists." },
  reauthentication_required: { label: "Reconnect required", tone: "text-red-600", action: "The provider no longer accepts this credential." },
  error: { label: "Error", tone: "text-amber-600", action: "Recent calls failed. Test the connection." },
  disabled: { label: "Disabled", tone: "text-neutral-400", action: "Paused by an admin." },
  revoked: { label: "Disconnected", tone: "text-neutral-400" },
  disconnected: { label: "Not connected", tone: "text-neutral-400" },
};

const METHOD_LABEL: Record<string, string> = {
  api_key: "API key",
  oauth: "OAuth",
  aws_bedrock: "AWS Bedrock",
  google_vertex: "Google Vertex",
};

/**
 * useSearchParams opts the subtree into client-side rendering, so Next requires a
 * Suspense boundary around it for the static shell to build.
 */
export default function AiProvidersPage() {
  return (
    <Suspense fallback={<div className="p-8 text-sm text-neutral-400">Loading providers…</div>}>
      <AiProviders />
    </Suspense>
  );
}

function AiProviders() {
  const params = useSearchParams();
  const [refreshKey, setRefreshKey] = useState(0);
  const [banner, setBanner] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [openForm, setOpenForm] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, { ok: boolean; message: string }>>({});

  const listLoader = useCallback(() => providerApi.list(), [refreshKey]);
  const usageLoader = useCallback(() => aiAdminApi.providers(), [refreshKey]);
  const data = usePolling(listLoader, 15000);
  const usage = usePolling(usageLoader, 20000);

  const refresh = () => setRefreshKey(k => k + 1);

  // The OAuth redirect lands back here with the outcome in the query string.
  useEffect(() => {
    const connected = params.get("connected");
    const error = params.get("error");
    if (connected) {
      setBanner({ tone: "ok", text: `${connected} connected. Your team can use it now.` });
      refresh();
    } else if (error) {
      setBanner({ tone: "error", text: oauthErrorText(error, params.get("provider")) });
    }
    if (connected || error) window.history.replaceState({}, "", window.location.pathname);
  }, [params]);

  const act = async (id: string, run: () => Promise<unknown>, success: string) => {
    setBusyId(id);
    setBanner(null);
    try {
      await run();
      setBanner({ tone: "ok", text: success });
      refresh();
    } catch (err) {
      setBanner({ tone: "error", text: errorMessage(err, "That did not work. Try again.") });
    } finally {
      setBusyId(null);
    }
  };

  const test = async (connection: ProviderConnection) => {
    setBusyId(connection.id);
    try {
      const result = await providerApi.validate(connection.id);
      setTestResults(prev => ({ ...prev, [connection.id]: result }));
      refresh();
    } catch (err) {
      setTestResults(prev => ({ ...prev, [connection.id]: { ok: false, message: errorMessage(err, "Test failed.") } }));
    } finally {
      setBusyId(null);
    }
  };

  const disconnect = (connection: ProviderConnection) => {
    const ok = window.confirm(
      `Disconnect ${connection.providerDisplayName}?\n\n` +
      "The stored credential is destroyed immediately and requests through it stop at once. " +
      "Usage history is kept. You can connect again at any time.",
    );
    if (!ok) return;
    return act(connection.id, () => providerApi.disconnect(connection.id),
      `${connection.providerDisplayName} disconnected.`);
  };

  if (!data) return <div className="p-8 text-sm text-neutral-400">Loading providers…</div>;

  const usageByProvider = new Map((usage?.providers ?? []).map(p => [p.provider, p]));
  const currency = usage?.currency ?? "USD";
  const connections = data.connections ?? [];

  return (
    <div className="mx-auto max-w-5xl p-8">
      <AdminNav />
      <header className="mb-6">
        <h1 className="text-lg font-medium">AI providers</h1>
        <p className="text-xs text-neutral-400">
          Connect the accounts your organization pays for. Everyone on your team reaches them through
          APUS — they never receive a provider credential.
        </p>
      </header>

      {banner && (
        <p
          role="status"
          className={`mb-5 rounded-lg px-4 py-2.5 text-sm ${banner.tone === "ok"
            ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-300"
            : "bg-red-50 text-red-800 dark:bg-red-950/40 dark:text-red-300"}`}
        >
          {banner.text}
        </p>
      )}

      <div className="space-y-4">
        {data.providers.map(descriptor => (
          <ProviderCard
            key={descriptor.id}
            descriptor={descriptor}
            connection={connections.find(c => c.provider === descriptor.id && c.status !== "revoked") ?? null}
            usage={usageByProvider.get(descriptor.id) ?? null}
            currency={currency}
            busyId={busyId}
            testResults={testResults}
            formOpen={openForm === descriptor.id}
            onOpenForm={() => { setOpenForm(descriptor.id); setBanner(null); }}
            onCloseForm={() => setOpenForm(null)}
            onConnected={(message) => { setOpenForm(null); setBanner({ tone: "ok", text: message }); refresh(); }}
            onTest={test}
            onDisconnect={disconnect}
            onToggle={(connection, enabled) => act(connection.id,
              () => providerApi.setEnabled(connection.id, enabled),
              `${connection.providerDisplayName} ${enabled ? "enabled" : "disabled"}.`)}
          />
        ))}
      </div>

      <p className="mt-8 border-t border-neutral-100 pt-4 text-xs text-neutral-400 dark:border-neutral-800">
        A personal Claude Pro or Max subscription cannot be connected here. Those plans sign in to the
        provider&apos;s own apps, not third-party gateways, and fanning one out across a team breaks
        their terms. Use an organization API key, cloud credentials, or an OAuth client issued to your
        organization.
      </p>
    </div>
  );
}

function ProviderCard({
  descriptor, connection, usage, currency, busyId, testResults,
  formOpen, onOpenForm, onCloseForm, onConnected, onTest, onDisconnect, onToggle,
}: {
  descriptor: ProviderDescriptor;
  connection: ProviderConnection | null;
  usage: ProviderUsageRow | null;
  currency: string;
  busyId: string | null;
  testResults: Record<string, { ok: boolean; message: string }>;
  formOpen: boolean;
  onOpenForm: () => void;
  onCloseForm: () => void;
  onConnected: (message: string) => void;
  onTest: (connection: ProviderConnection) => void;
  onDisconnect: (connection: ProviderConnection) => void;
  onToggle: (connection: ProviderConnection, enabled: boolean) => void;
}) {
  const status = STATUS[connection?.status ?? "disconnected"];
  const busy = connection !== null && busyId === connection.id;
  const test = connection ? testResults[connection.id] : undefined;
  const live = connection !== null && connection.status !== "disabled";

  return (
    <section className="rounded-lg border border-neutral-200 p-5 dark:border-neutral-800">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="flex items-center gap-2 text-sm font-medium">
            {descriptor.displayName}
            <span className={`text-xs ${status.tone}`}>· {status.label}</span>
          </h2>

          {connection ? (
            <dl className="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 text-xs text-neutral-500 sm:grid-cols-4">
              <Detail term="Authentication" value={METHOD_LABEL[connection.connectionType] ?? connection.connectionType} />
              <Detail term="Account" value={connection.hint} mono />
              <Detail term="Connected" value={new Date(connection.connectedAt).toLocaleDateString()} />
              <Detail term="Last checked" value={relativeTime(connection.lastValidatedAt)} />
              {connection.connectionType === "oauth" && connection.accessExpiresAt && (
                <Detail term="Token renews" value={relativeTime(connection.accessExpiresAt)} />
              )}
              {Object.entries(connection.config)
                .filter(([key]) => key !== "baseUrl")
                .map(([key, value]) => <Detail key={key} term={key} value={value} />)}
            </dl>
          ) : (
            <p className="mt-2 text-xs text-neutral-400">
              Not connected. {descriptor.oauthAvailable
                ? "Sign in with your organization's account, or paste a credential."
                : "Connect with a credential your organization owns."}
            </p>
          )}

          {status.action && <p className={`mt-2 text-xs ${status.tone}`}>{status.action}</p>}
          {connection?.lastFailureReason && connection.status !== "connected" && (
            <p className="mt-1 text-xs text-neutral-500">Last error: {connection.lastFailureReason}</p>
          )}
          {test && (
            <p className={`mt-2 text-xs ${test.ok ? "text-emerald-600" : "text-red-600"}`} role="status">
              {test.ok ? "✓ " : "✕ "}{test.message}
            </p>
          )}
        </div>

        <div className="flex flex-wrap gap-2">
          {connection ? (
            <>
              <button type="button" onClick={() => onTest(connection)} disabled={busy}
                className="rounded-md border border-neutral-200 px-3 py-1.5 text-xs hover:bg-neutral-50 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-900">
                {busy ? "Working…" : "Test connection"}
              </button>
              <button type="button" onClick={onOpenForm}
                className="rounded-md border border-neutral-200 px-3 py-1.5 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-900">
                Reconnect
              </button>
              <button type="button" onClick={() => onToggle(connection, !live)} disabled={busy}
                className="rounded-md border border-neutral-200 px-3 py-1.5 text-xs hover:bg-neutral-50 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-900">
                {live ? "Disable" : "Enable"}
              </button>
              <button type="button" onClick={() => onDisconnect(connection)} disabled={busy}
                className="rounded-md px-3 py-1.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50 dark:hover:bg-red-950/30">
                Disconnect
              </button>
            </>
          ) : (
            <button type="button" onClick={onOpenForm}
              className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white dark:bg-white dark:text-neutral-900">
              Connect {descriptor.displayName}
            </button>
          )}
        </div>
      </div>

      {usage && (usage.month.requests > 0 || usage.today.requests > 0) && (
        <div className="mt-4 grid grid-cols-2 gap-4 border-t border-neutral-100 pt-3 text-xs sm:grid-cols-5 dark:border-neutral-800">
          <Stat label="Requests today" value={fmt(usage.today.requests)} />
          <Stat label="Tokens today" value={fmt(usage.today.tokens)} />
          <Stat label="Cost today" value={money(usage.today.costMinor, currency)} />
          <Stat label="Cost this month" value={money(usage.month.customerCostMinor, currency)} />
          <Stat label="Failures / 429s" value={`${usage.month.failures} / ${usage.month.rateLimited}`} />
        </div>
      )}

      {formOpen && (
        <ConnectForm
          provider={descriptor}
          reconnect={connection !== null}
          onDone={onConnected}
          onCancel={onCloseForm}
        />
      )}
    </section>
  );
}

function Detail({ term, value, mono }: { term: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt className="text-neutral-400">{term}</dt>
      <dd className={mono ? "font-mono text-neutral-600 dark:text-neutral-300" : "text-neutral-600 dark:text-neutral-300"}>{value}</dd>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p className="text-neutral-400">{label}</p>
      <p className="mt-0.5 text-sm text-neutral-700 dark:text-neutral-200">{value}</p>
    </div>
  );
}

/** Plain-language text for the codes the OAuth callback redirects back with. */
function oauthErrorText(code: string, provider: string | null): string {
  const name = provider ?? "the provider";
  switch (code) {
    case "provider_denied":
      return `The login at ${name} was cancelled or declined. Nothing was connected.`;
    case "invalid_state":
      return "That login link had already been used or had expired. Start the connection again.";
    case "flow_mismatch":
      return "That login did not start in this browser, so it was refused. Start the connection again here.";
    case "oauth_rejected":
      return `${name} rejected the login. Start the connection again.`;
    case "oauth_unreachable":
      return `APUS could not reach ${name} to finish the login. Try again in a moment.`;
    default:
      return "The connection could not be completed. Start it again.";
  }
}
