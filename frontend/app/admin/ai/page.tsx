"use client";
import { useCallback, useState } from "react";
import { aiAdminApi, usePolling, money, fmt } from "@/lib/api";
import type { AiOverviewResult, AiTrendResult } from "@/lib/api";
import AdminNav from "@/components/AdminNav";

/**
 * The tenant AI dashboard. Every figure comes from the usage ledger — the same
 * append-only rows billing reconciles against — so what an admin sees here and what
 * they are invoiced for cannot drift apart.
 */
export default function AiOverviewPage() {
  const [days, setDays] = useState(30);
  const overviewLoader = useCallback(() => aiAdminApi.overview(), []);
  const trendLoader = useCallback(() => aiAdminApi.trend(days), [days]);

  const overview = usePolling(overviewLoader, 15000);
  const trend = usePolling(trendLoader, 60000);

  if (!overview) return <div className="p-8 text-sm text-neutral-400">Loading AI overview…</div>;

  const { currency, budget, totals, providers, models, users } = overview as AiOverviewResult;
  const usedPct = budget.unlimited || budget.budgetMinor === 0
    ? 0
    : Math.min(100, Math.round((budget.consumedMinor / budget.budgetMinor) * 100));

  return (
    <div className="mx-auto max-w-6xl p-8">
      <AdminNav />
      <header className="mb-6">
        <h1 className="text-lg font-medium">AI overview</h1>
        <p className="text-xs text-neutral-400">This calendar month, across every provider</p>
      </header>

      <section className="mb-6 rounded-lg border border-neutral-200 p-5 dark:border-neutral-800">
        <div className="flex flex-wrap items-baseline justify-between gap-4">
          <div>
            <p className="text-xs text-neutral-400">Monthly budget</p>
            <p className="text-2xl">
              {budget.unlimited ? "Unlimited" : money(budget.budgetMinor, currency)}
            </p>
          </div>
          <div className="text-right">
            <p className="text-xs text-neutral-400">Used</p>
            <p className="text-2xl">{money(budget.consumedMinor, currency)}</p>
          </div>
          <div className="text-right">
            <p className="text-xs text-neutral-400">Remaining</p>
            <p className={`text-2xl ${budget.remainingMinor === 0 ? "text-red-600" : ""}`}>
              {budget.unlimited ? "—" : money(budget.remainingMinor, currency)}
            </p>
          </div>
        </div>

        {!budget.unlimited && budget.budgetMinor > 0 && (
          <div className="mt-4 h-2 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
            <div
              className={`h-full ${usedPct >= 100 ? "bg-red-500" : usedPct >= 90 ? "bg-amber-500" : "bg-emerald-500"}`}
              style={{ width: `${usedPct}%` }}
            />
          </div>
        )}
        {budget.reservedMinor > 0 && (
          <p className="mt-2 text-xs text-neutral-400">
            {money(budget.reservedMinor, currency)} held for requests in flight.
          </p>
        )}
      </section>

      <section className="mb-6 grid grid-cols-2 gap-4 sm:grid-cols-6">
        <Metric label="Requests" value={fmt(totals.requests)} />
        <Metric label="Tokens" value={fmt(totals.tokens)} />
        <Metric label="Customer cost" value={money(totals.customerCostMinor, currency)} />
        <Metric label="Provider cost" value={money(totals.providerCostMinor, currency)} />
        <Metric label="Margin" value={money(totals.marginMinor, currency)}
          tone={totals.marginMinor < 0 ? "text-red-600" : undefined} />
        <Metric label="Avg latency" value={`${fmt(totals.avgLatencyMs)} ms`} />
      </section>

      {(totals.failures > 0 || totals.blocked > 0) && (
        <p className="mb-6 rounded-lg bg-amber-50 px-4 py-2 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-300">
          {totals.failures} failed and {totals.blocked} blocked request(s) this month. Blocked means a
          policy refused it — an exhausted allowance, a model that is not enabled, or a rate limit.
        </p>
      )}

      <div className="grid gap-6 lg:grid-cols-2">
        <Breakdown
          title="By provider"
          currency={currency}
          rows={providers.map(p => ({ key: p.provider, label: p.provider, costMinor: p.customerCostMinor, requests: p.requests, tokens: p.tokens }))}
        />
        <Breakdown
          title="By member"
          currency={currency}
          rows={users.map(u => ({ key: u.userId, label: u.email, costMinor: u.customerCostMinor, requests: u.requests, tokens: u.tokens }))}
        />
        <Breakdown
          title="By model"
          currency={currency}
          rows={models.map(m => ({ key: `${m.provider}/${m.model}`, label: `${m.model}`, sublabel: m.provider, costMinor: m.customerCostMinor, requests: m.requests, tokens: m.tokens }))}
        />
        <Trend trend={trend} days={days} onDays={setDays} />
      </div>
    </div>
  );
}

function Metric({ label, value, tone }: { label: string; value: string; tone?: string }) {
  return (
    <div className="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
      <p className="text-xs text-neutral-400">{label}</p>
      <p className={`mt-1 text-sm ${tone ?? ""}`}>{value}</p>
    </div>
  );
}

interface BreakdownRow { key: string; label: string; sublabel?: string; costMinor: number; requests: number; tokens: number }

function Breakdown({ title, rows, currency }: { title: string; rows: BreakdownRow[]; currency: string }) {
  const max = Math.max(1, ...rows.map(r => r.costMinor));
  return (
    <section className="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
      <h2 className="mb-3 text-sm font-medium">{title}</h2>
      {rows.length === 0 ? (
        <p className="text-xs text-neutral-400">No usage yet this month.</p>
      ) : (
        <ul className="space-y-2">
          {rows.slice(0, 12).map(row => (
            <li key={row.key}>
              <div className="flex items-baseline justify-between text-sm">
                <span className="truncate">
                  {row.label}
                  {row.sublabel && <span className="ml-1 text-xs text-neutral-400">{row.sublabel}</span>}
                </span>
                <span className="ml-3 shrink-0 tabular-nums">{money(row.costMinor, currency)}</span>
              </div>
              <div className="mt-1 h-1 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
                <div className="h-full bg-neutral-400 dark:bg-neutral-600"
                  style={{ width: `${Math.round((row.costMinor / max) * 100)}%` }} />
              </div>
              <p className="mt-0.5 text-xs text-neutral-400">
                {fmt(row.requests)} requests · {fmt(row.tokens)} tokens
              </p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function Trend({ trend, days, onDays }: { trend: AiTrendResult | null; days: number; onDays: (d: number) => void }) {
  const daily = trend?.daily ?? [];
  const max = Math.max(1, ...daily.map(d => d.customerCostMinor));

  return (
    <section className="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-sm font-medium">Daily spend</h2>
        <div className="flex gap-1 text-xs">
          {[7, 30, 90].map(d => (
            <button
              key={d}
              type="button"
              onClick={() => onDays(d)}
              aria-pressed={days === d}
              className={days === d
                ? "rounded bg-neutral-900 px-2 py-0.5 text-white dark:bg-white dark:text-neutral-900"
                : "rounded border border-neutral-200 px-2 py-0.5 dark:border-neutral-700"}
            >
              {d}d
            </button>
          ))}
        </div>
      </div>

      {daily.length === 0 ? (
        <p className="text-xs text-neutral-400">No usage in this window.</p>
      ) : (
        <>
          <div className="flex h-24 items-end gap-0.5">
            {daily.map(d => (
              <div
                key={d.date}
                title={`${d.date}: ${money(d.customerCostMinor, trend!.currency)} · ${fmt(d.requests)} requests`}
                className="flex-1 rounded-t bg-neutral-300 dark:bg-neutral-700"
                style={{ height: `${Math.max(2, Math.round((d.customerCostMinor / max) * 100))}%` }}
              />
            ))}
          </div>
          <p className="mt-2 text-xs text-neutral-400">
            {daily[0].date} – {daily[daily.length - 1].date}
          </p>
        </>
      )}
    </section>
  );
}
