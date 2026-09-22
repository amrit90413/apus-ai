// Thin fetch wrapper for the gateway's JSON APIs. Never throws on HTTP errors —
// callers branch on `status`. Throws `NetworkError` only when the host can't be reached.

export interface ApiError { code: string; message: string }

export interface ApiResponse<T> {
  status: number;
  ok: boolean;
  data: T | null;          // parsed body on 2xx (null for 204 / non-JSON)
  error: ApiError | null;  // parsed `{ error: { code, message } }` on non-2xx, best effort
  headers: Headers;
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  token?: string;   // JWT → Authorization: Bearer
  apiKey?: string;  // personal key → x-api-key
  timeoutMs?: number;
}

export class NetworkError extends Error {
  constructor(public readonly url: string, cause: unknown) {
    super(`Could not reach ${url}: ${describeCause(cause)}`);
    this.name = "NetworkError";
  }
}

function describeCause(err: unknown): string {
  if (err instanceof Error) {
    if (err.name === "AbortError" || err.name === "TimeoutError") return "timed out";
    const cause = (err as { cause?: { code?: string; message?: string } }).cause;
    return cause?.code ?? cause?.message ?? err.message;
  }
  return String(err);
}

export async function request<T>(url: string, opts: RequestOptions = {}): Promise<ApiResponse<T>> {
  const headers: Record<string, string> = { accept: "application/json" };
  if (opts.body !== undefined) headers["content-type"] = "application/json";
  if (opts.token) headers.authorization = `Bearer ${opts.token}`;
  if (opts.apiKey) headers["x-api-key"] = opts.apiKey;

  let res: Response;
  try {
    res = await fetch(url, {
      method: opts.method ?? "GET",
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
      signal: AbortSignal.timeout(opts.timeoutMs ?? 15_000),
    });
  } catch (err) {
    throw new NetworkError(url, err);
  }

  const text = await res.text();
  let json: any = null;
  if (text) { try { json = JSON.parse(text); } catch { /* non-JSON body */ } }

  if (res.ok) return { status: res.status, ok: true, data: json as T, error: null, headers: res.headers };

  // Dashboard API shape `{ error: { code, message } }`; Anthropic proxy shape
  // `{ type: "error", error: { type, message } }`. Normalise both.
  const e = json?.error;
  const error: ApiError = e && typeof e === "object"
    ? { code: e.code ?? e.type ?? `http_${res.status}`, message: e.message ?? res.statusText }
    : { code: `http_${res.status}`, message: (typeof json?.message === "string" ? json.message : text || res.statusText) };
  return { status: res.status, ok: false, data: null, error, headers: res.headers };
}

// Strip trailing slashes and whitespace; add https:// when the scheme is missing.
export function normalizeApiBase(raw: string): string {
  let url = raw.trim();
  if (!/^https?:\/\//i.test(url)) url = `https://${url}`;
  return url.replace(/\/+$/, "");
}
