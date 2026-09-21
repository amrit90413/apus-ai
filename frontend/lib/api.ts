// Thin typed client for the gateway admin API. The dashboard authenticates with
// the same JWT the CLI uses, held in localStorage and refreshed transparently.
const API = process.env.NEXT_PUBLIC_API_BASE ?? "/api";

const ACCESS_KEY = "access_token";
const REFRESH_KEY = "refresh_token";
const EXPIRES_KEY = "access_expires_at";

// Refresh this far ahead of expiry so an in-flight request can't straddle it.
const REFRESH_SKEW_MS = 60_000;

export class SessionExpiredError extends Error {
  constructor() { super("Session expired"); this.name = "SessionExpiredError"; }
}

function store(tokens: LoginResult): void {
  localStorage.setItem(ACCESS_KEY, tokens.accessToken);
  localStorage.setItem(REFRESH_KEY, tokens.refreshToken);
  localStorage.setItem(EXPIRES_KEY, tokens.accessExpiresAt);
}

function endSession(): SessionExpiredError {
  localStorage.removeItem(ACCESS_KEY);
  localStorage.removeItem(REFRESH_KEY);
  localStorage.removeItem(EXPIRES_KEY);
  // usePolling swallows rejections, so without this the dashboard would sit on
  // stale data instead of showing the user they have been signed out.
  if (typeof window !== "undefined" && window.location.pathname !== "/login")
    window.location.assign("/login");
  return new SessionExpiredError();
}

// The gateway rotates the refresh token on every use, so two concurrent refreshes
// would leave the second holding a hash the server has already replaced. Every
// caller therefore shares one in-flight request.
let refreshInFlight: Promise<string> | null = null;

function refreshAccessToken(): Promise<string> {
  if (refreshInFlight) return refreshInFlight;

  refreshInFlight = (async () => {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    if (!refreshToken) throw endSession();

    let res: Response;
    try {
      res = await fetch(`${API}/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken }),
      });
    } catch {
      // Network blip: keep the session and let the caller retry on the next poll.
      throw new Error("Could not reach the gateway to refresh the session.");
    }

    if (!res.ok) throw endSession();

    const tokens = (await res.json()) as LoginResult;
    store(tokens);
    return tokens.accessToken;
  })();

  return refreshInFlight.finally(() => { refreshInFlight = null; });
}

/** Returns a usable access token, refreshing when it is within the skew of expiry. */
export async function getValidAccessToken(force = false): Promise<string | null> {
  if (typeof window === "undefined") return null;

  const token = localStorage.getItem(ACCESS_KEY);
  if (!token) return null;
  if (force) return refreshAccessToken();

  const expiresAt = localStorage.getItem(EXPIRES_KEY);
  // A session stored before expiry tracking existed: refresh once to learn it.
  if (!expiresAt) return refreshAccessToken();

  const msLeft = new Date(expiresAt).getTime() - Date.now();
  if (Number.isNaN(msLeft)) return refreshAccessToken();

  return msLeft > REFRESH_SKEW_MS ? token : refreshAccessToken();
}

async function request<T>(path: string, init: RequestInit = {}): Promise<Response> {
  const send = async (token: string | null) =>
    fetch(`${API}${path}`, {
      ...init,
      credentials: "include",
      headers: {
        ...(init.headers as Record<string, string> | undefined),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    });

  let res = await send(await getValidAccessToken());

  // Still rejected with a token we believed was valid — the session may have been
  // revoked, or the gateway restarted. Force one refresh and retry exactly once.
  if (res.status === 401 && localStorage.getItem(ACCESS_KEY)) {
    res = await send(await getValidAccessToken(true));
  }

  if (!res.ok) throw new Error(`${res.status} ${path}`);
  return res;
}

async function get<T>(path: string): Promise<T> {
  const res = await request<T>(path);
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const res = await request<T>(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  return res.json() as Promise<T>;
}

async function del(path: string): Promise<void> {
  await request<void>(path, { method: "DELETE" });
}

export interface LoginResult { accessToken: string; refreshToken: string; accessExpiresAt: string; status?: never; }
export interface OtpPendingResult { status: "otp_required"; pendingToken: string; message: string; }

export const authApi = {
  login: async (email: string, password: string): Promise<LoginResult | OtpPendingResult> => {
    const res = await fetch(`${API}/v1/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password, deviceName: "web" }),
    });
    if (res.status === 202) return res.json() as Promise<OtpPendingResult>;
    if (!res.ok) throw new Error(`${res.status}`);
    return res.json() as Promise<LoginResult>;
  },
  verifyOtp: async (pendingToken: string, otp: string): Promise<LoginResult> => {
    const res = await fetch(`${API}/v1/auth/verify-otp`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pendingToken, otp }),
    });
    if (!res.ok) throw new Error(`${res.status}`);
    return res.json() as Promise<LoginResult>;
  },
  saveToken: (tokens: LoginResult) => store(tokens),
  logout: async () => {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    if (refreshToken) {
      // Best effort: revoke server-side so the refresh token dies with the session.
      try {
        await fetch(`${API}/v1/auth/logout`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ refreshToken }),
        });
      } catch { /* revoke on the server is best effort; clear locally regardless */ }
    }
    endSession();
  },
  clearToken: () => {
    localStorage.removeItem(ACCESS_KEY);
    localStorage.removeItem(REFRESH_KEY);
    localStorage.removeItem(EXPIRES_KEY);
  },
};

export interface OrgRow { id: string; name: string; seats: number; used: number; limit: number; status: "active" | "near" | "throttled"; }
export interface ConsumerRow { userId: string; email: string; inputTokens: number; outputTokens: number; pctOfQuota: number; costUsd: number; }
export interface Anomaly { id: string; userEmail: string; kind: string; detail: string; at: string; }
export interface WindowState { name: string; used: number; limit: number; resetInSeconds: number; }
export interface ProviderKeyRow { id: string; provider: string; keyHint: string; isActive: boolean; createdAt: string; }

export interface UserUsage { inputTokens: number; outputTokens: number; costUsd: number; requests: number; lastActive: string; }
export interface UserRow { id: string; email: string; phoneNumber?: string; phoneVerified: boolean; isActive: boolean; createdAt: string; role: string; workspaceId?: string; usage?: UserUsage; }
export interface WorkspaceRow { id: string; name: string; isActive: boolean; memberCount: number; }

export const adminApi = {
  // Super-admin: cross-tenant rollup (ClickHouse-backed).
  organizations: () => get<{ orgs: OrgRow[]; totals: { orgs: number; employees: number; tokensToday: number; costToday: number } }>("/v1/admin/organizations"),
  anomalies: () => get<{ anomalies: Anomaly[] }>("/v1/admin/anomalies"),
  // Org-admin: per-employee tracking within a workspace.
  topConsumers: (workspaceId: string) => get<{ consumers: ConsumerRow[]; window: WindowState }>(`/v1/admin/workspaces/${workspaceId}/top-consumers`),
  // User: own usage.
  myUsage: () => get<{ windows: WindowState[] }>("/v1/me/usage"),
  // Super-admin: provider API key management.
  listProviderKeys: () => get<{ keys: ProviderKeyRow[] }>("/v1/admin/provider-keys"),
  addProviderKey: (provider: string, apiKey: string) =>
    post<{ id: string }>("/v1/admin/provider-keys", { provider, apiKey }),
  removeProviderKey: (id: string) => del(`/v1/admin/provider-keys/${id}`),

  // Org-admin: user management.
  listUsers: () => get<{ users: UserRow[] }>("/v1/admin/users"),
  createUser: (body: { email: string; password: string; phoneNumber?: string; workspaceId: string; role: string }) =>
    post<{ userId: string; email: string }>("/v1/admin/users", body),
  updateUser: (id: string, body: { isActive?: boolean; role?: string; phoneNumber?: string }) =>
    post<{ updated: boolean }>(`/v1/admin/users/${id}`, body),
  revokeUserSessions: (id: string) =>
    post<{ sessionsRevoked: number }>(`/v1/admin/users/${id}/revoke-sessions`, {}),
  getUserActivity: (id: string) =>
    get<{ activity: unknown[] }>(`/v1/admin/users/${id}/activity`),

  // Org-admin: workspace/team management.
  listWorkspaces: () => get<{ workspaces: WorkspaceRow[] }>("/v1/admin/workspaces"),
  createWorkspace: (name: string) =>
    post<{ workspaceId: string }>("/v1/admin/workspaces", { name }),
  getWorkspaceMembers: (id: string) =>
    get<{ members: unknown[] }>(`/v1/admin/workspaces/${id}/members`),
  addWorkspaceMember: (wsId: string, userId: string, role: string) =>
    post<unknown>(`/v1/admin/workspaces/${wsId}/members`, { userId, role }),
  removeWorkspaceMember: (wsId: string, userId: string) =>
    del(`/v1/admin/workspaces/${wsId}/members/${userId}`),
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
