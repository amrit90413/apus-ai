"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { authApi } from "@/lib/api";

const links = [
  { href: "/admin/users", label: "Users" },
  { href: "/admin/team", label: "Team" },
  { href: "/admin/providers", label: "Providers" },
  { href: "/usage", label: "Usage" },
];

// Minimal top bar shared by the org-admin pages.
export default function AdminNav() {
  const pathname = usePathname();
  return (
    <nav aria-label="Admin" className="mb-6 flex items-center gap-4 border-b border-neutral-100 pb-3 text-sm dark:border-neutral-800">
      <Link href="/" className="mr-2 font-medium">YourCompany AI</Link>
      {links.map(l => {
        const active = pathname === l.href || pathname.startsWith(`${l.href}/`);
        return (
          <Link
            key={l.href}
            href={l.href}
            aria-current={active ? "page" : undefined}
            className={active
              ? "text-neutral-900 underline underline-offset-4 dark:text-neutral-100"
              : "text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"}
          >
            {l.label}
          </Link>
        );
      })}
      <button
        type="button"
        onClick={() => { void authApi.logout(); }}
        className="ml-auto text-neutral-400 hover:text-neutral-900 hover:underline dark:hover:text-neutral-100"
      >
        Logout
      </button>
    </nav>
  );
}
