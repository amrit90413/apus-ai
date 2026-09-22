"use client";
import { Suspense, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { adminApi, errorMessage } from "@/lib/api";

type State =
  | { status: "working" }
  | { status: "ok"; expiresAt: string | null }
  | { status: "error"; message: string };

// The provider redirects here with ?code=&state= (or ?error=&error_description=
// when the admin declined). We hand the code to the gateway, which holds the
// PKCE verifier, and it stores the resulting tokens for the organization.
function CallbackInner() {
  const params = useSearchParams();
  const code = params.get("code");
  const state = params.get("state");
  const oauthError = params.get("error");
  const oauthErrorDescription = params.get("error_description");

  const [result, setResult] = useState<State>({ status: "working" });
  // The code is single-use; make sure React StrictMode's double effect doesn't spend it twice.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;

    if (oauthError) {
      setResult({ status: "error", message: oauthErrorDescription ? `${oauthError}: ${oauthErrorDescription}` : oauthError });
      return;
    }
    if (!code || !state) {
      setResult({ status: "error", message: "Missing code or state in the callback URL. Start the login again." });
      return;
    }
    adminApi.finishProviderOAuth(code, state)
      .then(r => setResult({ status: "ok", expiresAt: r.expiresAt ?? null }))
      .catch(err => setResult({ status: "error", message: errorMessage(err, "The gateway could not complete the login.") }));
  }, [code, state, oauthError, oauthErrorDescription]);

  return (
    <div className="w-full max-w-sm rounded-xl border border-neutral-100 bg-white p-8 shadow-sm dark:border-neutral-800 dark:bg-neutral-900">
      <h1 className="mb-1 text-lg font-medium">Connecting Claude</h1>
      <p className="mb-6 text-sm text-neutral-500">Finishing the browser login for your organization</p>

      {result.status === "working" && (
        <p className="text-sm text-neutral-400" role="status">Exchanging the login code with the gateway…</p>
      )}
      {result.status === "ok" && (
        <div className="rounded-lg bg-green-50 px-4 py-3 text-sm text-green-700 dark:bg-green-950/40 dark:text-green-400" role="status">
          Connected. Your organization&apos;s requests now go through this login.
          {result.expiresAt && (
            <p className="mt-1 text-xs opacity-80">Access token valid until {new Date(result.expiresAt).toLocaleString()}; the gateway refreshes it automatically.</p>
          )}
        </div>
      )}
      {result.status === "error" && (
        <div className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700 dark:bg-red-950/40 dark:text-red-400" role="alert">
          {result.message}
        </div>
      )}

      {result.status !== "working" && (
        <Link
          href="/admin/providers"
          className="mt-6 block w-full rounded-lg bg-neutral-900 py-2 text-center text-sm text-white dark:bg-white dark:text-neutral-900"
        >
          Back to provider credentials
        </Link>
      )}
    </div>
  );
}

export default function ProviderOAuthCallbackPage() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-neutral-50 dark:bg-neutral-950">
      {/* useSearchParams needs a Suspense boundary so the static shell can prerender. */}
      <Suspense fallback={<p className="text-sm text-neutral-400">Loading…</p>}>
        <CallbackInner />
      </Suspense>
    </div>
  );
}
