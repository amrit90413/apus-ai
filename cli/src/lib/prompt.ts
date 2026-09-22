import pc from "picocolors";
import prompts from "prompts";

// Single-question prompt that exits cleanly on Ctrl-C instead of returning undefined.
export async function ask<T = string>(question: prompts.PromptObject<string>): Promise<T> {
  const answers = await prompts(question, {
    onCancel: () => { console.log(pc.dim("\nAborted.")); process.exit(130); },
  });
  return answers[question.name as string] as T;
}

export function isValidEmail(value: string): boolean {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());
}

// Reads the first line of stdin (for `--password-stdin`). Never echoes it.
export async function readLineFromStdin(): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of process.stdin) chunks.push(chunk as Buffer);
  const text = Buffer.concat(chunks).toString("utf8");
  return text.split(/\r?\n/)[0] ?? "";
}

// Display-safe form of a personal key: prefix only, never the full secret.
export function maskKey(key: string, prefix?: string | null): string {
  const shown = prefix && key.startsWith(prefix) ? prefix : key.slice(0, 9);
  return `${shown}…`;
}
