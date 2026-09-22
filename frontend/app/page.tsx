import Link from "next/link";

export default function Home() {
  const links = [
    { href: "/login", label: "Sign in", desc: "Email + password (admins: WhatsApp OTP)" },
    { href: "/register", label: "Create an organization", desc: "Sign up as the first admin (when enabled)" },
    { href: "/admin/providers", label: "AI provider", desc: "Connect your organization's Claude credential" },
    { href: "/admin/users", label: "Team members", desc: "Create users, allocate tokens & models, revoke keys" },
    { href: "/admin/team", label: "Teams", desc: "Manage workspaces and team members" },
    { href: "/admin", label: "Org dashboard", desc: "Workspace quota & top consumers" },
    { href: "/usage", label: "My usage", desc: "Your balance, quota & reset countdown" },
    { href: "/super-admin", label: "Super admin", desc: "Cross-tenant control plane & platform keys" },
  ];
  return (
    <div className="mx-auto max-w-2xl p-8">
      <h1 className="text-lg font-medium">apus-ai</h1>
      <p className="mb-6 mt-1 text-sm text-neutral-500">
        Connect Claude Code, VS Code, Cline or Roo Code to this gateway: run{" "}
        <code className="rounded bg-neutral-100 px-1.5 py-0.5 text-xs dark:bg-neutral-800">npx apus-ai</code>{" "}
        and sign in with the email your admin gave you.
      </p>
      <div className="grid gap-3">
        {links.map((l) => (
          <Link key={l.href} href={l.href} className="rounded-lg border border-neutral-100 p-4 hover:bg-neutral-50 dark:border-neutral-800">
            <p className="font-medium">{l.label}</p>
            <p className="text-sm text-neutral-500">{l.desc}</p>
          </Link>
        ))}
      </div>
    </div>
  );
}
