"use client";
import { useEffect } from "react";
import { useRouter } from "next/navigation";

/**
 * The provider page moved to /settings/ai-providers when it grew past Anthropic.
 * Kept as a redirect so bookmarks, docs and the old OAuth completion path still land
 * somewhere useful.
 */
export default function LegacyProvidersPage() {
  const router = useRouter();
  useEffect(() => { router.replace("/settings/ai-providers"); }, [router]);
  return <div className="p-8 text-sm text-neutral-400">Taking you to AI providers…</div>;
}
