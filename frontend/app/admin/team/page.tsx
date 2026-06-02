"use client";
import { useCallback, useState } from "react";
import { adminApi, usePolling } from "@/lib/api";
import type { WorkspaceRow } from "@/lib/api";

export default function AdminTeamPage() {
  const [selectedWs, setSelectedWs] = useState<string | null>(null);
  const [members, setMembers] = useState<Record<string, unknown[]>>({});
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState("");
  const [creating, setCreating] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);

  const loader = useCallback(() => adminApi.listWorkspaces(), [refreshKey]);
  const data = usePolling(loader, 15000);

  const loadMembers = async (wsId: string) => {
    if (selectedWs === wsId) { setSelectedWs(null); return; }
    setSelectedWs(wsId);
    if (!members[wsId]) {
      const res = await adminApi.getWorkspaceMembers(wsId);
      setMembers(prev => ({ ...prev, [wsId]: res.members }));
    }
  };

  const createWorkspace = async (e: React.FormEvent) => {
    e.preventDefault();
    setCreating(true);
    try {
      await adminApi.createWorkspace(newName);
      setNewName("");
      setShowCreate(false);
      setRefreshKey(k => k + 1);
    } finally {
      setCreating(false);
    }
  };

  const removeMember = async (wsId: string, userId: string) => {
    if (!confirm("Remove this member from the workspace?")) return;
    await adminApi.removeWorkspaceMember(wsId, userId);
    setMembers(prev => ({
      ...prev,
      [wsId]: (prev[wsId] ?? []).filter((m: any) => m.id !== userId),
    }));
  };

  if (!data) return <div className="p-8 text-neutral-500">Loading teams…</div>;

  const workspaces: WorkspaceRow[] = data.workspaces ?? [];

  return (
    <div className="mx-auto max-w-4xl p-8">
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-lg font-medium">Teams</h1>
          <p className="text-xs text-neutral-400">Org admin · manage workspaces and members</p>
        </div>
        <button onClick={() => setShowCreate(v => !v)}
          className="rounded-md border border-neutral-200 px-3 py-1.5 text-sm hover:bg-neutral-50 dark:border-neutral-700">
          {showCreate ? "Cancel" : "+ New team"}
        </button>
      </header>

      {showCreate && (
        <form onSubmit={createWorkspace} className="mb-6 flex gap-2">
          <input required value={newName} onChange={e => setNewName(e.target.value)}
            placeholder="Team name (e.g. Engineering)"
            className="flex-1 rounded border border-neutral-200 px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-900" />
          <button type="submit" disabled={creating}
            className="rounded bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900">
            {creating ? "Creating…" : "Create"}
          </button>
        </form>
      )}

      <div className="space-y-3">
        {workspaces.map(ws => (
          <div key={ws.id} className="rounded-lg border border-neutral-100 dark:border-neutral-800">
            <button onClick={() => loadMembers(ws.id)}
              className="flex w-full items-center justify-between px-4 py-3 text-left">
              <div>
                <p className="font-medium">{ws.name}</p>
                <p className="text-xs text-neutral-400">{ws.memberCount} members</p>
              </div>
              <span className="text-xs text-neutral-400">{selectedWs === ws.id ? "▲" : "▼"}</span>
            </button>

            {selectedWs === ws.id && (
              <div className="border-t border-neutral-100 px-4 pb-4 dark:border-neutral-800">
                {!members[ws.id] ? (
                  <p className="pt-3 text-xs text-neutral-400">Loading…</p>
                ) : (members[ws.id] as any[]).length === 0 ? (
                  <p className="pt-3 text-xs text-neutral-400">No members yet.</p>
                ) : (
                  <table className="mt-3 w-full text-sm">
                    <thead className="text-left text-neutral-400">
                      <tr>
                        <th className="py-1 font-normal">Email</th>
                        <th className="py-1 font-normal">Phone</th>
                        <th className="py-1 font-normal">Role</th>
                        <th className="py-1 font-normal">Status</th>
                        <th className="py-1 font-normal" />
                      </tr>
                    </thead>
                    <tbody>
                      {(members[ws.id] as any[]).map((m: any) => (
                        <tr key={m.id} className="border-t border-neutral-50 dark:border-neutral-900">
                          <td className="py-2">{m.email}</td>
                          <td className="py-2 text-neutral-400">{m.phoneNumber ?? "—"}</td>
                          <td className="py-2 text-neutral-400">{m.role}</td>
                          <td className={`py-2 ${m.isActive ? "text-emerald-600" : "text-neutral-400"}`}>
                            {m.isActive ? "active" : "disabled"}
                          </td>
                          <td className="py-2 text-right">
                            <button onClick={() => removeMember(ws.id, m.id)}
                              className="text-xs text-red-400 hover:underline">Remove</button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
