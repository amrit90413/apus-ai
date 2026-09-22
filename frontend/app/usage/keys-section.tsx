"use client";
import { useCallback, useEffect, useState } from "react";
import { meApi, errorMessage } from "@/lib/api";
import type { PersonalKeyRow, CreatedPersonalKey } from "@/lib/api";

/** The gateway origin this dashboard is served from — what clients point at. */
function gatewayOrigin(): string {
  if (typeof window === "undefined") return "https://your-gateway";
  return window.location.origin;
}

function claudeCodeSettings(origin: string, key: string, models: string[]): string {
  const env: Record<string, string> = {
    ANTHROPIC_AUTH_TOKEN: key,
    ANTHROPIC_BASE_URL: origin,
  };
  // Only advertise models this user was actually allocated.
  const pick = (needle: string) => models.find(m => m.includes(needle));
  const sonnet = pick("sonnet");
  const opus = pick("opus");
  const haiku = pick("haiku");
  if (sonnet) { env.ANTHROPIC_MODEL = sonnet; env.ANTHROPIC_DEFAULT_SONNET_MODEL = sonnet; }
  else if (models[0]) env.ANTHROPIC_MODEL = models[0];
  if (opus) env.ANTHROPIC_DEFAULT_OPUS_MODEL = opus;
  if (haiku) { env.ANTHROPIC_DEFAULT_HAIKU_MODEL = haiku; env.ANTHROPIC_SMALL_FAST_MODEL = haiku; }
  env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = "1";

  return JSON.stringify({ env, hasCompletedOnboarding: true }, null, 2);
}

function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        } catch { /* clipboard blocked — the text is on screen to select */ }
      }}
      className="rounded border border-neutral-200 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-900"
    >
      {copied ? "Copied" : label}
    </button>
  );
}

export default function KeysSection() {
  const [keys, setKeys] = useState<PersonalKeyRow[] | null>(null);
  const [models, setModels] = useState<string[]>([]);
  const [minted, setMinted] = useState<CreatedPersonalKey | null>(null);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [k, m] = await Promise.all([meApi.listKeys(), meApi.models()]);
      setKeys(k.keys);
      setModels(m.models);
    } catch (err) {
      setError(errorMessage(err, "Could not load your keys."));
      setKeys([]);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      setMinted(await meApi.createKey(name.trim()));
      setName("");
      await load();
    } catch (err) {
      setError(errorMessage(err, "Could not create the key."));
    } finally {
      setBusy(false);
    }
  };

  const handleRevoke = async (row: PersonalKeyRow) => {
    if (!confirm(`Revoke "${row.name}" (${row.prefix})? Any client using it stops working within 30 seconds.`)) return;
    setBusy(true);
    setError(null);
    try {
      await meApi.revokeKey(row.id);
      if (minted?.id === row.id) setMinted(null);
      await load();
    } catch (err) {
      setError(errorMessage(err, "Could not revoke the key."));
    } finally {
      setBusy(false);
    }
  };

  const active = (keys ?? []).filter(k => !k.revokedAt);
  const origin = gatewayOrigin();

  return (
    <section className="mt-10">
      <h2 className="text-base font-medium">Your API keys</h2>
      <p className="mt-1 text-xs text-neutral-400">
        For Claude Code, Cline, Roo Code and anything else that takes an Anthropic base URL.
        Usage on these keys is charged to you.
      </p>

      <form onSubmit={handleCreate} className="mt-4 flex gap-2">
        <label htmlFor="key-name" className="sr-only">Key name</label>
        <input
          id="key-name"
          value={name}
          onChange={e => setName(e.target.value)}
          maxLength={80}
          placeholder="laptop"
          className="flex-1 rounded border border-neutral-200 px-3 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900"
        />
        <button
          type="submit"
          disabled={busy || !name.trim()}
          className="rounded-md bg-neutral-900 px-4 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-white dark:text-neutral-900"
        >
          Create key
        </button>
      </form>

      {error && <p className="mt-2 text-xs text-red-600" role="alert">{error}</p>}

      {minted && (
        <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-900 dark:bg-amber-950/30">
          <div className="flex items-center justify-between">
            <p className="text-sm font-medium text-amber-900 dark:text-amber-200">
              Copy this key now — it is not shown again.
            </p>
            <CopyButton text={minted.key} label="Copy key" />
          </div>
          <code className="mt-2 block overflow-x-auto rounded bg-white px-3 py-2 font-mono text-xs dark:bg-neutral-900">
            {minted.key}
          </code>

          <div className="mt-4 flex items-center justify-between">
            <p className="text-xs font-medium text-amber-900 dark:text-amber-200">
              Claude Code — merge into <code>~/.claude/settings.json</code>
            </p>
            <CopyButton text={claudeCodeSettings(origin, minted.key, models)} label="Copy config" />
          </div>
          <pre className="mt-2 overflow-x-auto rounded bg-white p-3 font-mono text-xs dark:bg-neutral-900">
{claudeCodeSettings(origin, minted.key, models)}
          </pre>
          <p className="mt-2 text-xs text-amber-900 dark:text-amber-200">
            Then run <code>claude</code>. If it shows a login screen asking how you want to sign in,
            the settings file is not being read — do not pick &quot;Claude.ai Subscription&quot;, which
            bypasses this gateway entirely. Unset <code>ANTHROPIC_API_KEY</code> if your shell exports one.
          </p>
        </div>
      )}

      <div className="mt-5">
        {keys === null ? (
          <p className="text-sm text-neutral-400">Loading keys…</p>
        ) : active.length === 0 ? (
          <p className="rounded-lg border border-dashed border-neutral-200 p-4 text-sm text-neutral-400 dark:border-neutral-700">
            No active keys. Create one above to use Claude Code through the gateway.
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead className="text-left text-neutral-400">
              <tr>
                <th className="py-2 font-normal">Name</th>
                <th className="py-2 font-normal">Key</th>
                <th className="py-2 font-normal">Last used</th>
                <th className="py-2 font-normal" />
              </tr>
            </thead>
            <tbody>
              {active.map(k => (
                <tr key={k.id} className="border-t border-neutral-100 dark:border-neutral-800">
                  <td className="py-2.5">{k.name}</td>
                  <td className="py-2.5 font-mono text-neutral-500">{k.prefix}…</td>
                  <td className="py-2.5 text-neutral-400">
                    {k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleDateString() : "never"}
                  </td>
                  <td className="py-2.5 text-right">
                    <button
                      type="button"
                      onClick={() => handleRevoke(k)}
                      disabled={busy}
                      className="text-xs text-red-500 hover:underline disabled:opacity-50"
                    >
                      Revoke
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
