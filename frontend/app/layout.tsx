import "./globals.css";
import type { ReactNode } from "react";

export const metadata = { title: "YourCompany AI — Admin", description: "AI gateway dashboards" };

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body className="bg-white text-neutral-900 antialiased dark:bg-neutral-950 dark:text-neutral-100">
        {children}
      </body>
    </html>
  );
}
