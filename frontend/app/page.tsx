import Link from "next/link";

export default function Home() {
  const links = [
    { href: "/super-admin", label: "Super admin", desc: "Cross-tenant control plane" },
    { href: "/admin", label: "Org admin", desc: "Per-employee tracking" },
    { href: "/usage", label: "My usage", desc: "Your quota & reset countdown" },
  ];
  return (
    <div className="mx-auto max-w-2xl p-8">
      <h1 className="mb-6 text-lg font-medium">YourCompany AI</h1>
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
