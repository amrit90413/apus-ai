"use client";
import { useCallback } from "react";
import { adminApi, usePolling, fmt } from "@/lib/api";

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
    </div>
  );
}
