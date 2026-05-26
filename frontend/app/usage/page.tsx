"use client";
import { useCallback } from "react";
import { adminApi, usePolling } from "@/lib/api";

// Employee self-service: see your own usage + reset countdown.
export default function UsagePage() {
  const loader = useCallback(() => adminApi.myUsage(), []);
  const data = usePolling(loader, 5000);
  if (!data) return <div className="p-8 text-neutral-500">Loading…</div>;

  return (
    <div className="mx-auto max-w-2xl p-8">
      <h1 className="mb-6 text-lg font-medium">Your usage</h1>
      <div className="space-y-4">
        {data.windows.map((w) => {
          const pct = Math.round((w.used / w.limit) * 100);
          const mins = Math.ceil(w.resetInSeconds / 60);
          return (
            <div key={w.name} className="rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
              <div className="mb-2 flex justify-between text-sm">
                <span className="font-medium">{w.name}</span>
                <span className="text-neutral-400">resets in {mins}m</span>
              </div>
              <div className="h-2 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
                <div className={pct > 90 ? "h-full bg-red-500" : pct > 70 ? "h-full bg-amber-500" : "h-full bg-emerald-500"} style={{ width: `${Math.min(100, pct)}%` }} />
              </div>
              <p className="mt-1 text-xs text-neutral-400">{w.used.toLocaleString()} / {w.limit.toLocaleString()} tokens</p>
            </div>
          );
        })}
      </div>
    </div>
  );
}
