"use client";
import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { authApi, ApiError } from "@/lib/api";
import type { RegisterAvailability } from "@/lib/api";

const inputClass =
  "w-full rounded-lg border border-neutral-200 px-3 py-2 text-sm outline-none focus:border-neutral-400 dark:border-neutral-700 dark:bg-neutral-800";
const labelClass = "mb-1 block text-sm text-neutral-600 dark:text-neutral-400";

// Server validation codes → what the admin should do about them.
function describeError(err: unknown, minPasswordLength: number): string {
  if (!(err instanceof ApiError)) return "Could not reach the gateway. Try again.";
  switch (err.code) {
    case "email_taken": return "An account with this email already exists. Sign in instead.";
    case "weak_password": return `Password must be at least ${minPasswordLength} characters.`;
    case "invalid_invite": return "That invite code is not valid.";
    case "phone_required": return "A phone number is required on this gateway.";
    case "invalid_phone": return err.message || "Enter the full number with country code (e.g. 919876543210).";
    case "invalid_email": return "Enter a valid email address.";
    case "invalid_organization": return "Enter a valid organization name.";
    case "registration_disabled": return "Self-service signup is disabled — ask your platform administrator.";
    default: return err.message || "Registration failed. Try again.";
  }
}

export default function RegisterPage() {
  const router = useRouter();
  const [availability, setAvailability] = useState<RegisterAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);

  const [organizationName, setOrganizationName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [phone, setPhone] = useState("");
  const [inviteCode, setInviteCode] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    let alive = true;
    authApi.registerAvailability()
      .then(a => { if (alive) setAvailability(a); })
      .catch(() => { if (alive) setAvailabilityError("Could not reach the gateway. Try again later."); });
    return () => { alive = false; };
  }, []);

  useEffect(() => {
    if (!done) return;
    const id = setTimeout(() => router.push("/login"), 1500);
    return () => clearTimeout(id);
  }, [done, router]);

  const minPasswordLength = availability?.minPasswordLength ?? 8;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (password !== confirm) { setError("Passwords do not match."); return; }
    if (password.length < minPasswordLength) { setError(`Password must be at least ${minPasswordLength} characters.`); return; }
    setLoading(true);
    try {
      await authApi.register({
        organizationName: organizationName.trim(),
        email: email.trim(),
        password,
        phoneNumber: availability?.phoneRequired ? phone.trim() : undefined,
        inviteCode: availability?.inviteCodeRequired ? inviteCode.trim() : undefined,
      });
      setDone(true);
    } catch (err) {
      setError(describeError(err, minPasswordLength));
    } finally {
      setLoading(false);
    }
  };

  let body: React.ReactNode;
  if (availabilityError) {
    body = <p className="text-sm text-red-600">{availabilityError}</p>;
  } else if (!availability) {
    body = <p className="text-sm text-neutral-400">Checking signup availability…</p>;
  } else if (!availability.enabled) {
    body = (
      <div className="space-y-4">
        <p className="rounded-lg bg-neutral-50 px-4 py-3 text-sm text-neutral-600 dark:bg-neutral-800 dark:text-neutral-300">
          Self-service signup is disabled — ask your platform administrator.
        </p>
        <Link href="/login" className="block text-center text-sm text-neutral-500 hover:underline">Back to sign in</Link>
      </div>
    );
  } else if (done) {
    body = (
      <div className="space-y-4">
        <div className="rounded-lg bg-green-50 px-4 py-3 text-sm text-green-700 dark:bg-green-950/40 dark:text-green-400" role="status">
          Organization created — sign in
        </div>
        <Link href="/login" className="block text-center text-sm text-neutral-500 hover:underline">Go to sign in</Link>
      </div>
    );
  } else {
    body = (
      <form onSubmit={handleSubmit} className="space-y-4">
        <div>
          <label htmlFor="reg-org" className={labelClass}>Organization name</label>
          <input id="reg-org" type="text" required autoComplete="organization" value={organizationName}
            onChange={e => setOrganizationName(e.target.value)} className={inputClass} placeholder="Acme Inc" />
        </div>
        <div>
          <label htmlFor="reg-email" className={labelClass}>Admin email</label>
          <input id="reg-email" type="email" required autoComplete="email" value={email}
            onChange={e => setEmail(e.target.value)} className={inputClass} placeholder="you@company.com" />
        </div>
        <div>
          <label htmlFor="reg-password" className={labelClass}>Password</label>
          <input id="reg-password" type="password" required autoComplete="new-password" minLength={minPasswordLength}
            value={password} onChange={e => setPassword(e.target.value)} className={inputClass} placeholder="••••••••" />
          <p className="mt-1 text-xs text-neutral-400">At least {minPasswordLength} characters.</p>
        </div>
        <div>
          <label htmlFor="reg-confirm" className={labelClass}>Confirm password</label>
          <input id="reg-confirm" type="password" required autoComplete="new-password" value={confirm}
            onChange={e => setConfirm(e.target.value)} className={inputClass} placeholder="••••••••" />
        </div>
        {availability.phoneRequired && (
          <div>
            <label htmlFor="reg-phone" className={labelClass}>WhatsApp number</label>
            <input id="reg-phone" type="tel" required autoComplete="tel" inputMode="numeric" value={phone}
              onChange={e => setPhone(e.target.value)} className={inputClass} placeholder="919876543210"
              aria-describedby="reg-phone-hint" />
            <p id="reg-phone-hint" className="mt-1 text-xs text-neutral-400">
              Country code first, digits only — e.g. <span className="font-mono">91</span>9876543210 for India. The login code is sent here on WhatsApp.
            </p>
            <p className="mt-1 text-xs text-neutral-400">Admin sign-in on this gateway uses a WhatsApp OTP.</p>
          </div>
        )}
        {availability.inviteCodeRequired && (
          <div>
            <label htmlFor="reg-invite" className={labelClass}>Invite code</label>
            <input id="reg-invite" type="text" required autoComplete="off" value={inviteCode}
              onChange={e => setInviteCode(e.target.value)} className={inputClass} placeholder="Provided by your platform administrator" />
          </div>
        )}
        {error && <p className="text-sm text-red-600" role="alert">{error}</p>}
        <button
          type="submit"
          disabled={loading}
          className="w-full rounded-lg bg-neutral-900 py-2 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900"
        >
          {loading ? "Creating organization…" : "Create organization"}
        </button>
        <p className="text-center text-xs text-neutral-400">
          Already have an account? <Link href="/login" className="hover:underline">Sign in</Link>
        </p>
      </form>
    );
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-neutral-50 dark:bg-neutral-950">
      <div className="w-full max-w-sm rounded-xl border border-neutral-100 bg-white p-8 shadow-sm dark:border-neutral-800 dark:bg-neutral-900">
        <h1 className="mb-1 text-lg font-medium">YourCompany AI</h1>
        <p className="mb-6 text-sm text-neutral-500">Create an organization</p>
        {body}
      </div>
    </div>
  );
}
