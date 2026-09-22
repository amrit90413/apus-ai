"use client";
import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import { adminApi, usePolling, fmt, errorMessage } from "@/lib/api";
import type { UserRow, WorkspaceRow, BalanceResult, UserQuotaResult, PersonalKeyRow, LedgerEntry } from "@/lib/api";
import AdminNav from "@/components/AdminNav";

const inputClass = "w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800";
const primaryBtn = "rounded bg-neutral-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900";
const secondaryBtn = "rounded border border-neutral-200 px-3 py-1.5 text-sm hover:bg-neutral-50 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-800";

function RoleBadge({ role }: { role: string }) {
  const colors: Record<string, string> = {
    SuperAdmin: "bg-purple-50 text-purple-700",
    OrgAdmin: "bg-blue-50 text-blue-700",
    WorkspaceAdmin: "bg-cyan-50 text-cyan-700",
    User: "bg-neutral-100 text-neutral-600",
  };
  return (
    <span className={`rounded px-2 py-0.5 text-xs font-medium ${colors[role] ?? colors.User}`}>
      {role}
    </span>
  );
}

function CreateUserModal({ workspaces, onClose, onCreated }: {
  workspaces: WorkspaceRow[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [phone, setPhone] = useState("");
  const [role, setRole] = useState("User");
  const [workspaceId, setWorkspaceId] = useState(workspaces[0]?.id ?? "");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      await adminApi.createUser({ email, password, phoneNumber: phone || undefined, workspaceId, role });
      onCreated();
      onClose();
    } catch (err) {
      setError(errorMessage(err, "Failed to create user. Check if email is already taken."));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div role="dialog" aria-modal="true" aria-labelledby="create-user-title"
        className="w-full max-w-md rounded-xl bg-white p-6 shadow-xl dark:bg-neutral-900">
        <h2 id="create-user-title" className="mb-4 text-base font-medium">Create user</h2>
        <form onSubmit={handleSubmit} className="space-y-3">
          <label htmlFor="cu-email" className="sr-only">Email</label>
          <input id="cu-email" required type="email" placeholder="Email" value={email} onChange={e => setEmail(e.target.value)} className={inputClass} />
          <label htmlFor="cu-password" className="sr-only">Temporary password</label>
          <input id="cu-password" required type="password" placeholder="Temporary password" value={password} onChange={e => setPassword(e.target.value)} className={inputClass} />
          <label htmlFor="cu-phone" className="sr-only">WhatsApp number</label>
          <input id="cu-phone" type="tel" placeholder="WhatsApp number (e.g. 919876543210)" value={phone} onChange={e => setPhone(e.target.value)} className={inputClass} />
          <label htmlFor="cu-role" className="sr-only">Role</label>
          <select id="cu-role" value={role} onChange={e => setRole(e.target.value)} className={inputClass}>
            <option value="User">User</option>
            <option value="WorkspaceAdmin">Workspace Admin</option>
            <option value="OrgAdmin">Org Admin</option>
          </select>
          <label htmlFor="cu-workspace" className="sr-only">Workspace</label>
          <select id="cu-workspace" value={workspaceId} onChange={e => setWorkspaceId(e.target.value)} className={inputClass}>
            {workspaces.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
          {error && <p className="text-xs text-red-600" role="alert">{error}</p>}
          <div className="flex gap-2 pt-1">
            <button type="button" onClick={onClose}
              className="flex-1 rounded border border-neutral-200 py-2 text-sm dark:border-neutral-700">Cancel</button>
            <button type="submit" disabled={loading}
              className="flex-1 rounded bg-neutral-900 py-2 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900">
              {loading ? "Creating…" : "Create"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ Allocate

function parseTokens(raw: string, min: number): number | null {
  const n = Number(raw);
  return raw.trim() !== "" && Number.isInteger(n) && n >= min ? n : null;
}

function LedgerRowView({ e }: { e: LedgerEntry }) {
  const sign = e.delta > 0 ? "+" : "";
  return (
    <tr className="border-t border-neutral-50 dark:border-neutral-800">
      <td className="py-1 text-neutral-500">{e.kind}</td>
      <td className={`py-1 font-mono ${e.delta < 0 ? "text-red-600" : "text-emerald-600"}`}>{sign}{e.delta.toLocaleString()}</td>
      <td className="py-1 font-mono">{e.balanceAfter === null ? "∞" : e.balanceAfter.toLocaleString()}</td>
      <td className="py-1 text-neutral-400">{new Date(e.createdAt).toLocaleString()}</td>
      <td className="py-1 text-neutral-400">{e.reference ?? "—"}</td>
    </tr>
  );
}

function BalanceSection({ user, onChanged }: { user: UserRow; onChanged: () => void }) {
  const ws = user.workspaceId;
  const [balance, setBalance] = useState<BalanceResult | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [mode, setMode] = useState<"idle" | "grant" | "set">("idle");
  const [amount, setAmount] = useState("");
  const [note, setNote] = useState("");
  // Minted when the grant form opens and reused until a grant succeeds, so a
  // retried or double-submitted request can't add the tokens twice.
  const [idempotencyKey, setIdempotencyKey] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setBalance(await adminApi.getUserBalance(user.id, ws));
      setLoadError(null);
    } catch (err) {
      setLoadError(errorMessage(err, "Could not load the token balance."));
    }
  }, [user.id, ws]);

  useEffect(() => { void load(); }, [load]);

  const openForm = (m: "grant" | "set") => {
    setMode(m);
    setAmount("");
    setNote("");
    setError(null);
    if (m === "grant") setIdempotencyKey(crypto.randomUUID());
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const tokens = parseTokens(amount, mode === "grant" ? 1 : 0);
    if (tokens === null) {
      setError(mode === "grant" ? "Enter a whole number of tokens greater than zero." : "Enter a whole number of tokens (0 or more).");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const next = mode === "grant"
        ? await adminApi.grantTokens(user.id, { tokens, note: note.trim() || undefined, idempotencyKey }, ws)
        : await adminApi.setTokens(user.id, { tokens, note: note.trim() || undefined }, ws);
      setBalance(next);
      setMode("idle");
      onChanged();
    } catch (err) {
      setError(errorMessage(err, "The change was not saved."));
    } finally {
      setBusy(false);
    }
  };

  const makeUnlimited = async () => {
    if (!confirm(`Remove the prepaid balance for ${user.email}? They will be limited only by rolling windows.`)) return;
    setBusy(true);
    setError(null);
    try {
      setBalance(await adminApi.revokeBalance(user.id, ws));
      setMode("idle");
      onChanged();
    } catch (err) {
      setError(errorMessage(err, "Could not remove the balance."));
    } finally {
      setBusy(false);
    }
  };

  const history = balance?.history.slice(0, 10) ?? [];

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium text-neutral-500">Token balance</h3>
      {loadError ? (
        <p className="text-xs text-red-600" role="alert">{loadError}</p>
      ) : !balance ? (
        <p className="text-xs text-neutral-400">Loading balance…</p>
      ) : (
        <>
          <div className="mb-3 flex flex-wrap items-center gap-3">
            <p className="text-xl font-medium">
              {balance.enforced && balance.balance !== null ? `${balance.balance.toLocaleString()} tokens` : "Unlimited"}
            </p>
            <span className="text-xs text-neutral-400">
              {balance.enforced ? "prepaid balance enforced" : "rolling windows only"}
            </span>
          </div>
          <div className="mb-3 flex flex-wrap gap-2">
            <button type="button" onClick={() => openForm("grant")} disabled={busy} className={secondaryBtn}>Add tokens</button>
            <button type="button" onClick={() => openForm("set")} disabled={busy} className={secondaryBtn}>Set balance</button>
            {balance.enforced && (
              <button type="button" onClick={makeUnlimited} disabled={busy} className={`${secondaryBtn} text-red-600`}>Make unlimited</button>
            )}
          </div>
          {mode !== "idle" && (
            <form onSubmit={submit} className="mb-3 rounded-lg border border-neutral-100 p-3 dark:border-neutral-800">
              <div className="flex flex-col gap-2 sm:flex-row">
                <div className="sm:w-40">
                  <label htmlFor="bal-amount" className="mb-1 block text-xs text-neutral-500">
                    {mode === "grant" ? "Tokens to add" : "New balance"}
                  </label>
                  <input id="bal-amount" type="number" min={mode === "grant" ? 1 : 0} step={1} required autoFocus
                    value={amount} onChange={e => setAmount(e.target.value)} className={inputClass} placeholder="1000000" />
                </div>
                <div className="flex-1">
                  <label htmlFor="bal-note" className="mb-1 block text-xs text-neutral-500">Note (optional)</label>
                  <input id="bal-note" type="text" maxLength={200} value={note} onChange={e => setNote(e.target.value)}
                    className={inputClass} placeholder="e.g. Q4 allocation" />
                </div>
              </div>
              {error && <p className="mt-2 text-xs text-red-600" role="alert">{error}</p>}
              <div className="mt-2 flex gap-2">
                <button type="submit" disabled={busy} className={primaryBtn}>
                  {busy ? "Saving…" : mode === "grant" ? "Add tokens" : "Set balance"}
                </button>
                <button type="button" onClick={() => setMode("idle")} disabled={busy} className={secondaryBtn}>Cancel</button>
              </div>
            </form>
          )}
          {mode === "idle" && error && <p className="mb-2 text-xs text-red-600" role="alert">{error}</p>}
          {history.length > 0 && (
            <table className="w-full text-xs">
              <thead className="text-left text-neutral-400">
                <tr>
                  <th className="py-1 font-normal">Kind</th>
                  <th className="py-1 font-normal">Delta</th>
                  <th className="py-1 font-normal">After</th>
                  <th className="py-1 font-normal">When</th>
                  <th className="py-1 font-normal">Reference</th>
                </tr>
              </thead>
              <tbody>{history.map(e => <LedgerRowView key={e.id} e={e} />)}</tbody>
            </table>
          )}
        </>
      )}
    </section>
  );
}

function ModelsSection({ user, onChanged }: { user: UserRow; onChanged: () => void }) {
  const ws = user.workspaceId;
  const [quota, setQuota] = useState<UserQuotaResult | null>(null);
  const [policyModels, setPolicyModels] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadQuota = useCallback(async () => {
    const q = await adminApi.getUserQuota(user.id, ws);
    setQuota(q);
    setSelected(new Set(q.effective.allowedModels));
    return q;
  }, [user.id, ws]);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const q = await loadQuota();
        if (!alive) return;
        // The workspace policy is the pick-list; it's optional context, so a
        // failure here only shrinks the list rather than hiding the section.
        const wsId = ws ?? q.workspaceId;
        try {
          const p = await adminApi.getWorkspacePolicy(wsId);
          if (alive) setPolicyModels(p.policy?.allowedModels ?? p.allowedModels ?? []);
        } catch { /* fall back to the user's effective models */ }
      } catch (err) {
        if (alive) setLoadError(errorMessage(err, "Could not load the model allowlist."));
      }
    })();
    return () => { alive = false; };
  }, [loadQuota, ws]);

  const options = useMemo(() => {
    const all = new Set<string>([
      ...policyModels,
      ...(quota?.effective.allowedModels ?? []),
      ...(quota?.override?.allowedModels ?? []),
    ]);
    return [...all].sort();
  }, [policyModels, quota]);

  const toggle = (m: string) => setSelected(prev => {
    const next = new Set(prev);
    if (next.has(m)) next.delete(m); else next.add(m);
    return next;
  });

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await adminApi.setUserModels(user.id, options.filter(m => selected.has(m)), ws);
      await loadQuota();
      onChanged();
    } catch (err) {
      setError(errorMessage(err, "Could not save the model allowlist."));
    } finally {
      setBusy(false);
    }
  };

  const inherit = async () => {
    if (!confirm(`Drop the per-user override for ${user.email} and inherit the workspace policy?`)) return;
    setBusy(true);
    setError(null);
    try {
      await adminApi.clearUserQuota(user.id, ws);
      await loadQuota();
      onChanged();
    } catch (err) {
      setError(errorMessage(err, "Could not clear the override."));
    } finally {
      setBusy(false);
    }
  };

  const dirty = quota !== null && (
    selected.size !== quota.effective.allowedModels.length ||
    quota.effective.allowedModels.some(m => !selected.has(m))
  );

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium text-neutral-500">Allowed models</h3>
      {loadError ? (
        <p className="text-xs text-red-600" role="alert">{loadError}</p>
      ) : !quota ? (
        <p className="text-xs text-neutral-400">Loading models…</p>
      ) : (
        <>
          <p className="mb-2 text-xs text-neutral-400">
            {quota.hasOverride
              ? "Per-user override in effect."
              : "Inherited from the workspace policy."}
          </p>
          {options.length === 0 ? (
            <p className="mb-2 text-xs text-neutral-400">No models in the workspace policy yet — set them on the workspace first.</p>
          ) : (
            <fieldset className="mb-3 grid gap-1 sm:grid-cols-2">
              <legend className="sr-only">Models this user may call</legend>
              {options.map(m => {
                const id = `model-${m.replace(/[^a-z0-9]/gi, "-")}`;
                return (
                  <div key={m} className="flex items-center gap-2">
                    <input id={id} type="checkbox" checked={selected.has(m)} onChange={() => toggle(m)} disabled={busy}
                      className="h-4 w-4 rounded border-neutral-300 dark:border-neutral-600" />
                    <label htmlFor={id} className="font-mono text-xs">
                      {m}
                      {!policyModels.includes(m) && <span className="ml-1 text-neutral-400">(not in workspace policy)</span>}
                    </label>
                  </div>
                );
              })}
            </fieldset>
          )}
          {error && <p className="mb-2 text-xs text-red-600" role="alert">{error}</p>}
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={save} disabled={busy || !dirty || options.length === 0} className={primaryBtn}>
              {busy ? "Saving…" : "Save models"}
            </button>
            {quota.hasOverride && (
              <button type="button" onClick={inherit} disabled={busy} className={secondaryBtn}>Inherit workspace models</button>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function KeysSection({ user }: { user: UserRow }) {
  const [keys, setKeys] = useState<PersonalKeyRow[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setKeys((await adminApi.listUserKeys(user.id)).keys);
      setLoadError(null);
    } catch (err) {
      setLoadError(errorMessage(err, "Could not load personal keys."));
    }
  }, [user.id]);

  useEffect(() => { void load(); }, [load]);

  const revoke = async (k: PersonalKeyRow) => {
    if (!confirm(`Revoke key "${k.name}" (${k.prefix}…)? Any IDE using it stops working immediately.`)) return;
    setRevoking(k.id);
    setError(null);
    try {
      await adminApi.revokeUserKey(user.id, k.id);
      await load();
    } catch (err) {
      setError(errorMessage(err, "Could not revoke the key."));
    } finally {
      setRevoking(null);
    }
  };

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium text-neutral-500">Personal keys</h3>
      {loadError ? (
        <p className="text-xs text-red-600" role="alert">{loadError}</p>
      ) : !keys ? (
        <p className="text-xs text-neutral-400">Loading keys…</p>
      ) : keys.length === 0 ? (
        <p className="text-xs text-neutral-400">No personal keys yet — the user mints one with the CLI.</p>
      ) : (
        <table className="w-full text-xs">
          <thead className="text-left text-neutral-400">
            <tr>
              <th className="py-1 font-normal">Name</th>
              <th className="py-1 font-normal">Prefix</th>
              <th className="py-1 font-normal">Created</th>
              <th className="py-1 font-normal">Last used</th>
              <th className="py-1 font-normal">Status</th>
              <th className="py-1 font-normal" />
            </tr>
          </thead>
          <tbody>
            {keys.map(k => (
              <tr key={k.id} className="border-t border-neutral-50 dark:border-neutral-800">
                <td className="py-1">{k.name}</td>
                <td className="py-1 font-mono text-neutral-500">{k.prefix}…</td>
                <td className="py-1 text-neutral-400">{new Date(k.createdAt).toLocaleDateString()}</td>
                <td className="py-1 text-neutral-400">{k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : "Never"}</td>
                <td className={`py-1 ${k.revokedAt ? "text-neutral-400" : "text-emerald-600"}`}>
                  {k.revokedAt ? `revoked ${new Date(k.revokedAt).toLocaleDateString()}` : "active"}
                </td>
                <td className="py-1 text-right">
                  {!k.revokedAt && (
                    <button type="button" onClick={() => revoke(k)} disabled={revoking === k.id}
                      className="text-red-500 hover:underline disabled:opacity-50">
                      {revoking === k.id ? "Revoking…" : "Revoke"}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {error && <p className="mt-2 text-xs text-red-600" role="alert">{error}</p>}
    </section>
  );
}

function AllocateModal({ user, onClose, onChanged }: {
  user: UserRow;
  onClose: () => void;
  onChanged: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div role="dialog" aria-modal="true" aria-labelledby="allocate-title"
        className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl bg-white p-6 shadow-xl dark:bg-neutral-900">
        <div className="mb-4 flex items-start justify-between gap-4">
          <div>
            <h2 id="allocate-title" className="text-base font-medium">Allocate</h2>
            <p className="text-xs text-neutral-400">{user.email}</p>
          </div>
          <button type="button" onClick={onClose} aria-label="Close"
            className="rounded px-2 py-1 text-sm text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800">
            ✕
          </button>
        </div>
        <div className="space-y-6">
          <BalanceSection user={user} onChanged={onChanged} />
          <ModelsSection user={user} onChanged={onChanged} />
          <KeysSection user={user} />
        </div>
        <div className="mt-6 flex justify-end">
          <button type="button" onClick={onClose} className={secondaryBtn}>Close</button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------- Page

export default function AdminUsersPage() {
  const [showCreate, setShowCreate] = useState(false);
  const [allocateUser, setAllocateUser] = useState<UserRow | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [activity, setActivity] = useState<Record<string, unknown[] | null>>({});

  const usersLoader = useCallback(() => adminApi.listUsers(), [refreshKey]);
  const workspacesLoader = useCallback(() => adminApi.listWorkspaces(), []);
  const data = usePolling(usersLoader, 10000);
  const wsData = usePolling(workspacesLoader, 30000);
  const refresh = useCallback(() => setRefreshKey(k => k + 1), []);
  const closeAllocate = useCallback(() => setAllocateUser(null), []);
  const closeCreate = useCallback(() => setShowCreate(false), []);

  const toggleActivity = async (userId: string) => {
    if (expandedId === userId) { setExpandedId(null); return; }
    setExpandedId(userId);
    if (!activity[userId]) {
      const result = await adminApi.getUserActivity(userId);
      setActivity(prev => ({ ...prev, [userId]: result.activity }));
    }
  };

  const toggleActive = async (userId: string, current: boolean) => {
    await adminApi.updateUser(userId, { isActive: !current });
    refresh();
  };

  const revokeSession = async (userId: string) => {
    if (!confirm("Force-logout this user from all devices?")) return;
    await adminApi.revokeUserSessions(userId);
    refresh();
  };

  if (!data) return <div className="p-8 text-neutral-500">Loading users…</div>;

  const users: UserRow[] = data.users ?? [];

  return (
    <div className="mx-auto max-w-5xl p-8">
      <AdminNav />
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Team members</h1>
          <p className="text-xs text-neutral-400">Org admin · create users, allocate tokens and models, manage access</p>
        </div>
        <button type="button" onClick={() => setShowCreate(true)}
          className="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white dark:bg-white dark:text-neutral-900">
          + Add user
        </button>
      </header>

      {/* Stats bar */}
      <div className="mb-6 grid grid-cols-3 gap-3">
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900">
          <p className="text-sm text-neutral-500">Total users</p>
          <p className="mt-1 text-2xl font-medium">{users.length}</p>
        </div>
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900">
          <p className="text-sm text-neutral-500">Active</p>
          <p className="mt-1 text-2xl font-medium">{users.filter(u => u.isActive).length}</p>
        </div>
        <div className="rounded-lg bg-neutral-50 p-4 dark:bg-neutral-900">
          <p className="text-sm text-neutral-500">Est. cost (30d)</p>
          <p className="mt-1 text-2xl font-medium">
            ${users.reduce((s, u) => s + (u.usage?.costUsd ?? 0), 0).toFixed(2)}
          </p>
        </div>
      </div>

      {/* User table */}
      <table className="w-full text-sm">
        <thead className="text-left text-neutral-400">
          <tr>
            <th className="py-2 font-normal">User</th>
            <th className="py-2 font-normal">Role</th>
            <th className="py-2 font-normal">Balance</th>
            <th className="py-2 font-normal">Tokens (30d)</th>
            <th className="py-2 font-normal">Cost (30d)</th>
            <th className="py-2 font-normal">Last active</th>
            <th className="py-2 font-normal">Status</th>
            <th className="py-2 font-normal" />
          </tr>
        </thead>
        <tbody>
          {users.map(u => (
            <Fragment key={u.id}>
              <tr className="border-t border-neutral-100 dark:border-neutral-800">
                <td className="py-2.5">
                  <p className="font-medium">{u.email}</p>
                  {u.phoneNumber && <p className="text-xs text-neutral-400">{u.phoneNumber}</p>}
                </td>
                <td className="py-2.5"><RoleBadge role={u.role} /></td>
                <td className="py-2.5">
                  {u.tokenBalance === null || u.tokenBalance === undefined
                    ? <span className="text-neutral-400">Unlimited</span>
                    : fmt(u.tokenBalance)}
                </td>
                <td className="py-2.5">{u.usage ? fmt((u.usage.inputTokens ?? 0) + (u.usage.outputTokens ?? 0)) : "—"}</td>
                <td className="py-2.5">{u.usage ? `$${u.usage.costUsd.toFixed(2)}` : "—"}</td>
                <td className="py-2.5 text-neutral-400 text-xs">
                  {u.usage?.lastActive ? new Date(u.usage.lastActive).toLocaleDateString() : "Never"}
                </td>
                <td className="py-2.5">
                  <span className={u.isActive ? "text-emerald-600" : "text-neutral-400"}>
                    {u.isActive ? "active" : "disabled"}
                  </span>
                </td>
                <td className="py-2.5 text-right whitespace-nowrap">
                  <button type="button" onClick={() => setAllocateUser(u)}
                    className="mr-2 text-xs text-blue-500 hover:underline">
                    Allocate
                  </button>
                  <button type="button" onClick={() => toggleActivity(u.id)}
                    className="mr-2 text-xs text-blue-500 hover:underline">
                    {expandedId === u.id ? "Hide" : "Activity"}
                  </button>
                  <button type="button" onClick={() => toggleActive(u.id, u.isActive)}
                    className="mr-2 text-xs text-neutral-400 hover:underline">
                    {u.isActive ? "Disable" : "Enable"}
                  </button>
                  <button type="button" onClick={() => revokeSession(u.id)}
                    className="text-xs text-red-400 hover:underline">Logout</button>
                </td>
              </tr>
              {expandedId === u.id && (
                <tr className="border-t border-neutral-50 dark:border-neutral-900">
                  <td colSpan={8} className="bg-neutral-50 px-4 py-3 dark:bg-neutral-900/50">
                    {!activity[u.id] ? (
                      <p className="text-xs text-neutral-400">Loading activity…</p>
                    ) : (activity[u.id] as any[]).length === 0 ? (
                      <p className="text-xs text-neutral-400">No activity in last 14 days.</p>
                    ) : (
                      <div className="space-y-1">
                        {(activity[u.id] as any[]).map((a: any) => (
                          <div key={a.day} className="flex items-center gap-4 text-xs">
                            <span className="w-24 text-neutral-500">{a.day}</span>
                            <span>{fmt(a.totalTokens)} tokens</span>
                            <span className="text-neutral-400">${Number(a.costUsd).toFixed(3)}</span>
                            <span className="text-neutral-400">{a.requests} req</span>
                            <span className="text-neutral-300">{(a.modelsUsed ?? []).join(", ")}</span>
                          </div>
                        ))}
                      </div>
                    )}
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>

      {showCreate && wsData && (
        <CreateUserModal
          workspaces={wsData.workspaces}
          onClose={closeCreate}
          onCreated={refresh}
        />
      )}

      {allocateUser && (
        <AllocateModal
          user={allocateUser}
          onClose={closeAllocate}
          onChanged={refresh}
        />
      )}
    </div>
  );
}
