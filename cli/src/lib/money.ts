// Money crosses the wire as an integer count of minor units (paise, cents) plus a
// currency code — never as a float. These helpers only format it for a terminal.

const SYMBOLS: Record<string, string> = { USD: "$", INR: "₹", EUR: "€", GBP: "£", JPY: "¥" };
const ZERO_DECIMAL = new Set(["JPY", "KRW"]);

export function money(minor: number | null | undefined, currency = "USD"): string {
  if (minor === null || minor === undefined) return "—";
  const code = currency.toUpperCase();
  const exponent = ZERO_DECIMAL.has(code) ? 0 : 2;
  const symbol = SYMBOLS[code] ?? `${code} `;
  return symbol + (minor / 10 ** exponent).toLocaleString(undefined, {
    minimumFractionDigits: exponent,
    maximumFractionDigits: exponent,
  });
}

/** A 20-cell bar for a 0–100 percentage. */
export function bar(percent: number): string {
  const filled = Math.max(0, Math.min(20, Math.round(percent / 5)));
  return "█".repeat(filled).padEnd(20, "░");
}
