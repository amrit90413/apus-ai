"use client";
import { useCallback, useState } from "react";
import { adminApi, usePolling, fmt } from "@/lib/api";
import type { UserRow, WorkspaceRow } from "@/lib/api";

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

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      await adminApi.createUser({ email, password, phoneNumber: phone || undefined, workspaceId, role });
      onCreated();
      onClose();
    } catch {
      setError("Failed to create user. Check if email is already taken.");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="w-full max-w-md rounded-xl bg-white p-6 shadow-xl dark:bg-neutral-900">
        <h2 className="mb-4 text-base font-medium">Create user</h2>
        <form onSubmit={handleSubmit} className="space-y-3">
          <input required type="email" placeholder="Email" value={email} onChange={e => setEmail(e.target.value)}
            className="w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800" />
          <input required type="password" placeholder="Temporary password" value={password} onChange={e => setPassword(e.target.value)}
            className="w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800" />
          <input type="tel" placeholder="WhatsApp number (e.g. 919876543210)" value={phone} onChange={e => setPhone(e.target.value)}
            className="w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800" />
          <select value={role} onChange={e => setRole(e.target.value)}
            className="w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800">
            <option value="User">User</option>
            <option value="WorkspaceAdmin">Workspace Admin</option>
            <option value="OrgAdmin">Org Admin</option>
          </select>
          <select value={workspaceId} onChange={e => setWorkspaceId(e.target.value)}
            className="w-full rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-800">
            {workspaces.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
          {error && <p className="text-xs text-red-600">{error}</p>}
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

export default function AdminUsersPage() {
  const [showCreate, setShowCreate] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [activity, setActivity] = useState<Record<string, unknown[] | null>>({});

  const usersLoader = useCallback(() => adminApi.listUsers(), [refreshKey]);
  const workspacesLoader = useCallback(() => adminApi.listWorkspaces(), []);
  const data = usePolling(usersLoader, 10000);
  const wsData = usePolling(workspacesLoader, 30000);

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
    setRefreshKey(k => k + 1);
  };

  const revokeSession = async (userId: string) => {
    if (!confirm("Force-logout this user from all devices?")) return;
    await adminApi.revokeUserSessions(userId);
    setRefreshKey(k => k + 1);
  };

  if (!data) return <div className="p-8 text-neutral-500">Loading users…</div>;

  const users: UserRow[] = data.users ?? [];

  return (
    <div className="mx-auto max-w-5xl p-8">
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Team members</h1>
          <p className="text-xs text-neutral-400">Org admin · create users, track usage, manage access</p>
        </div>
        <button onClick={() => setShowCreate(true)}
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
            <th className="py-2 font-normal">Tokens (30d)</th>
            <th className="py-2 font-normal">Cost (30d)</th>
            <th className="py-2 font-normal">Last active</th>
            <th className="py-2 font-normal">Status</th>
            <th className="py-2 font-normal" />
          </tr>
        </thead>
        <tbody>
          {users.map(u => (
            <>
              <tr key={u.id} className="border-t border-neutral-100 dark:border-neutral-800">
                <td className="py-2.5">
                  <p className="font-medium">{u.email}</p>
                  {u.phoneNumber && <p className="text-xs text-neutral-400">{u.phoneNumber}</p>}
                </td>
                <td className="py-2.5"><RoleBadge role={u.role} /></td>
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
                <td className="py-2.5 text-right">
                  <button onClick={() => toggleActivity(u.id)}
                    className="mr-2 text-xs text-blue-500 hover:underline">
                    {expandedId === u.id ? "Hide" : "Activity"}
                  </button>
                  <button onClick={() => toggleActive(u.id, u.isActive)}
                    className="mr-2 text-xs text-neutral-400 hover:underline">
                    {u.isActive ? "Disable" : "Enable"}
                  </button>
                  <button onClick={() => revokeSession(u.id)}
                    className="text-xs text-red-400 hover:underline">Logout</button>
                </td>
              </tr>
              {expandedId === u.id && (
                <tr key={`${u.id}-activity`} className="border-t border-neutral-50 dark:border-neutral-900">
                  <td colSpan={7} className="bg-neutral-50 px-4 py-3 dark:bg-neutral-900/50">
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
            </>
          ))}
        </tbody>
      </table>

      {showCreate && wsData && (
        <CreateUserModal
          workspaces={wsData.workspaces}
          onClose={() => setShowCreate(false)}
          onCreated={() => setRefreshKey(k => k + 1)}
        />
      )}
    </div>
  );
}
