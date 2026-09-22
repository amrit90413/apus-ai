import { request } from "./http.js";

export interface Tokens { accessToken: string; refreshToken: string; accessExpiresAt: string }

export type LoginResult =
  | { kind: "ok"; tokens: Tokens }
  | { kind: "otp_required"; pendingToken: string; message: string }
  | { kind: "invalid_credentials" }
  | { kind: "rate_limited"; retryAfterSeconds: number | null }
  | { kind: "error"; status: number; message: string };

export async function login(apiBase: string, email: string, password: string, deviceName: string): Promise<LoginResult> {
  const res = await request<any>(`${apiBase}/api/v1/auth/login`, {
    method: "POST",
    body: { email, password, deviceName },
  });
  if (res.status === 202 && res.data?.status === "otp_required") {
    return { kind: "otp_required", pendingToken: res.data.pendingToken, message: res.data.message ?? "" };
  }
  if (res.ok && res.data?.accessToken) return { kind: "ok", tokens: res.data as Tokens };
  if (res.status === 401) return { kind: "invalid_credentials" };
  if (res.status === 429) {
    const ra = Number(res.headers.get("retry-after"));
    return { kind: "rate_limited", retryAfterSeconds: Number.isFinite(ra) && ra > 0 ? ra : null };
  }
  return { kind: "error", status: res.status, message: res.error?.message ?? `HTTP ${res.status}` };
}

export type OtpResult =
  | { kind: "ok"; tokens: Tokens }
  | { kind: "invalid_otp"; message: string }
  | { kind: "error"; status: number; message: string };

export async function verifyOtp(apiBase: string, pendingToken: string, otp: string): Promise<OtpResult> {
  const res = await request<any>(`${apiBase}/api/v1/auth/verify-otp`, {
    method: "POST",
    body: { pendingToken, otp },
  });
  if (res.ok && res.data?.accessToken) return { kind: "ok", tokens: res.data as Tokens };
  if (res.status === 401 || res.status === 400) {
    return { kind: "invalid_otp", message: res.error?.message ?? "OTP is incorrect or has expired." };
  }
  return { kind: "error", status: res.status, message: res.error?.message ?? `HTTP ${res.status}` };
}
