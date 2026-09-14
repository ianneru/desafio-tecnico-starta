"use client";

import { useEffect, useState, useTransition } from "react";
import { useRouter } from "next/navigation";

export default function RefreshControls({
  renderedAt,
  intervalMs = 5000,
}: {
  renderedAt: string;
  intervalMs?: number;
}) {
  const router = useRouter();
  const [auto, setAuto] = useState(true);
  const [isPending, startTransition] = useTransition();

  // ponytail: polling, porque a API não expõe stream. Trocar por SSE/WebSocket se push for necessário.
  useEffect(() => {
    if (!auto) return;
    const timer = setInterval(() => router.refresh(), intervalMs);
    return () => clearInterval(timer);
  }, [auto, intervalMs, router]);

  return (
    <div className="refresh">
      <span className="muted">Atualizado às {renderedAt}</span>
      <label className="toggle">
        <input type="checkbox" checked={auto} onChange={(event) => setAuto(event.target.checked)} />
        Atualização automática (5s)
      </label>
      <button type="button" onClick={() => startTransition(() => router.refresh())} disabled={isPending}>
        {isPending ? "Atualizando…" : "Atualizar agora"}
      </button>
    </div>
  );
}
