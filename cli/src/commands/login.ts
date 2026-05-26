import pc from "picocolors";
import prompts from "prompts";
import { hostname } from "node:os";
import { saveCredentials, clearCredentials } from "../lib/credentials.js";

export async function login(apiBase: string): Promise<void> {
  const { email } = await prompts({ type: "text", name: "email", message: "Work email" });
  const { password } = await prompts({ type: "password", name: "password", message: "Password" });

  const res = await fetch(`${apiBase}/api/v1/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    // Device name + fingerprint enable multi-device sessions + anomaly detection.
    body: JSON.stringify({ email, password, deviceName: hostname() }),
  });

  if (!res.ok) { console.error(pc.red("Login failed. Check your credentials.")); process.exit(1); }
  const data = await res.json() as { accessToken: string; refreshToken: string; accessExpiresAt: string };
  await saveCredentials({ apiBase, ...data });
  console.log(pc.green("Logged in. Token stored in your OS keychain."));
}

export async function logout(): Promise<void> {
  await clearCredentials();
  console.log(pc.green("Logged out. Local credentials cleared."));
}
