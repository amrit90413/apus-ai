import pc from "picocolors";
import prompts from "prompts";
import { requireCredentials, getValidAccessToken } from "../lib/credentials.js";

interface ChatOptions { model: string; once?: string }

// Streams a single turn from the gateway's SSE endpoint, printing tokens as they
// arrive. Returns false if the caller should stop (quota exhausted / auth error).
async function streamTurn(
  apiBase: string,
  token: string,
  model: string,
  messages: { role: string; content: string }[],
): Promise<boolean> {
  const res = await fetch(`${apiBase}/api/v1/chat/stream`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify({ model, messages, maxTokens: 4096 }),
  });

  // Quota headers come back on every response — show remaining budget.
  const remaining = res.headers.get("x-quota-remaining");
  const resetIn = res.headers.get("x-quota-reset-seconds");

  if (res.status === 429) {
    const mins = resetIn ? Math.ceil(Number(resetIn) / 60) : "?";
    console.error(pc.yellow(`\nQuota reached. Resets in ~${mins} min.`));
    return false;
  }
  if (res.status === 403) {
    console.error(pc.red(`\nThis model isn't enabled for your workspace.`));
    return false;
  }
  if (!res.ok || !res.body) {
    console.error(pc.red(`\nGateway error (${res.status}).`));
    return false;
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let assistant = "";

  process.stdout.write(pc.cyan("\nassistant ▸ "));
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let idx;
    while ((idx = buffer.indexOf("\n\n")) !== -1) {
      const frame = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      const event = /event: (.*)/.exec(frame)?.[1];
      const dataLine = /data: (.*)/.exec(frame)?.[1];
      if (!dataLine) continue;
      const data = JSON.parse(dataLine);

      if (event === "token") { process.stdout.write(data.text); assistant += data.text; }
      else if (event === "error") console.error(pc.red(`\n[${data.code}] ${data.message}`));
      else if (event === "done" && data.usage) {
        const u = data.usage;
        process.stdout.write(
          pc.dim(`\n\n  ${u.inputTokens}+${u.outputTokens} tok` +
            (remaining ? ` · ${Number(remaining).toLocaleString()} left in window` : "")) + "\n");
      }
    }
  }
  return true;
}

export async function chat(opts: ChatOptions): Promise<void> {
  const creds = await requireCredentials();

  const token = await getValidAccessToken(creds);
  const history: { role: string; content: string }[] = [];

  // Non-interactive one-shot: `yourcompany-ai chat -p "..."` (good for shell pipes).
  if (opts.once) {
    history.push({ role: "user", content: opts.once });
    await streamTurn(creds.apiBase, token, opts.model, history);
    return;
  }

  console.log(pc.dim(`Connected to ${creds.apiBase} · model ${opts.model}. Ctrl-C to exit.`));
  while (true) {
    const { input } = await prompts({ type: "text", name: "input", message: pc.green("you") });
    if (!input) break;
    history.push({ role: "user", content: input });
    const ok = await streamTurn(creds.apiBase, token, opts.model, history);
    if (!ok) break;
  }
}
