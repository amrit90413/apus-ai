"use client";
import { useCallback, useState } from "react";
import { adminApi, usePolling, fmt, type ProviderKeyRow } from "@/lib/api";

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg bg-neutral-50 dark:bg-neutral-900 p-4">
      <p className="text-sm text-neutral-500">{label}</p>
      <p className="mt-1 text-2xl font-medium">{value}</p>
    </div>
  );
}

const statusStyles: Record<string, string> = {
  active: "text-emerald-600",
  near: "text-amber-600",
  throttled: "text-red-600",
};

function ProviderKeysBadge({ provider }: { provider: string }) {
  const colors: Record<string, string> = {
    anthropic: "bg-violet-50 text-violet-700",
    openai: "bg-green-50 text-green-700",
  };
  return (
    <span className={`rounded px-2 py-0.5 text-xs font-medium ${colors[provider] ?? "bg-neutral-100 text-neutral-600"}`}>
      {provider}
    </span>
  );
}

function ProviderKeysSection() {
  const [keys, setKeys] = useState<ProviderKeyRow[] | null>(null);
  const [provider, setProvider] = useState("anthropic");
  const [apiKey, setApiKey] = useState("");
  const [adding, setAdding] = useState(false);
  const [showForm, setShowForm] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loader = useCallback(() => adminApi.listProviderKeys(), []);
  const polled = usePolling(loader, 10000);
  const displayed = keys ?? polled;

  const handleAdd = async () => {
    if (!apiKey.trim()) return;
    setAdding(true);
    setError(null);
    try {
      await adminApi.addProviderKey(provider, apiKey.trim());
      setApiKey("");
      setShowForm(false);
      // Force refresh by clearing local override so polling picks up the new key.
      setKeys(null);
    } catch {
      setError("Failed to save key. Check the key length and try again.");
    } finally {
      setAdding(false);
    }
  };

  const handleRemove = async (id: string, p: string) => {
    try {
      await adminApi.removeProviderKey(id);
      setKeys((prev) =>
        (prev ?? polled ?? []).map((k) => (k.id === id ? { ...k, isActive: false } : k))
      );
    } catch {
      setError(`Failed to remove key for ${p}.`);
    }
  };

  return (
    <section className="mt-8">
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-medium text-neutral-500">API keys</h2>
        <button
          onClick={() => { setShowForm((v) => !v); setError(null); }}
          className="rounded-md border border-neutral-200 px-3 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700"
        >
          {showForm ? "Cancel" : "+ Add key"}
        </button>
      </div>

      {showForm && (
        <div className="mb-4 rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
          <div className="flex gap-2">
            <select
              value={provider}
              onChange={(e) => setProvider(e.target.value)}
              className="rounded border border-neutral-200 px-2 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
            >
              <option value="anthropic">Anthropic</option>
              <option value="openai">OpenAI</option>
            </select>
            <input
              type="password"
              placeholder="sk-ant-..."
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              className="flex-1 rounded border border-neutral-200 px-3 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
            />
            <button
              onClick={handleAdd}
              disabled={adding || !apiKey.trim()}
              className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900"
            >
              {adding ? "Saving…" : "Save"}
            </button>
          </div>
          {error && <p className="mt-2 text-xs text-red-600">{error}</p>}
        </div>
      )}

      {!displayed ? (
        <p className="text-sm text-neutral-400">Loading keys…</p>
      ) : displayed.length === 0 ? (
        <p className="rounded-lg border border-dashed border-neutral-200 p-4 text-sm text-neutral-400 dark:border-neutral-700">
          No API keys saved yet. Add one above to enable AI requests.
        </p>
      ) : (
        <table className="w-full text-sm">
          <thead className="text-left text-neutral-400">
            <tr>
              <th className="py-2 font-normal">Provider</th>
              <th className="py-2 font-normal">Key</th>
              <th className="py-2 font-normal">Status</th>
              <th className="py-2 font-normal">Added</th>
              <th className="py-2 font-normal" />
            </tr>
          </thead>
          <tbody>
            {displayed.map((k) => (
              <tr key={k.id} className="border-t border-neutral-100 dark:border-neutral-800">
                <td className="py-2.5"><ProviderKeysBadge provider={k.provider} /></td>
                <td className="py-2.5 font-mono text-neutral-500">{k.keyHint}</td>
                <td className={`py-2.5 ${k.isActive ? "text-emerald-600" : "text-neutral-400"}`}>
                  {k.isActive ? "active" : "removed"}
                </td>
                <td className="py-2.5 text-neutral-400">
                  {new Date(k.createdAt).toLocaleDateString()}
                </td>
                <td className="py-2.5 text-right">
                  {k.isActive && (
                    <button
                      onClick={() => handleRemove(k.id, k.provider)}
                      className="text-xs text-red-500 hover:underline"
                    >
                      Remove
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export default function SuperAdminPage() {
  const orgsLoader = useCallback(() => adminApi.organizations(), []);
  const anomLoader = useCallback(() => adminApi.anomalies(), []);
  const data = usePolling(orgsLoader, 5000);
  const anomalies = usePolling(anomLoader, 8000);

  if (!data) return <div className="p-8 text-neutral-500">Loading control plane…</div>;

  return (
    <div className="mx-auto max-w-5xl p-8">
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Platform control plane</h1>
          <p className="text-xs text-neutral-400">Super admin · all tenants</p>
        </div>
        <span className="rounded-md bg-emerald-50 px-2.5 py-1 text-xs text-emerald-700">gateway healthy</span>
      </header>

      <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Stat label="Organizations" value={String(data.totals.orgs)} />
        <Stat label="Active employees" value={String(data.totals.employees)} />
        <Stat label="Tokens today" value={fmt(data.totals.tokensToday)} />
        <Stat label="Est. cost today" value={`$${data.totals.costToday.toFixed(0)}`} />
      </div>

      {anomalies && anomalies.anomalies.length > 0 && (
        <div className="mb-6 rounded-md bg-red-50 px-4 py-2.5 text-sm text-red-700 dark:bg-red-950/40">
          {anomalies.anomalies.length} anomalies — {anomalies.anomalies[0].userEmail}: {anomalies.anomalies[0].detail}
        </div>
      )}

      <h2 className="mb-2 text-sm font-medium text-neutral-500">Organizations</h2>
      <table className="w-full text-sm">
        <thead className="text-left text-neutral-400">
          <tr>
            <th className="py-2 font-normal">Org</th>
            <th className="py-2 font-normal">Seats</th>
            <th className="py-2 font-normal">Monthly quota</th>
            <th className="py-2 font-normal">Status</th>
          </tr>
        </thead>
        <tbody>
          {data.orgs.map((o) => {
            const pct = Math.round((o.used / o.limit) * 100);
            return (
              <tr key={o.id} className="border-t border-neutral-100 dark:border-neutral-800">
                <td className="py-2.5 font-medium">{o.name}</td>
                <td className="py-2.5">{o.seats}</td>
                <td className="py-2.5">
                  <div className="h-1.5 w-40 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
                    <div className={pct > 95 ? "h-full bg-red-500" : pct > 80 ? "h-full bg-amber-500" : "h-full bg-blue-500"} style={{ width: `${Math.min(100, pct)}%` }} />
                  </div>
                  <span className="text-xs text-neutral-400">{fmt(o.used)} / {fmt(o.limit)}</span>
                </td>
                <td className={`py-2.5 ${statusStyles[o.status]}`}>{o.status}</td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <ProviderKeysSection />
    </div>
  );
}
