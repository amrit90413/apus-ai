import { homedir, platform } from "node:os";
import { dirname, join } from "node:path";
import { access, chmod, copyFile, mkdir, readFile, writeFile } from "node:fs/promises";

// ---------------------------------------------------------------------------
// Clients the wizard knows how to configure.
// ---------------------------------------------------------------------------

export type ClientId = "claude" | "cline" | "roo";

export interface ClientInfo { id: ClientId; title: string; hint: string }

export const CLIENTS: ClientInfo[] = [
  { id: "claude", title: "Claude Code CLI / VS Code Claude extension", hint: "~/.claude/settings.json" },
  { id: "cline",  title: "Cline",    hint: "VS Code settings.json" },
  { id: "roo",    title: "Roo Code", hint: "VS Code settings.json" },
];

const CLIENT_ALIASES: Record<string, ClientId> = {
  claude: "claude", "claude-code": "claude", claudecode: "claude", code: "claude",
  cline: "cline",
  roo: "roo", "roo-code": "roo", roocode: "roo", "roo-cline": "roo",
};

// Parses `--clients claude,cline,roo`. Throws on unknown names.
export function parseClientList(raw: string): ClientId[] {
  const out: ClientId[] = [];
  for (const part of raw.split(",")) {
    const name = part.trim().toLowerCase();
    if (!name) continue;
    const id = CLIENT_ALIASES[name];
    if (!id) throw new Error(`Unknown client "${part.trim()}". Use: claude, cline, roo`);
    if (!out.includes(id)) out.push(id);
  }
  if (out.length === 0) throw new Error("No clients selected. Use: claude, cline, roo");
  return out;
}

export function clientTitle(id: ClientId): string {
  return CLIENTS.find(c => c.id === id)?.title ?? id;
}

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

export function claudeSettingsPath(): string {
  return join(homedir(), ".claude", "settings.json");
}

function vscodeUserDir(flavour: "Code" | "Code - Insiders"): string {
  switch (platform()) {
    case "darwin":  return join(homedir(), "Library", "Application Support", flavour, "User");
    case "win32":   return join(process.env.APPDATA ?? join(homedir(), "AppData", "Roaming"), flavour, "User");
    default:        return join(process.env.XDG_CONFIG_HOME ?? join(homedir(), ".config"), flavour, "User");
  }
}

async function exists(path: string): Promise<boolean> {
  try { await access(path); return true; } catch { return false; }
}

// Stable VS Code by default; falls back to Insiders when only Insiders is installed.
export async function vscodeSettingsPath(): Promise<string> {
  const stable = join(vscodeUserDir("Code"), "settings.json");
  if (await exists(stable)) return stable;
  const insiders = join(vscodeUserDir("Code - Insiders"), "settings.json");
  if (await exists(insiders)) return insiders;
  return stable;
}

// ---------------------------------------------------------------------------
// Model defaults for Claude Code env vars
// ---------------------------------------------------------------------------

export interface ModelDefaults {
  primary?: string;
  opus?: string;
  sonnet?: string;
  haiku?: string;     // ANTHROPIC_DEFAULT_HAIKU_MODEL: first haiku, else first model
  smallFast?: string; // ANTHROPIC_SMALL_FAST_MODEL: same rule as haiku
}

export function pickModelDefaults(models: string[], primary?: string): ModelDefaults {
  if (models.length === 0 && !primary) return {};
  const find = (needle: string) => models.find(m => m.toLowerCase().includes(needle));
  const haiku = find("haiku") ?? models[0] ?? primary;
  return {
    primary: primary ?? models[0],
    opus: find("opus"),
    sonnet: find("sonnet"),
    haiku,
    smallFast: haiku,
  };
}

// ---------------------------------------------------------------------------
// JSON merge helpers
// ---------------------------------------------------------------------------

// VS Code settings.json is JSONC: comments and trailing commas are common. We try
// strict JSON first, then a lenient parse. Comments can't survive a rewrite, so the
// caller backs the original up before writing whenever the lenient path was used.
// Walks `text` outside string literals; `onChar` returns how many chars to skip
// (0 = keep this char). String literals are always copied verbatim.
function rewriteOutsideStrings(text: string, onChar: (i: number) => { skip: number } | null): string {
  let out = "";
  let i = 0;
  const n = text.length;
  while (i < n) {
    if (text[i] === "\"") {
      let j = i + 1;
      while (j < n && text[j] !== "\"") { if (text[j] === "\\") j++; j++; }
      out += text.slice(i, j + 1); i = j + 1; continue;
    }
    const r = onChar(i);
    if (r) { i += r.skip; continue; }
    out += text[i]; i++;
  }
  return out;
}

function stripJsonc(text: string): string {
  // Pass 1: comments.
  const noComments = rewriteOutsideStrings(text, i => {
    if (text[i] === "/" && text[i + 1] === "/") {
      let j = i; while (j < text.length && text[j] !== "\n") j++;
      return { skip: j - i };
    }
    if (text[i] === "/" && text[i + 1] === "*") {
      const end = text.indexOf("*/", i + 2);
      return { skip: (end === -1 ? text.length : end + 2) - i };
    }
    return null;
  });
  // Pass 2: trailing commas (`,` followed only by whitespace then `}` or `]`).
  return rewriteOutsideStrings(noComments, i => {
    if (noComments[i] !== ",") return null;
    let j = i + 1;
    while (j < noComments.length && /\s/.test(noComments[j])) j++;
    return noComments[j] === "}" || noComments[j] === "]" ? { skip: 1 } : null;
  });
}

export type ParseOutcome =
  | { kind: "missing" }
  | { kind: "strict"; value: Record<string, unknown> }
  | { kind: "lenient"; value: Record<string, unknown> }
  | { kind: "invalid" };

export async function readJsonObject(path: string): Promise<ParseOutcome> {
  let raw: string;
  try { raw = await readFile(path, "utf8"); } catch { return { kind: "missing" }; }
  raw = raw.replace(/^﻿/, "");
  if (!raw.trim()) return { kind: "missing" };
  const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
  try { const v = JSON.parse(raw); if (isObj(v)) return { kind: "strict", value: v }; } catch { /* try lenient */ }
  try { const v = JSON.parse(stripJsonc(raw)); if (isObj(v)) return { kind: "lenient", value: v }; } catch { /* invalid */ }
  return { kind: "invalid" };
}

export interface WriteResult {
  path: string;
  created: boolean;
  backup?: string;         // set when the original was backed up
  backupReason?: "comments" | "invalid";
}

// Reads `path` (tolerating a missing file), lets `mutate` set our keys on the parsed
// object, then writes it back with 2-space indent. Everything we didn't touch is kept.
export async function mergeJsonFile(
  path: string,
  mutate: (obj: Record<string, unknown>) => void,
  opts: { mode?: number } = {},
): Promise<WriteResult> {
  const parsed = await readJsonObject(path);
  const result: WriteResult = { path, created: parsed.kind === "missing" };

  let obj: Record<string, unknown> = {};
  if (parsed.kind === "strict" || parsed.kind === "lenient") obj = parsed.value;

  if (parsed.kind === "lenient" || parsed.kind === "invalid") {
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    result.backup = `${path}.bak-${stamp}`;
    result.backupReason = parsed.kind === "lenient" ? "comments" : "invalid";
    await copyFile(path, result.backup);
  }

  mutate(obj);

  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, JSON.stringify(obj, null, 2) + "\n", { encoding: "utf8", mode: opts.mode });
  if (opts.mode !== undefined) {
    try { await chmod(path, opts.mode); } catch { /* not supported on this platform */ }
  }
  return result;
}

// ---------------------------------------------------------------------------
// Per-client config writers (exact keys from API_CONTRACT.md)
// ---------------------------------------------------------------------------

export interface GatewayConfig { gateway: string; apiKey: string; models: ModelDefaults }

export const CLAUDE_MANAGED_ENV = [
  "ANTHROPIC_AUTH_TOKEN",
  "ANTHROPIC_BASE_URL",
  "ANTHROPIC_MODEL",
  "ANTHROPIC_DEFAULT_OPUS_MODEL",
  "ANTHROPIC_DEFAULT_SONNET_MODEL",
  "ANTHROPIC_DEFAULT_HAIKU_MODEL",
  "ANTHROPIC_SMALL_FAST_MODEL",
  "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC",
] as const;

export function buildClaudeEnv(cfg: GatewayConfig): Record<string, string | undefined> {
  const m = cfg.models;
  return {
    ANTHROPIC_AUTH_TOKEN: cfg.apiKey,
    ANTHROPIC_BASE_URL: cfg.gateway,
    ANTHROPIC_MODEL: m.primary,
    ANTHROPIC_DEFAULT_OPUS_MODEL: m.opus,
    ANTHROPIC_DEFAULT_SONNET_MODEL: m.sonnet,
    ANTHROPIC_DEFAULT_HAIKU_MODEL: m.haiku,
    ANTHROPIC_SMALL_FAST_MODEL: m.smallFast,
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: "1",
  };
}

export interface ClaudeWriteResult extends WriteResult { warnings: string[] }

export async function writeClaudeSettings(cfg: GatewayConfig): Promise<ClaudeWriteResult> {
  const warnings: string[] = [];
  const res = await mergeJsonFile(claudeSettingsPath(), obj => {
    const envRaw = obj.env;
    const env: Record<string, unknown> = envRaw && typeof envRaw === "object" && !Array.isArray(envRaw)
      ? (envRaw as Record<string, unknown>) : {};
    const ours = buildClaudeEnv(cfg);
    for (const key of CLAUDE_MANAGED_ENV) {
      const v = ours[key];
      if (v === undefined) delete env[key]; else env[key] = v;
    }
    if (typeof env.ANTHROPIC_API_KEY === "string" && env.ANTHROPIC_API_KEY) {
      warnings.push("env.ANTHROPIC_API_KEY is also set in this file; remove it so Claude Code uses the gateway key.");
    }
    obj.env = env;
    obj.hasCompletedOnboarding = true;
  }, { mode: 0o600 });
  return { ...res, warnings };
}

// Cline and Roo Code both live in VS Code's user settings.json.
export async function writeVsCodeSettings(cfg: GatewayConfig, clients: ClientId[]): Promise<WriteResult> {
  const path = await vscodeSettingsPath();
  return mergeJsonFile(path, obj => {
    if (clients.includes("cline")) {
      obj["cline.apiProvider"] = "anthropic";
      obj["cline.anthropicBaseUrl"] = cfg.gateway;
      obj["cline.apiKey"] = cfg.apiKey;
    }
    if (clients.includes("roo")) {
      obj["roo-cline.apiProvider"] = "anthropic";
      obj["roo-cline.anthropicBaseUrl"] = cfg.gateway;
      obj["roo-cline.apiKey"] = cfg.apiKey;
    }
  }, { mode: 0o600 });
}
