/** Tracks the latest one-line "what is it doing" per active run. */

const summaries = new Map<string, string>();

export function summaryFor(runId: string): string {
  return summaries.get(runId) ?? "";
}

export function noteEvent(runId: string, kind: string, payload: unknown): boolean {
  if (kind !== "assistant") return false;
  const blocks = (payload as { message?: { content?: unknown[] } })
    ?.message?.content ?? [];
  for (const raw of blocks) {
    const b = raw as { type?: string; text?: string; name?: string; input?: Record<string, unknown> };
    if (b.type === "text" && b.text) {
      summaries.set(runId, b.text.replace(/\s+/g, " ").slice(0, 160));
      return true;
    }
    if (b.type === "tool_use") {
      const target = b.input?.file_path ?? b.input?.command ?? "";
      summaries.set(runId, `${b.name}: ${String(target)}`.slice(0, 160));
      return true;
    }
  }
  return false;
}
