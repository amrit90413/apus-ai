"use client";
import { useCallback } from "react";
import { adminApi, myAiApi, usePolling, money, fmt } from "@/lib/api";
import type { MyAiResult } from "@/lib/api";
import KeysSection from "./keys-section";

// Employee self-service: your own allowance, usage and reset countdown. Nothing
// here says anything about how the organization authenticates to a provider.
export default function UsagePage() {
  const loader = useCallback(() => adminApi.myUsage(), []);
  const aiLoader = useCallback(() => myAiApi.summary(), []);
  const data = usePolling(loader, 5000);
  const ai = usePolling(aiLoader, 10000);
  if (!data) return <div className="p-8 text-neutral-500">Loading…</div>;

  return (
    <div className="mx-auto max-w-2xl p-8">
      <h1 className="mb-6 text-lg font-medium">Your AI usage</h1>
      {ai && <AllowanceCard ai={ai} />}
      {data.balance?.enforced && (
        <div className="mb-6 rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
          <div className="flex justify-between text-sm">
            <span className="font-medium">Prepaid balance</span>
            <span className={(data.balance.remaining ?? 0) <= 0 ? "text-red-600" : "text-neutral-500"}>
              {(data.balance.remaining ?? 0).toLocaleString()} tokens left
            </span>
          </div>
          {(data.balance.remaining ?? 0) <= 0 && (
            <p className="mt-1 text-xs text-red-600">Exhausted — ask your admin to add tokens.</p>
          )}
        </div>
      )}
      <div className="space-y-4">
        {data.windows.length === 0 && <p className="text-sm text-neutral-400">No rolling windows configured.</p>}
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
      {ai && ai.models.length > 0 && (
        <section className="mt-6 rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
          <h2 className="text-sm font-medium">Models available to you</h2>
          <ul className="mt-2 space-y-1 text-sm">
            {ai.models.map(m => (
              <li key={m.id} className="flex items-baseline justify-between">
                <span className="font-mono text-xs">{m.id}</span>
                <span className="text-xs text-neutral-400">via {m.providers.join(", ")}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <KeysSection />
    </div>
  );
}

/** Monthly allowance, what is left, and when it resets. */
function AllowanceCard({ ai }: { ai: MyAiResult }) {
  const { allowance, usage, access, currency } = ai;
  const pct = allowance.unlimited || allowance.budgetMinor === 0
    ? 0
    : Math.min(100, Math.round((allowance.usedMinor / allowance.budgetMinor) * 100));

  return (
    <section className="mb-6 rounded-lg border border-neutral-100 p-4 dark:border-neutral-800">
      <div className="flex flex-wrap items-baseline justify-between gap-3">
        <div>
          <p className="text-xs text-neutral-400">Monthly allowance</p>
          <p className="text-xl">{allowance.unlimited ? "Unlimited" : money(allowance.budgetMinor, currency)}</p>
        </div>
        <div className="text-right">
          <p className="text-xs text-neutral-400">Used</p>
          <p className="text-xl">{money(allowance.usedMinor, currency)}</p>
        </div>
        <div className="text-right">
          <p className="text-xs text-neutral-400">Remaining</p>
          <p className={`text-xl ${allowance.remainingMinor === 0 ? "text-red-600" : ""}`}>
            {allowance.unlimited ? "—" : money(allowance.remainingMinor, currency)}
          </p>
        </div>
      </div>

      {!allowance.unlimited && allowance.budgetMinor > 0 && (
        <div className="mt-3 h-2 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
          <div
            className={pct >= 100 ? "h-full bg-red-500" : pct >= 90 ? "h-full bg-amber-500" : "h-full bg-emerald-500"}
            style={{ width: `${pct}%` }}
          />
        </div>
      )}

      <p className="mt-2 text-xs text-neutral-400">
        {fmt(usage.requests)} requests · {fmt(usage.tokens)} tokens ·
        resets {new Date(allowance.resetsAt).toLocaleDateString(undefined, { day: "numeric", month: "long" })}
      </p>

      {allowance.remainingMinor === 0 && !allowance.unlimited && (
        <p className="mt-2 text-xs text-red-600">
          Your allowance is used up. Ask your admin to top it up, or wait for the reset.
        </p>
      )}
      {access.status !== "active" && (
        <p className="mt-2 text-xs text-amber-600">
          Your AI access is {access.status}. Contact your administrator.
        </p>
      )}
      {!access.organizationAiEnabled && (
        <p className="mt-2 text-xs text-amber-600">AI access is turned off for your organization.</p>
      )}
    </section>
  );
}
