"use client";
import { useState } from "react";
import { providerApi, errorMessage } from "@/lib/api";
import type { ConnectProviderBody, ConnectionType, ProviderDescriptor } from "@/lib/api";

const METHOD_LABELS: Record<ConnectionType, string> = {
  oauth: "Browser login (OAuth)",
  api_key: "API key",
  aws_bedrock: "AWS Bedrock credentials",
  google_vertex: "Google Vertex service account",
};

const KEY_PLACEHOLDERS: Record<string, string> = {
  anthropic: "sk-ant-…",
  openai: "sk-…",
  gemini: "AIza…",
};

/**
 * The connect panel for one provider.
 *
 * OAuth is offered only when the operator has configured a client the provider
 * issued to this organization; otherwise the supported credential method is the
 * way in. A personal consumer subscription is deliberately not an option — those
 * plans sign in to the provider's own apps, not third-party gateways.
 */
export default function ConnectForm({
  provider,
  reconnect,
  onDone,
  onCancel,
}: {
  provider: ProviderDescriptor;
  reconnect: boolean;
  onDone: (message: string) => void;
  onCancel: () => void;
}) {
  const methods = provider.connectionTypes.filter(t => t !== "oauth" || provider.oauthAvailable);
  const [method, setMethod] = useState<ConnectionType>(
    provider.oauthAvailable && provider.connectionTypes.includes("oauth") ? "oauth" : methods[0],
  );
  const [fields, setFields] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const set = (key: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
    setFields(prev => ({ ...prev, [key]: e.target.value }));

  const startOAuth = async () => {
    setBusy(true);
    setError(null);
    try {
      const { authorizeUrl } = await providerApi.startOAuth(provider.id);
      // Full navigation, not a popup: the provider sets its own cookies and some
      // block being framed.
      window.location.assign(authorizeUrl);
    } catch (err) {
      setError(errorMessage(err, "Could not start the browser login."));
      setBusy(false);
    }
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (method === "oauth") return startOAuth();

    setBusy(true);
    setError(null);
    try {
      const body: ConnectProviderBody = { connectionType: method };
      if (method === "api_key") body.apiKey = fields.apiKey?.trim();
      if (method === "aws_bedrock") {
        body.accessKeyId = fields.accessKeyId?.trim();
        body.secretAccessKey = fields.secretAccessKey?.trim();
        body.sessionToken = fields.sessionToken?.trim() || undefined;
        body.region = fields.region?.trim();
      }
      if (method === "google_vertex") {
        body.serviceAccountJson = fields.serviceAccountJson?.trim();
        body.project = fields.project?.trim() || undefined;
        body.location = fields.location?.trim();
      }

      const result = await providerApi.connect(provider.id, body);
      // The credential is gone from this component the moment it is posted; it is
      // never put in localStorage, a URL, or anywhere it could be read back.
      setFields({});
      onDone(result.validated
        ? `${provider.displayName} connected and verified.`
        : `${provider.displayName} saved, but the test call failed: ${result.message}`);
    } catch (err) {
      setError(errorMessage(err, "Could not connect. Check the details and try again."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} className="mt-4 rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
      <h3 className="text-sm font-medium">
        {reconnect ? "Reconnect" : "Connect"} {provider.displayName}
      </h3>
      <p className="mt-1 text-xs text-neutral-400">
        Your users never see this credential. They authenticate to APUS and their usage is metered
        against the connection you make here.
      </p>

      {methods.length > 1 && (
        <fieldset className="mt-4">
          <legend className="text-xs text-neutral-500">Authentication method</legend>
          <div className="mt-2 flex flex-wrap gap-4">
            {methods.map(m => (
              <label key={m} className="flex items-center gap-2 text-sm">
                <input
                  type="radio"
                  name={`method-${provider.id}`}
                  value={m}
                  checked={method === m}
                  onChange={() => { setMethod(m); setError(null); }}
                />
                {METHOD_LABELS[m]}
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {method === "oauth" && (
        <p className="mt-4 text-xs text-neutral-500">
          You will be sent to {provider.displayName} to sign in and approve APUS. When you come back,
          the access and refresh tokens are encrypted and stored server-side — they are never shown
          here or sent to a user.
        </p>
      )}

      {method === "api_key" && (
        <Field
          id={`${provider.id}-key`}
          label="API key"
          type="password"
          placeholder={KEY_PLACEHOLDERS[provider.id] ?? "…"}
          value={fields.apiKey ?? ""}
          onChange={set("apiKey")}
          hint={`Use a service-account key owned by your organization. ${provider.displayName} bills it directly.`}
        />
      )}

      {method === "aws_bedrock" && (
        <>
          <Field id="aws-key-id" label="Access key id" value={fields.accessKeyId ?? ""} onChange={set("accessKeyId")} placeholder="AKIA…" />
          <Field id="aws-secret" label="Secret access key" type="password" value={fields.secretAccessKey ?? ""} onChange={set("secretAccessKey")} />
          <Field id="aws-session" label="Session token (optional)" type="password" value={fields.sessionToken ?? ""} onChange={set("sessionToken")}
            hint="Only for temporary STS credentials." />
          <Field id="aws-region" label="Region" value={fields.region ?? ""} onChange={set("region")} placeholder="us-east-1" />
        </>
      )}

      {method === "google_vertex" && (
        <>
          <div className="mt-4">
            <label htmlFor="gcp-sa" className="block text-xs text-neutral-500">Service-account key (JSON)</label>
            <textarea
              id="gcp-sa"
              rows={5}
              spellCheck={false}
              value={fields.serviceAccountJson ?? ""}
              onChange={set("serviceAccountJson")}
              placeholder='{"type":"service_account","client_email":"…","private_key":"…"}'
              className="mt-1 w-full rounded border border-neutral-200 px-3 py-2 font-mono text-xs dark:border-neutral-700 dark:bg-neutral-900"
            />
            <p className="mt-1 text-xs text-neutral-400">
              Grant it the Vertex AI User role. APUS exchanges it for short-lived access tokens and
              never stores those.
            </p>
          </div>
          <Field id="gcp-project" label="Project id" value={fields.project ?? ""} onChange={set("project")}
            hint="Defaults to the project in the key file." />
          <Field id="gcp-location" label="Location" value={fields.location ?? ""} onChange={set("location")} placeholder="us-central1" />
        </>
      )}

      {error && <p className="mt-3 text-xs text-red-600" role="alert">{error}</p>}

      <div className="mt-4 flex gap-2">
        <button
          type="submit"
          disabled={busy}
          className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900"
        >
          {busy ? "Working…" : method === "oauth" ? `Continue to ${provider.displayName}` : "Validate & connect"}
        </button>
        <button
          type="button"
          onClick={onCancel}
          className="rounded-md border border-neutral-200 px-4 py-1.5 text-sm dark:border-neutral-700"
        >
          Cancel
        </button>
        <a
          href={provider.docsUrl}
          target="_blank"
          rel="noreferrer noopener"
          className="ml-auto self-center text-xs text-blue-500 hover:underline"
        >
          Where do I get this?
        </a>
      </div>
    </form>
  );
}

function Field({
  id, label, value, onChange, type = "text", placeholder, hint,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  type?: string;
  placeholder?: string;
  hint?: string;
}) {
  return (
    <div className="mt-4">
      <label htmlFor={id} className="block text-xs text-neutral-500">{label}</label>
      <input
        id={id}
        type={type}
        autoComplete="off"
        spellCheck={false}
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        className="mt-1 w-full rounded border border-neutral-200 px-3 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
      />
      {hint && <p className="mt-1 text-xs text-neutral-400">{hint}</p>}
    </div>
  );
}
