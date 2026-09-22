"use client";
import { useCallback, useState } from "react";
import { adminApi, usePolling, errorMessage } from "@/lib/api";
import type { ProviderCredentialRow, CredentialTestResult } from "@/lib/api";
import AdminNav from "@/components/AdminNav";

function ProviderBadge({ provider }: { provider: string }) {
  const colors: Record<string, string> = {
    anthropic: "bg-violet-50 text-violet-700 dark:bg-violet-950/40 dark:text-violet-300",
    openai: "bg-green-50 text-green-700 dark:bg-green-950/40 dark:text-green-300",
  };
  return (
    <span className={`rounded px-2 py-0.5 text-xs font-medium ${colors[provider] ?? "bg-neutral-100 text-neutral-600 dark:bg-neutral-800 dark:text-neutral-300"}`}>
      {provider}
    </span>
  );
}

function KindBadge({ kind }: { kind: ProviderCredentialRow["kind"] }) {
  return (
    <span className="rounded bg-neutral-100 px-2 py-0.5 text-xs font-medium text-neutral-600 dark:bg-neutral-800 dark:text-neutral-300">
      {kind === "oauth" ? "OAuth" : "API key"}
    </span>
  );
}

/** "in 12m" / "in 3h" / "in 2d" / "expired" for an ISO timestamp. */
function relative(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now();
  if (Number.isNaN(ms)) return "unknown";
  if (ms <= 0) return "expired";
  const mins = Math.round(ms / 60_000);
  if (mins < 60) return `in ${Math.max(1, mins)}m`;
  const hours = Math.round(mins / 60);
  if (hours < 48) return `in ${hours}h`;
  return `in ${Math.round(hours / 24)}d`;
}

/**
 * First-run connection chooser. Mirrors the shape of a login method picker: the
 * admin connects one credential and every user in the org routes through it.
 *
 * A personal Claude.ai subscription is deliberately absent. Consumer Pro/Max
 * plans authenticate Anthropic's own surfaces, not third-party gateways, and
 * fanning one out across a team is subscription sharing — see docs/ADMIN_GUIDE.md.
 */
function ConnectChooser({
  oauthEnabled,
  connecting,
  onAddKey,
  onConnect,
}: {
  oauthEnabled: boolean;
  connecting: boolean;
  onAddKey: () => void;
  onConnect: () => void;
}) {
  return (
    <div className="rounded-lg border border-neutral-200 p-6 dark:border-neutral-800">
      <h2 className="text-sm font-medium">Connect a provider account</h2>
      <p className="mt-1 mb-5 text-xs text-neutral-400">
        Your users do not connect anything themselves — they authenticate to this gateway and
        their usage is metered against the credential you connect here.
      </p>

      <div className="space-y-3">
        <button
          type="button"
          onClick={onAddKey}
          className="w-full rounded-md bg-neutral-900 px-4 py-2.5 text-sm font-medium text-white dark:bg-white dark:text-neutral-900"
        >
          Anthropic Console API key
        </button>
        <p className="-mt-1.5 text-xs text-neutral-400">
          Recommended. Use a service-account key owned by your organization, billed through Console.
        </p>

        <button
          type="button"
          onClick={onConnect}
          disabled={!oauthEnabled || connecting}
          title={oauthEnabled ? undefined : "No OAuth client configured for this deployment"}
          className="w-full rounded-md border border-neutral-200 px-4 py-2.5 text-sm font-medium hover:bg-neutral-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-900"
        >
          {connecting ? "Redirecting…" : "Connect with browser login"}
        </button>
        <p className="-mt-1.5 text-xs text-neutral-400">
          {oauthEnabled
            ? "Sign in to the provider account that belongs to your organization."
            : "Unavailable — needs an OAuth client issued to your organization. Set Anthropic:OAuth ClientId, AuthorizeUrl, TokenUrl and RedirectUri, then restart the gateway."}
        </p>
      </div>

      <p className="mt-5 border-t border-neutral-100 pt-4 text-xs text-neutral-400 dark:border-neutral-800">
        A personal Claude Pro or Max subscription cannot be connected here. Those plans sign in to
        Anthropic&apos;s own apps, not third-party gateways, and sharing one across a team breaks
        Anthropic&apos;s terms. Use a Console key, or an OAuth client issued to your organization.
      </p>
    </div>
  );
}

export default function AdminProvidersPage() {
  const [refreshKey, setRefreshKey] = useState(0);
  const loader = useCallback(() => adminApi.listProviderCredentials(), [refreshKey]);
  const data = usePolling(loader, 10000);
  const refresh = () => setRefreshKey(k => k + 1);

  // Add-key form.
  const [showForm, setShowForm] = useState(false);
  const [provider, setProvider] = useState("anthropic");
  const [apiKey, setApiKey] = useState("");
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  // OAuth start.
  const [connecting, setConnecting] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);

  // Per-row action state.
  const [testing, setTesting] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, CredentialTestResult>>({});
  const [removing, setRemoving] = useState<string | null>(null);

  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!apiKey.trim()) return;
    setSaving(true);
    setFormError(null);
    try {
      await adminApi.addProviderApiKey(provider, apiKey.trim());
      setApiKey("");
      setShowForm(false);
      refresh();
    } catch (err) {
      setFormError(errorMessage(err, "Failed to save the key. Check it and try again."));
    } finally {
      setSaving(false);
    }
  };

  const handleConnect = async () => {
    setConnecting(true);
    setPageError(null);
    try {
      const { authorizeUrl } = await adminApi.startProviderOAuth();
      window.location.assign(authorizeUrl);
    } catch (err) {
      setPageError(errorMessage(err, "Could not start the browser login."));
      setConnecting(false);
    }
  };

  const handleTest = async (id: string) => {
    setTesting(id);
    setPageError(null);
    try {
      const result = await adminApi.testProviderCredential(id);
      setTestResults(prev => ({ ...prev, [id]: result }));
    } catch (err) {
      setTestResults(prev => ({ ...prev, [id]: { ok: false, message: errorMessage(err, "Test failed.") } }));
    } finally {
      setTesting(null);
    }
  };

  const handleRemove = async (row: ProviderCredentialRow) => {
    if (!confirm(`Remove the ${row.provider} ${row.kind === "oauth" ? "OAuth connection" : "API key"} (${row.hint})? Users will fall back to the platform key, if any.`)) return;
    setRemoving(row.id);
    setPageError(null);
    try {
      await adminApi.removeProviderCredential(row.id);
      refresh();
    } catch (err) {
      setPageError(errorMessage(err, "Failed to remove the credential."));
    } finally {
      setRemoving(null);
    }
  };

  const credentials = data?.credentials ?? null;
  const oauthEnabled = data?.oauth.enabled ?? false;

  return (
    <div className="mx-auto max-w-5xl p-8">
      <AdminNav />
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Provider credentials</h1>
          <p className="text-xs text-neutral-400">Org admin · the Claude connection your users go through</p>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={handleConnect}
            disabled={!oauthEnabled || connecting}
            title={oauthEnabled
              ? "Sign in to your organization's provider account"
              : "No OAuth client configured — set Anthropic:OAuth in the gateway config"}
            className="rounded-md border border-neutral-200 px-3 py-1.5 text-sm hover:bg-neutral-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-900"
          >
            {connecting ? "Redirecting…" : "Connect with browser login"}
          </button>
          <button
            type="button"
            onClick={() => { setShowForm(v => !v); setFormError(null); }}
            className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white dark:bg-white dark:text-neutral-900"
          >
            {showForm ? "Cancel" : "+ Add API key"}
          </button>
        </div>
      </header>

      <div className="mb-6 rounded-lg bg-blue-50 px-4 py-3 text-sm text-blue-800 dark:bg-blue-950/40 dark:text-blue-300">
        Use a Console service-account API key for your organization. Never share a personal Claude subscription across users.
      </div>

      {showForm && (
        <form onSubmit={handleAdd} className="mb-6 rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
          <div className="flex flex-col gap-2 sm:flex-row">
            <div>
              <label htmlFor="cred-provider" className="sr-only">Provider</label>
              <select
                id="cred-provider"
                value={provider}
                onChange={e => setProvider(e.target.value)}
                className="rounded border border-neutral-200 px-2 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
              >
                <option value="anthropic">Anthropic</option>
                <option value="openai">OpenAI</option>
              </select>
            </div>
            <div className="flex-1">
              <label htmlFor="cred-key" className="sr-only">API key</label>
              <input
                id="cred-key"
                type="password"
                autoComplete="off"
                placeholder={provider === "anthropic" ? "sk-ant-…" : "sk-…"}
                value={apiKey}
                onChange={e => setApiKey(e.target.value)}
                className="w-full rounded border border-neutral-200 px-3 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
              />
            </div>
            <button
              type="submit"
              disabled={saving || !apiKey.trim()}
              className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900"
            >
              {saving ? "Saving…" : "Save"}
            </button>
          </div>
          {formError && <p className="mt-2 text-xs text-red-600" role="alert">{formError}</p>}
        </form>
      )}

      {pageError && <p className="mb-4 text-sm text-red-600" role="alert">{pageError}</p>}

      {!credentials ? (
        <p className="text-sm text-neutral-400">Loading credentials…</p>
      ) : credentials.length === 0 ? (
        <>
          <p className="mb-4 rounded-lg border border-dashed border-neutral-200 p-4 text-sm text-neutral-400 dark:border-neutral-700">
            No credential connected — users will get provider_not_configured until you add one (a platform-wide fallback key may apply).
          </p>
          {!showForm && (
            <ConnectChooser
              oauthEnabled={oauthEnabled}
              connecting={connecting}
              onAddKey={() => { setShowForm(true); setFormError(null); }}
              onConnect={handleConnect}
            />
          )}
        </>
      ) : (
        <table className="w-full text-sm">
          <thead className="text-left text-neutral-400">
            <tr>
              <th className="py-2 font-normal">Provider</th>
              <th className="py-2 font-normal">Kind</th>
              <th className="py-2 font-normal">Credential</th>
              <th className="py-2 font-normal">Status</th>
              <th className="py-2 font-normal">Added</th>
              <th className="py-2 font-normal" />
            </tr>
          </thead>
          <tbody>
            {credentials.map(c => {
              const test = testResults[c.id];
              return (
                <tr key={c.id} className="border-t border-neutral-100 align-top dark:border-neutral-800">
                  <td className="py-2.5"><ProviderBadge provider={c.provider} /></td>
                  <td className="py-2.5"><KindBadge kind={c.kind} /></td>
                  <td className="py-2.5">
                    <p className="font-mono text-neutral-500">{c.hint}</p>
                    {c.kind === "oauth" && c.accessExpiresAt && (
                      <p className="text-xs text-neutral-400" title={new Date(c.accessExpiresAt).toLocaleString()}>
                        refreshes {relative(c.accessExpiresAt)}
                      </p>
                    )}
                    {c.lastError && <p className="text-xs text-red-600">{c.lastError}</p>}
                    {test && (
                      <p className={`text-xs ${test.ok ? "text-emerald-600" : "text-red-600"}`} role="status">
                        {test.ok ? "OK" : "Failed"}{test.message ? ` — ${test.message}` : ""}
                      </p>
                    )}
                  </td>
                  <td className={`py-2.5 ${c.isActive ? "text-emerald-600" : "text-neutral-400"}`}>
                    {c.isActive ? "active" : "removed"}
                  </td>
                  <td className="py-2.5 text-neutral-400">{new Date(c.createdAt).toLocaleDateString()}</td>
                  <td className="py-2.5 text-right">
                    {c.isActive && (
                      <>
                        <button
                          type="button"
                          onClick={() => handleTest(c.id)}
                          disabled={testing === c.id}
                          className="mr-2 text-xs text-blue-500 hover:underline disabled:opacity-50"
                        >
                          {testing === c.id ? "Testing…" : "Test"}
                        </button>
                        <button
                          type="button"
                          onClick={() => handleRemove(c)}
                          disabled={removing === c.id}
                          className="text-xs text-red-500 hover:underline disabled:opacity-50"
                        >
                          {removing === c.id ? "Removing…" : "Remove"}
                        </button>
                      </>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
