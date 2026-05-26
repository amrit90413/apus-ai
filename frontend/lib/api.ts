// Thin typed client for the gateway admin API. The dashboard authenticates with
// the same JWT the CLI uses (stored in an httpOnly cookie set by the web login).
const API = process.env.NEXT_PUBLIC_API_BASE ?? "/api";

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${API}${path}`, { credentials: "include" });
  if (!res.ok) throw new Error(`${res.status} ${path}`);
  return res.json() as Promise<T>;
}

export interface OrgRow { id: string; name: string; seats: number; used: number; limit: number; status: "active" | "near" | "throttled"; }
export interface ConsumerRow { userId: string; email: string; inputTokens: number; outputTokens: number; pctOfQuota: number; costUsd: number; }
export interface Anomaly { id: string; userEmail: string; kind: string; detail: string; at: string; }
export interface WindowState { name: string; used: number; limit: number; resetInSeconds: number; }

export const adminApi = {
  // Super-admin: cross-tenant rollup (ClickHouse-backed).
  organizations: () => get<{ orgs: OrgRow[]; totals: { orgs: number; employees: number; tokensToday: number; costToday: number } }>("/v1/admin/organizations"),
  anomalies: () => get<{ anomalies: Anomaly[] }>("/v1/admin/anomalies"),
  // Org-admin: per-employee tracking within a workspace.
  topConsumers: (workspaceId: string) => get<{ consumers: ConsumerRow[]; window: WindowState }>(`/v1/admin/workspaces/${workspaceId}/top-consumers`),
  // User: own usage.
  myUsage: () => get<{ windows: WindowState[] }>("/v1/me/usage"),
};

export function fmt(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`;
  return n.toString();
}

// Poll an async loader every `ms` for "realtime" dashboards without websockets.
// Swap for a Server-Sent Events subscription in production for sub-second updates.
import { useEffect, useState } from "react";
export function usePolling<T>(loader: () => Promise<T>, ms = 5000): T | null {
  const [data, setData] = useState<T | null>(null);
  useEffect(() => {
    let alive = true;
    const tick = () => loader().then(d => { if (alive) setData(d); }).catch(() => {});
    tick();
    const id = setInterval(tick, ms);
    return () => { alive = false; clearInterval(id); };
  }, [loader, ms]);
  return data;
}
