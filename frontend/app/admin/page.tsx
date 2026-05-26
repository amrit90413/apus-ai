"use client";
import { useCallback } from "react";
import { adminApi, usePolling, fmt } from "@/lib/api";

// Org/workspace admin: the per-employee tracking view (your core need).
export default function AdminPage() {
  const workspaceId = "demo-workspace"; // from route param / session in production
  const loader = useCallback(() => adminApi.topConsumers(workspaceId), [workspaceId]);
  const data = usePolling(loader, 5000);
  if (!data) return <div className="p-8 text-neutral-500">Loading workspace…</div>;

  const mins = Math.ceil(data.window.resetInSeconds / 60);
  return (
    <div className="mx-auto max-w-4xl p-8">
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Engineering workspace</h1>
          <p className="text-xs text-neutral-400">Org admin · per-employee tracking</p>
        </div>
        <button className="rounded-md border border-neutral-200 px-3 py-1.5 text-sm hover:bg-neutral-50 dark:border-neutral-700">
          Manage quota
        </button>
      </header>

      <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900"><p className="text-sm text-neutral-500">Members</p><p className="mt-1 text-2xl font-medium">{data.consumers.length}</p></div>
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900"><p className="text-sm text-neutral-500">Window usage</p><p className="mt-1 text-2xl font-medium">{Math.round((data.window.used / data.window.limit) * 100)}%</p></div>
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900"><p className="text-sm text-neutral-500">Quota window</p><p className="mt-1 text-2xl font-medium">{Math.round(data.window.resetInSeconds / 3600)}h</p></div>
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900"><p className="text-sm text-neutral-500">Resets in</p><p className="mt-1 text-2xl font-medium">{mins}m</p></div>
      </div>

      <h2 className="mb-2 text-sm font-medium text-neutral-500">Top consumers this window</h2>
      <table className="w-full text-sm">
        <thead className="text-left text-neutral-400"><tr>
          <th className="py-2 font-normal">Employee</th>
          <th className="py-2 font-normal">In / out</th>
          <th className="py-2 font-normal">Personal quota</th>
          <th className="py-2 text-right font-normal">Cost</th>
        </tr></thead>
        <tbody>
          {data.consumers.map((c) => (
            <tr key={c.userId} className="border-t border-neutral-100 dark:border-neutral-800">
              <td className="py-2.5 font-medium">{c.email}</td>
              <td className="py-2.5 text-neutral-500">{fmt(c.inputTokens)} / {fmt(c.outputTokens)}</td>
              <td className="py-2.5">
                <div className="h-1.5 w-40 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
                  <div className={c.pctOfQuota > 95 ? "h-full bg-red-500" : c.pctOfQuota > 70 ? "h-full bg-amber-500" : "h-full bg-emerald-500"} style={{ width: `${Math.min(100, c.pctOfQuota)}%` }} />
                </div>
              </td>
              <td className="py-2.5 text-right">${c.costUsd.toFixed(2)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
