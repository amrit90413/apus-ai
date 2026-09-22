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

/**
 * A non-2xx response from the gateway. `code` is the server's snake_case error
 * code (`{ error: { code, message } }`) when the body carried one, otherwise
 * `http_<status>`; `message` is the server's human text when present.
 */
export class ApiError extends Error {
  readonly code: string;
  readonly status: number;
  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
  }
}

async function toApiError(res: Response, path: string): Promise<ApiError> {
  let code = `http_${res.status}`;
  let message = `${res.status} ${path}`;
  try {
    const body = (await res.json()) as { error?: { code?: string; message?: string } } | null;
    if (body?.error?.code) code = body.error.code;
    if (body?.error?.message) message = body.error.message;
  } catch { /* empty or non-JSON body: keep the generic message */ }
  return new ApiError(res.status, code, message);
}

function store(tokens: LoginResult): void {
  localStorage.setItem(ACCESS_KEY, tokens.accessToken);
  localStorage.setItem(REFRESH_KEY, tokens.refreshToken);
  localStorage.setItem(EXPIRES_KEY, tokens.accessExpiresAt);
}

// Pages that work without a session; never bounce these to /login.
const PUBLIC_PATHS = new Set(["/login", "/register"]);

function endSession(): SessionExpiredError {
  localStorage.removeItem(ACCESS_KEY);
  localStorage.removeItem(REFRESH_KEY);
  localStorage.removeItem(EXPIRES_KEY);
  // usePolling swallows rejections, so without this the dashboard would sit on
  // stale data instead of showing the user they have been signed out.
  if (typeof window !== "undefined" && !PUBLIC_PATHS.has(window.location.pathname))
    window.location.assign(`/login?next=${encodeURIComponent(window.location.pathname)}`);
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

  // No session at all: don't fire an unauthenticated request every poll tick —
  // send the visitor to sign in immediately.
  if (typeof window !== "undefined" && !localStorage.getItem(ACCESS_KEY)) throw endSession();

  let res = await send(await getValidAccessToken());

  // Still rejected with a token we believed was valid — the session may have been
  // revoked, or the gateway restarted. Force one refresh and retry exactly once.
  if (res.status === 401 && localStorage.getItem(ACCESS_KEY)) {
    res = await send(await getValidAccessToken(true));
  }

  if (!res.ok) throw await toApiError(res, path);
  return res;
}

async function get<T>(path: string): Promise<T> {
  const res = await request<T>(path);
  return res.json() as Promise<T>;
}

async function sendJson<T>(method: "POST" | "PUT" | "PATCH", path: string, body: unknown): Promise<T> {
  const res = await request<T>(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body: unknown): Promise<T> {
  return sendJson<T>("POST", path, body);
}

async function put<T>(path: string, body: unknown): Promise<T> {
  return sendJson<T>("PUT", path, body);
}

async function patch<T>(path: string, body: unknown): Promise<T> {
  return sendJson<T>("PATCH", path, body);
}

async function del(path: string): Promise<void> {
  await request<void>(path, { method: "DELETE" });
}

/** DELETE that returns a JSON body (e.g. the balance endpoints echo the new state). */
async function delJson<T>(path: string): Promise<T> {
  const res = await request<T>(path, { method: "DELETE" });
  return res.json() as Promise<T>;
}

/** Optional `?workspaceId=` — only needed when the user is in more than one workspace. */
function wsQuery(workspaceId?: string): string {
  return workspaceId ? `?workspaceId=${encodeURIComponent(workspaceId)}` : "";
}

// Public (unauthenticated) endpoints share the error shape but must not attach
// or refresh a JWT, so they bypass `request`.
async function publicFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API}${path}`, init);
  if (!res.ok) throw await toApiError(res, path);
  return res.json() as Promise<T>;
}

export interface LoginResult { accessToken: string; refreshToken: string; accessExpiresAt: string; status?: never; }
export interface OtpPendingResult { status: "otp_required"; pendingToken: string; message: string; }

export interface RegisterAvailability {
  enabled: boolean;
  inviteCodeRequired: boolean;
  phoneRequired: boolean;
  minPasswordLength: number;
}
export interface RegisterRequest {
  organizationName: string;
  email: string;
  password: string;
  phoneNumber?: string;
  inviteCode?: string;
}
export interface RegisterResult { organizationId: string; slug: string; workspaceId: string; userId: string; next: string; }

export const authApi = {
  login: async (email: string, password: string): Promise<LoginResult | OtpPendingResult> => {
    const res = await fetch(`${API}/v1/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password, deviceName: "web" }),
    });
    if (res.status === 202) return res.json() as Promise<OtpPendingResult>;
    if (!res.ok) throw await toApiError(res, "/v1/auth/login");
    return res.json() as Promise<LoginResult>;
  },
  verifyOtp: async (pendingToken: string, otp: string): Promise<LoginResult> => {
    const res = await fetch(`${API}/v1/auth/verify-otp`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pendingToken, otp }),
    });
    if (!res.ok) throw await toApiError(res, "/v1/auth/verify-otp");
    return res.json() as Promise<LoginResult>;
  },
  // Self-service organization signup. Availability tells the form which fields
  // the gateway requires; `register` rejects with an ApiError carrying the
  // server code (email_taken, weak_password, invalid_invite, phone_required...).
  registerAvailability: () => publicFetch<RegisterAvailability>("/v1/auth/register"),
  register: (body: RegisterRequest) =>
    publicFetch<RegisterResult>("/v1/auth/register", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
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
export interface UserRow {
  id: string;
  email: string;
  phoneNumber?: string;
  phoneVerified: boolean;
  isActive: boolean;
  createdAt: string;
  role: string;
  workspaceId?: string;
  /** Prepaid token allowance; null = unlimited (rolling windows still apply). */
  tokenBalance: number | null;
  usage?: UserUsage;
}
export interface WorkspaceRow { id: string; name: string; isActive: boolean; memberCount: number; }

// Org-owned provider credentials (the organization's Claude connection).
export type ProviderCredentialKind = "api_key" | "oauth";
export interface ProviderCredentialRow {
  id: string;
  organizationId: string | null;
  provider: string;
  kind: ProviderCredentialKind;
  hint: string;
  isActive: boolean;
  accessExpiresAt: string | null;
  lastRefreshedAt: string | null;
  lastError: string | null;
  createdAt: string;
}
export interface ProviderCredentialsResult {
  credentials: ProviderCredentialRow[];
  oauth: { enabled: boolean; provider: string };
}
export interface OAuthStartResult { authorizeUrl: string; expiresInSeconds: number; }
export interface OAuthFinishResult { id: string; expiresAt: string | null; }
export interface CredentialTestResult { ok: boolean; message: string; }

// Prepaid token balance + ledger.
export type LedgerKind = "grant" | "set" | "usage" | "revoke" | "allowance";
export interface LedgerEntry {
  id: number;
  kind: LedgerKind;
  delta: number;
  balanceAfter: number | null;
  actorUserId: string | null;
  reference: string | null;
  createdAt: string;
}
/** Recurring top-up. Null on BalanceResult when the user has no allowance. */
export interface Allowance {
  tokens: number;
  rollover: boolean;
  period: "monthly";
  /** Last period credited, "YYYY-MM"; null before the first credit. */
  lastCreditedPeriod: string | null;
}
export interface BalanceResult {
  userId: string;
  workspaceId: string;
  membershipId: string;
  enforced: boolean;
  balance: number | null;
  allowance: Allowance | null;
  history: LedgerEntry[];
}

// Per-user quota override and the workspace policy it falls back to.
export interface QuotaOverride { userWindows?: unknown[]; allowedModels?: string[]; }
export interface EffectiveQuota {
  allowedModels: string[];
  userWindows: unknown[];
  workspaceWindows: unknown[];
  requestsPerMinute: number;
}
export interface UserQuotaResult {
  userId: string;
  workspaceId: string;
  hasOverride: boolean;
  override: QuotaOverride | null;
  effective: EffectiveQuota;
}
export interface WorkspacePolicy {
  allowedModels: string[];
  userWindows: unknown[];
  workspaceWindows: unknown[];
  requestsPerMinute: number;
}
export interface WorkspacePolicyResult {
  workspaceId: string;
  isDefault?: boolean;
  policy?: WorkspacePolicy;
  /** Some gateway versions return the policy fields at the top level. */
  allowedModels?: string[];
}

// Personal `apus_...` keys a user minted for their IDE clients.
export interface PersonalKeyRow {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  revokedAt: string | null;
}

/** Returned once, at creation: `key` is never retrievable again. */
export interface CreatedPersonalKey extends PersonalKeyRow {
  key: string;
}

/** Self-service personal keys for the /v1 proxy. Minting requires a JWT session. */
export const meApi = {
  listKeys: () => get<{ keys: PersonalKeyRow[] }>("/v1/me/keys"),
  createKey: (name: string) => post<CreatedPersonalKey>("/v1/me/keys", { name }),
  revokeKey: (id: string) => del(`/v1/me/keys/${id}`),
  models: () => get<{ models: string[] }>("/v1/me/models"),
};

export const adminApi = {
  // Super-admin: cross-tenant rollup (ClickHouse-backed).
  organizations: () => get<{ orgs: OrgRow[]; totals: { orgs: number; employees: number; tokensToday: number; costToday: number } }>("/v1/admin/organizations"),
  anomalies: () => get<{ anomalies: Anomaly[] }>("/v1/admin/anomalies"),
  // Org-admin: per-employee tracking within a workspace.
  topConsumers: (workspaceId: string) => get<{ consumers: ConsumerRow[]; window: WindowState }>(`/v1/admin/workspaces/${workspaceId}/top-consumers`),
  // User: own usage.
  myUsage: () => get<{ windows: WindowState[]; balance?: { enforced: boolean; remaining: number | null } }>("/v1/me/usage"),
  // Super-admin: platform-wide fallback provider API keys.
  listProviderKeys: () => get<{ keys: ProviderKeyRow[] }>("/v1/admin/provider-keys"),
  addProviderKey: (provider: string, apiKey: string) =>
    post<{ id: string }>("/v1/admin/provider-keys", { provider, apiKey }),
  removeProviderKey: (id: string) => del(`/v1/admin/provider-keys/${id}`),

  // Org-admin: the organization's own provider credential (API key or OAuth).
  listProviderCredentials: () => get<ProviderCredentialsResult>("/v1/admin/provider-credentials"),
  addProviderApiKey: (provider: string, apiKey: string) =>
    post<{ id: string }>("/v1/admin/provider-credentials/api-key", { provider, apiKey }),
  startProviderOAuth: () =>
    post<OAuthStartResult>("/v1/admin/provider-credentials/oauth/start", { provider: "anthropic" }),
  finishProviderOAuth: (code: string, state: string) =>
    post<OAuthFinishResult>("/v1/admin/provider-credentials/oauth/callback", { code, state }),
  testProviderCredential: (id: string) =>
    post<CredentialTestResult>(`/v1/admin/provider-credentials/${id}/test`, {}),
  removeProviderCredential: (id: string) => del(`/v1/admin/provider-credentials/${id}`),

  // Org-admin: user management.
  listUsers: () => get<{ users: UserRow[] }>("/v1/admin/users"),
  createUser: (body: { email: string; password: string; phoneNumber?: string; workspaceId: string; role: string }) =>
    post<{ userId: string; email: string }>("/v1/admin/users", body),
  updateUser: (id: string, body: { isActive?: boolean; role?: string; phoneNumber?: string }) =>
    patch<{ updated: boolean }>(`/v1/admin/users/${id}`, body),
  revokeUserSessions: (id: string) =>
    post<{ sessionsRevoked: number }>(`/v1/admin/users/${id}/revoke-sessions`, {}),
  getUserActivity: (id: string) =>
    get<{ activity: unknown[] }>(`/v1/admin/users/${id}/activity`),

  // Org-admin: prepaid token balance per user (null = unlimited).
  getUserBalance: (id: string, workspaceId?: string) =>
    get<BalanceResult>(`/v1/admin/users/${id}/balance${wsQuery(workspaceId)}`),
  grantTokens: (id: string, body: { tokens: number; note?: string; idempotencyKey?: string }, workspaceId?: string) =>
    post<BalanceResult>(`/v1/admin/users/${id}/balance/grant${wsQuery(workspaceId)}`, body),
  setTokens: (id: string, body: { tokens: number; note?: string }, workspaceId?: string) =>
    put<BalanceResult>(`/v1/admin/users/${id}/balance${wsQuery(workspaceId)}`, body),
  revokeBalance: (id: string, workspaceId?: string) =>
    delJson<BalanceResult>(`/v1/admin/users/${id}/balance${wsQuery(workspaceId)}`),
  // Recurring monthly allowance: credits the current period immediately, then renews.
  setAllowance: (id: string, body: { tokens: number; rollover: boolean; note?: string }, workspaceId?: string) =>
    put<BalanceResult>(`/v1/admin/users/${id}/balance/allowance${wsQuery(workspaceId)}`, body),
  clearAllowance: (id: string, workspaceId?: string) =>
    delJson<BalanceResult>(`/v1/admin/users/${id}/balance/allowance${wsQuery(workspaceId)}`),

  // Org-admin: per-user model allowlist (override of the workspace policy).
  getUserQuota: (id: string, workspaceId?: string) =>
    get<UserQuotaResult>(`/v1/admin/users/${id}/quota${wsQuery(workspaceId)}`),
  setUserModels: (id: string, allowedModels: string[], workspaceId?: string) =>
    put<{ userId: string; workspaceId: string; override: QuotaOverride }>(`/v1/admin/users/${id}/quota${wsQuery(workspaceId)}`, { allowedModels }),
  clearUserQuota: (id: string, workspaceId?: string) =>
    del(`/v1/admin/users/${id}/quota${wsQuery(workspaceId)}`),
  getWorkspacePolicy: (id: string) =>
    get<WorkspacePolicyResult>(`/v1/admin/workspaces/${id}/policy`),

  // Org-admin: a user's personal gateway keys.
  listUserKeys: (id: string) => get<{ keys: PersonalKeyRow[] }>(`/v1/admin/users/${id}/keys`),
  revokeUserKey: (id: string, keyId: string) => del(`/v1/admin/users/${id}/keys/${keyId}`),

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


// ---------------------------------------------------------------------------
// Multi-provider connections, currency allowances and the AI dashboards.
// Money always crosses the wire as an integer count of minor units (paise,
// cents) plus its currency — never as a float.
// ---------------------------------------------------------------------------

export type ConnectionType = "api_key" | "oauth" | "aws_bedrock" | "google_vertex";

export type ConnectionStatus =
  | "disconnected" | "connecting" | "connected" | "refreshing"
  | "expired" | "reauthentication_required" | "revoked" | "disabled" | "error";

export interface ProviderConnection {
  id: string;
  organizationId: string | null;
  provider: string;
  providerDisplayName: string;
  connectionType: ConnectionType;
  status: ConnectionStatus;
  connectionPurpose: string;
  displayName: string | null;
  /** Last four characters, an access key id or a service-account email — never the secret. */
  hint: string;
  providerAccountId: string | null;
  providerOrganizationId: string | null;
  config: Record<string, string>;
  scopes: string | null;
  encryptionKeyVersion: number;
  connectedByUserId: string | null;
  connectedAt: string;
  accessExpiresAt: string | null;
  lastValidatedAt: string | null;
  lastRefreshedAt: string | null;
  revokedAt: string | null;
  failureCount: number;
  lastFailureAt: string | null;
  lastFailureReason: string | null;
}

export interface ProviderDescriptor {
  id: string;
  displayName: string;
  connectionTypes: ConnectionType[];
  oauthAvailable: boolean;
  requiredConfig: string[];
  docsUrl: string;
}

export interface ProviderConnectionsResult {
  connections: ProviderConnection[];
  /** Legacy alias kept so an older dashboard build keeps rendering. */
  credentials: ProviderConnection[];
  providers: ProviderDescriptor[];
  oauth: { enabled: boolean; provider: string };
  features: Record<string, boolean>;
}

export interface ConnectProviderBody {
  connectionType: ConnectionType;
  apiKey?: string;
  accessKeyId?: string;
  secretAccessKey?: string;
  sessionToken?: string;
  region?: string;
  serviceAccountJson?: string;
  project?: string;
  location?: string;
  displayName?: string;
}

export interface ConnectResult {
  id: string;
  status: ConnectionStatus;
  hint: string;
  validated: boolean;
  message: string;
  connection: ProviderConnection | null;
}

export interface ConnectionTestResult {
  ok: boolean;
  message: string;
  connection: ProviderConnection | null;
}

export interface ProviderUsageRow {
  provider: string;
  displayName: string;
  connection: ProviderConnection | null;
  today: { requests: number; tokens: number; costMinor: number };
  month: {
    requests: number; tokens: number;
    customerCostMinor: number; providerCostMinor: number;
    failures: number; rateLimited: number;
  };
}

export interface ProviderUsageResult { currency: string; providers: ProviderUsageRow[] }

export interface TeamMemberRow {
  userId: string;
  email: string;
  workspaceId: string;
  membershipId: string;
  role: string;
  isActive: boolean;
  aiStatus: "active" | "suspended" | "disabled";
  accessExpiresAt: string | null;
  currency: string;
  unlimited: boolean;
  monthlyAllowanceMinor: number | null;
  dailyAllowanceMinor: number | null;
  budgetMinor: number;
  consumedMinor: number;
  reservedMinor: number;
  remainingMinor: number | null;
  requests: number;
  tokens: number;
  tokenBalance: number | null;
  limits: { rpm: number | null; tpm: number | null; concurrency: number | null; dailyRequests: number | null };
  allowedModels: string[];
  allowedProviders: string[];
}

export interface TeamResult {
  currency: string;
  period: { start: string; end: string };
  organization: { monthlyBudgetMinor: number | null; unlimited: boolean; allocatedToMembersMinor: number };
  users: TeamMemberRow[];
}

export interface AiOverviewResult {
  currency: string;
  period: { start: string; end: string };
  budget: {
    unlimited: boolean;
    budgetMinor: number;
    consumedMinor: number;
    reservedMinor: number;
    remainingMinor: number | null;
  };
  totals: {
    requests: number; tokens: number;
    customerCostMinor: number; providerCostMinor: number; marginMinor: number;
    avgLatencyMs: number; failures: number; blocked: number;
  };
  providers: { provider: string; customerCostMinor: number; providerCostMinor: number; requests: number; tokens: number }[];
  models: { provider: string; model: string; customerCostMinor: number; requests: number; tokens: number }[];
  users: { userId: string; email: string; customerCostMinor: number; requests: number; tokens: number }[];
}

export interface AiTrendResult {
  currency: string;
  days: number;
  daily: { date: string; requests: number; tokens: number; customerCostMinor: number; providerCostMinor: number }[];
}

export interface AiSettings {
  currency: string;
  monthlyBudgetMinor: number | null;
  unlimitedBudget: boolean;
  markupBps: number;
  aiEnabled: boolean;
  allowedProviders: string[];
  allowedModels: string[];
  features: Record<string, boolean>;
}

export interface MyAiResult {
  currency: string;
  allowance: {
    unlimited: boolean;
    budgetMinor: number;
    usedMinor: number;
    reservedMinor: number;
    remainingMinor: number | null;
    periodStart: string;
    resetsAt: string;
  };
  usage: { requests: number; tokens: number };
  tokenBalance: { enforced: boolean; remaining: number | null };
  access: { status: string; expiresAt: string | null; organizationAiEnabled: boolean };
  limits: { rpm: number | null; tpm: number | null; concurrency: number | null; dailyRequests: number | null };
  models: { id: string; providers: string[] }[];
}

export const providerApi = {
  list: () => get<ProviderConnectionsResult>("/v1/provider-connections"),
  connect: (provider: string, body: ConnectProviderBody) =>
    post<ConnectResult>(`/v1/provider-connections/${provider}/connect`, body),
  startOAuth: (provider: string) =>
    post<OAuthStartResult>(`/v1/provider-connections/${provider}/oauth/start`, {}),
  finishOAuth: (provider: string, code: string, state: string) =>
    post<OAuthFinishResult>(`/v1/provider-connections/${provider}/oauth/callback`, { code, state }),
  validate: (id: string) => post<ConnectionTestResult>(`/v1/provider-connections/${id}/validate`, {}),
  disconnect: (id: string) => post<{ id: string; status: string }>(`/v1/provider-connections/${id}/disconnect`, {}),
  setEnabled: (id: string, enabled: boolean) =>
    post<{ id: string; status: string }>(`/v1/provider-connections/${id}/${enabled ? "enable" : "disable"}`, {}),
};

export const aiAdminApi = {
  overview: () => get<AiOverviewResult>("/v1/admin/ai/overview"),
  trend: (days = 30) => get<AiTrendResult>(`/v1/admin/ai/usage?days=${days}`),
  providers: () => get<ProviderUsageResult>("/v1/admin/ai/providers"),
  settings: () => get<AiSettings>("/v1/admin/ai/settings"),
  updateSettings: (body: Partial<{
    currency: string; monthlyBudgetMinor: number | null; unlimitedBudget: boolean;
    markupBps: number; aiEnabled: boolean; allowedProviders: string[]; allowedModels: string[];
  }>) => put<AiSettings>("/v1/admin/ai/settings", body),

  team: () => get<TeamResult>("/v1/admin/ai/users"),
  setAllowance: (userId: string, body: { monthlyMinor: number | null; dailyMinor?: number | null; unlimited?: boolean; note?: string }, workspaceId?: string) =>
    put<unknown>(`/v1/admin/ai/users/${userId}/allowance${wsQuery(workspaceId)}`, body),
  topUp: (userId: string, amountMinor: number, note?: string, workspaceId?: string) =>
    post<unknown>(`/v1/admin/ai/users/${userId}/allowance/top-up${wsQuery(workspaceId)}`, { amountMinor, note }),
  resetAllowance: (userId: string, workspaceId?: string) =>
    post<unknown>(`/v1/admin/ai/users/${userId}/allowance/reset${wsQuery(workspaceId)}`, {}),
  setLimits: (userId: string, body: { rpm?: number | null; tpm?: number | null; concurrency?: number | null; dailyRequests?: number | null }, workspaceId?: string) =>
    put<unknown>(`/v1/admin/ai/users/${userId}/limits${wsQuery(workspaceId)}`, body),
  setAccess: (userId: string, body: { allowedModels?: string[]; allowedProviders?: string[] }, workspaceId?: string) =>
    put<unknown>(`/v1/admin/ai/users/${userId}/models${wsQuery(workspaceId)}`, body),
  suspend: (userId: string, workspaceId?: string) =>
    post<unknown>(`/v1/admin/ai/users/${userId}/suspend${wsQuery(workspaceId)}`, {}),
  reactivate: (userId: string, workspaceId?: string) =>
    post<unknown>(`/v1/admin/ai/users/${userId}/reactivate${wsQuery(workspaceId)}`, {}),
};

export const myAiApi = {
  summary: () => get<MyAiResult>("/v1/me/ai"),
};

const CURRENCY_SYMBOLS: Record<string, string> = { USD: "$", INR: "\u20b9", EUR: "\u20ac", GBP: "\u00a3", JPY: "\u00a5" };
const ZERO_DECIMAL = new Set(["JPY", "KRW"]);

/** Formats an integer count of minor units as the currency a human reads. */
export function money(minor: number | null | undefined, currency = "USD"): string {
  if (minor === null || minor === undefined) return "\u2014";
  const code = currency.toUpperCase();
  const exponent = ZERO_DECIMAL.has(code) ? 0 : 2;
  const symbol = CURRENCY_SYMBOLS[code] ?? `${code} `;
  const value = minor / 10 ** exponent;
  return symbol + value.toLocaleString(undefined, { minimumFractionDigits: exponent, maximumFractionDigits: exponent });
}

/** Parses a human-typed amount into minor units. Returns null for anything unusable. */
export function toMinor(input: string, currency = "USD"): number | null {
  const trimmed = input.trim().replace(/,/g, "");
  if (!trimmed || !/^\d*\.?\d*$/.test(trimmed)) return null;
  const exponent = ZERO_DECIMAL.has(currency.toUpperCase()) ? 0 : 2;
  const value = Number(trimmed);
  return Number.isFinite(value) ? Math.round(value * 10 ** exponent) : null;
}

/** Minor units back to a plain editable string ("100000" -> "1000"). */
export function fromMinor(minor: number | null | undefined, currency = "USD"): string {
  if (minor === null || minor === undefined) return "";
  const exponent = ZERO_DECIMAL.has(currency.toUpperCase()) ? 0 : 2;
  return String(minor / 10 ** exponent);
}

/** "in 12m" / "in 3h" / "in 2d" / "expired" for an ISO timestamp. */
export function relativeTime(iso: string | null): string {
  if (!iso) return "never";
  const ms = new Date(iso).getTime() - Date.now();
  if (Number.isNaN(ms)) return "unknown";
  const past = ms < 0;
  const mins = Math.round(Math.abs(ms) / 60_000);
  const label = mins < 60 ? `${Math.max(1, mins)}m`
    : mins < 2880 ? `${Math.round(mins / 60)}h`
    : `${Math.round(mins / 1440)}d`;
  return past ? `${label} ago` : `in ${label}`;
}

export function fmt(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`;
  return n.toString();
}

/** Human-readable message for a thrown value; prefers the gateway's own text. */
export function errorMessage(err: unknown, fallback: string): string {
  if (err instanceof ApiError) return err.message || fallback;
  if (err instanceof Error && err.message) return err.message;
  return fallback;
}

// Poll an async loader every `ms` for "realtime" dashboards without websockets.
// Stops on errors that a retry cannot fix (signed out, forbidden, endpoint missing)
// and pauses while the tab is hidden, so an idle dashboard is not a request storm.
// Swap for a Server-Sent Events subscription in production for sub-second updates.
import { useEffect, useState } from "react";
export function usePolling<T>(loader: () => Promise<T>, ms = 5000): T | null {
  const [data, setData] = useState<T | null>(null);
  useEffect(() => {
    let alive = true;
    let id: ReturnType<typeof setInterval> | null = null;
    const stop = () => { if (id !== null) { clearInterval(id); id = null; } };
    const tick = () => {
      if (typeof document !== "undefined" && document.hidden) return;
      loader().then(d => { if (alive) setData(d); }).catch((err: unknown) => {
        if (err instanceof SessionExpiredError) { stop(); return; }
        if (err instanceof ApiError && (err.status === 401 || err.status === 403 || err.status === 404)) {
          console.warn(`[polling] ${err.status} ${err.code} — stopped`);
          stop();
        }
        // Network blips and 5xx: keep polling; the next tick may succeed.
      });
    };
    tick();
    id = setInterval(tick, ms);
    return () => { alive = false; stop(); };
  }, [loader, ms]);
  return data;
}
