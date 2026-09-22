"use client";
import { useCallback, useState } from "react";
import { aiAdminApi, usePolling, errorMessage, money, toMinor, fromMinor, fmt } from "@/lib/api";
import type { TeamMemberRow, TeamResult } from "@/lib/api";
import AdminNav from "@/components/AdminNav";

/**
 * Team AI allowances.
 *
 * Amounts are edited in whole currency ("1000") and sent as minor units, because
 * money never travels as a float. Every change here takes effect on the member's
 * next request — including raising a limit for someone who is currently blocked.
 */
export default function TeamAllowancesPage() {
  const [refreshKey, setRefreshKey] = useState(0);
  const [editing, setEditing] = useState<string | null>(null);
  const [banner, setBanner] = useState<{ tone: "ok" | "error"; text: string } | null>(null);

  const loader = useCallback(() => aiAdminApi.team(), [refreshKey]);
  const data = usePolling(loader, 15000);
  const refresh = () => setRefreshKey(k => k + 1);

  const run = async (work: () => Promise<unknown>, success: string) => {
    setBanner(null);
    try {
      await work();
      setBanner({ tone: "ok", text: success });
      setEditing(null);
      refresh();
    } catch (err) {
      setBanner({ tone: "error", text: errorMessage(err, "That change did not save.") });
    }
  };

  if (!data) return <div className="p-8 text-sm text-neutral-400">Loading team…</div>;

  const { currency, organization, users, period } = data as TeamResult;
  const allocated = organization.allocatedToMembersMinor;
  const pool = organization.unlimited ? null : (organization.monthlyBudgetMinor ?? 0) - allocated;

  return (
    <div className="mx-auto max-w-6xl p-8">
      <AdminNav />
      <header className="mb-6">
        <h1 className="text-lg font-medium">Team AI allowances</h1>
        <p className="text-xs text-neutral-400">
          Period {new Date(period.start).toLocaleDateString()} – {new Date(period.end).toLocaleDateString()} ·
          resets automatically
        </p>
      </header>

      <div className="mb-6 grid grid-cols-2 gap-4 rounded-lg border border-neutral-200 p-4 text-sm sm:grid-cols-4 dark:border-neutral-800">
        <Summary label="Organization budget" value={organization.unlimited ? "Unlimited" : money(organization.monthlyBudgetMinor, currency)} />
        <Summary label="Allocated to members" value={money(allocated, currency)} />
        <Summary
          label="Remaining shared pool"
          value={pool === null ? "—" : money(pool, currency)}
          tone={pool !== null && pool < 0 ? "text-amber-600" : undefined}
          hint={pool !== null && pool < 0 ? "Allocations exceed the budget; the tenant cap still applies." : undefined}
        />
        <Summary label="Members" value={String(users.length)} />
      </div>

      {banner && (
        <p role="status" className={`mb-4 rounded-lg px-4 py-2 text-sm ${banner.tone === "ok"
          ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-300"
          : "bg-red-50 text-red-800 dark:bg-red-950/40 dark:text-red-300"}`}>{banner.text}</p>
      )}

      <table className="w-full text-sm">
        <thead className="text-left text-neutral-400">
          <tr>
            <th className="py-2 font-normal">Member</th>
            <th className="py-2 font-normal">Monthly</th>
            <th className="py-2 font-normal">Used</th>
            <th className="py-2 font-normal">Remaining</th>
            <th className="py-2 font-normal">Requests</th>
            <th className="py-2 font-normal">Status</th>
            <th className="py-2 font-normal" />
          </tr>
        </thead>
        <tbody>
          {users.map(user => (
            <MemberRow
              key={user.membershipId}
              user={user}
              currency={currency}
              editing={editing === user.membershipId}
              onEdit={() => { setEditing(user.membershipId); setBanner(null); }}
              onCancel={() => setEditing(null)}
              onSave={(body) => run(
                () => aiAdminApi.setAllowance(user.userId, body, user.workspaceId),
                `${user.email}'s allowance updated.`)}
              onTopUp={(amountMinor) => run(
                () => aiAdminApi.topUp(user.userId, amountMinor, "admin top-up", user.workspaceId),
                `Added ${money(amountMinor, currency)} to ${user.email} for this period.`)}
              onReset={() => run(
                () => aiAdminApi.resetAllowance(user.userId, user.workspaceId),
                `${user.email}'s usage reset for this period.`)}
              onSuspend={(suspend) => run(
                () => suspend
                  ? aiAdminApi.suspend(user.userId, user.workspaceId)
                  : aiAdminApi.reactivate(user.userId, user.workspaceId),
                `${user.email} ${suspend ? "suspended" : "reactivated"}.`)}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function MemberRow({
  user, currency, editing, onEdit, onCancel, onSave, onTopUp, onReset, onSuspend,
}: {
  user: TeamMemberRow;
  currency: string;
  editing: boolean;
  onEdit: () => void;
  onCancel: () => void;
  onSave: (body: { monthlyMinor: number | null; unlimited?: boolean }) => void;
  onTopUp: (amountMinor: number) => void;
  onReset: () => void;
  onSuspend: (suspend: boolean) => void;
}) {
  const [amount, setAmount] = useState(fromMinor(user.monthlyAllowanceMinor, currency));
  const [unlimited, setUnlimited] = useState(user.unlimited);
  const [topUp, setTopUp] = useState("");
  const [fieldError, setFieldError] = useState<string | null>(null);

  const usedPct = user.unlimited || user.budgetMinor === 0
    ? 0
    : Math.min(100, Math.round((user.consumedMinor / user.budgetMinor) * 100));

  const save = () => {
    if (unlimited) return onSave({ monthlyMinor: null, unlimited: true });
    const minor = toMinor(amount, currency);
    if (minor === null || minor < 0) { setFieldError("Enter an amount like 1000 or 1000.50."); return; }
    setFieldError(null);
    onSave({ monthlyMinor: minor, unlimited: false });
  };

  const applyTopUp = () => {
    const minor = toMinor(topUp, currency);
    if (minor === null || minor <= 0) { setFieldError("Enter an amount to add."); return; }
    setFieldError(null);
    setTopUp("");
    onTopUp(minor);
  };

  return (
    <tr className="border-t border-neutral-100 align-top dark:border-neutral-800">
      <td className="py-3">
        <p>{user.email}</p>
        <p className="text-xs text-neutral-400">{user.role}</p>
      </td>

      <td className="py-3">
        {editing ? (
          <div className="space-y-2">
            <label className="flex items-center gap-2 text-xs">
              <input type="checkbox" checked={unlimited} onChange={e => setUnlimited(e.target.checked)} />
              Unlimited
            </label>
            {!unlimited && (
              <input
                aria-label={`Monthly allowance for ${user.email}`}
                value={amount}
                onChange={e => setAmount(e.target.value)}
                inputMode="decimal"
                placeholder="1000"
                className="w-28 rounded border border-neutral-200 px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-900"
              />
            )}
            {fieldError && <p className="text-xs text-red-600">{fieldError}</p>}
          </div>
        ) : user.unlimited ? (
          <span className="text-neutral-400">Unlimited</span>
        ) : (
          money(user.budgetMinor, currency)
        )}
      </td>

      <td className="py-3">
        <p>{money(user.consumedMinor, currency)}</p>
        {!user.unlimited && user.budgetMinor > 0 && (
          <div className="mt-1 h-1 w-24 overflow-hidden rounded bg-neutral-100 dark:bg-neutral-800">
            <div
              className={`h-full ${usedPct >= 100 ? "bg-red-500" : usedPct >= 90 ? "bg-amber-500" : "bg-emerald-500"}`}
              style={{ width: `${usedPct}%` }}
            />
          </div>
        )}
      </td>

      <td className="py-3">
        {user.remainingMinor === null
          ? <span className="text-neutral-400">—</span>
          : <span className={user.remainingMinor === 0 ? "text-red-600" : ""}>{money(user.remainingMinor, currency)}</span>}
        {user.reservedMinor > 0 && (
          <p className="text-xs text-neutral-400" title="Held for requests currently in flight">
            {money(user.reservedMinor, currency)} in flight
          </p>
        )}
      </td>

      <td className="py-3 text-neutral-500">
        {fmt(user.requests)}
        <p className="text-xs text-neutral-400">{fmt(user.tokens)} tokens</p>
      </td>

      <td className="py-3">
        <span className={user.aiStatus === "active" ? "text-emerald-600" : "text-amber-600"}>
          {user.aiStatus}
        </span>
        {user.accessExpiresAt && (
          <p className="text-xs text-neutral-400">
            until {new Date(user.accessExpiresAt).toLocaleDateString()}
          </p>
        )}
      </td>

      <td className="py-3 text-right">
        {editing ? (
          <div className="space-y-2">
            <div className="flex justify-end gap-2">
              <button type="button" onClick={save}
                className="rounded bg-neutral-900 px-3 py-1 text-xs text-white dark:bg-white dark:text-neutral-900">Save</button>
              <button type="button" onClick={onCancel}
                className="rounded border border-neutral-200 px-3 py-1 text-xs dark:border-neutral-700">Cancel</button>
            </div>
            <div className="flex justify-end gap-1">
              <input
                aria-label={`Top up ${user.email} for this period`}
                value={topUp}
                onChange={e => setTopUp(e.target.value)}
                inputMode="decimal"
                placeholder="Top up"
                className="w-20 rounded border border-neutral-200 px-2 py-1 text-xs dark:border-neutral-700 dark:bg-neutral-900"
              />
              <button type="button" onClick={applyTopUp}
                className="rounded border border-neutral-200 px-2 py-1 text-xs dark:border-neutral-700">Add</button>
            </div>
            <div className="flex justify-end gap-2 text-xs">
              <button type="button" onClick={onReset} className="text-blue-500 hover:underline">Reset usage</button>
              <button type="button" onClick={() => onSuspend(user.aiStatus === "active")}
                className={user.aiStatus === "active" ? "text-red-500 hover:underline" : "text-emerald-600 hover:underline"}>
                {user.aiStatus === "active" ? "Suspend" : "Reactivate"}
              </button>
            </div>
          </div>
        ) : (
          <button type="button" onClick={onEdit} className="text-xs text-blue-500 hover:underline">Manage</button>
        )}
      </td>
    </tr>
  );
}

function Summary({ label, value, tone, hint }: { label: string; value: string; tone?: string; hint?: string }) {
  return (
    <div>
      <p className="text-xs text-neutral-400">{label}</p>
      <p className={`mt-0.5 text-base ${tone ?? ""}`}>{value}</p>
      {hint && <p className="text-xs text-neutral-400">{hint}</p>}
    </div>
  );
}
