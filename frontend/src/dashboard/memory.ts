/**
 * The Memory view: what JARVIS carries into every conversation, and a way
 * to read (and know you can edit) the plain-Markdown folder behind it.
 *
 * The backend endpoint this targets (`GET /api/memory`, `GET
 * /api/memory/<kind>/<slug>`) does not exist yet as of this view being
 * built — see the contract in the Memory view report. A 404 means "not
 * shipped yet", not "empty", and must degrade calmly with no console noise.
 *
 * Every string here — titles, hooks, and especially journal/memory body
 * text — can contain content JARVIS copied out of someone else's Claude
 * Code session. Attacker-influenced, same discipline as Sessions: always
 * textContent, never innerHTML.
 */
import {
  getMemory, getMemoryDoc, ApiError,
  type MemorySnapshot, type MemoryKind,
  type MemoryIndexEntry, type MemoryFileEntry, type ProjectNoteEntry, type JournalEntry,
} from "./api";
import { el, row, button, emptyState } from "./ui";

let started = false;
let openDocToken = 0;

function fmtWhen(epochSec: number): string {
  if (!epochSec) return "—";
  return new Date(epochSec * 1000).toLocaleString([], {
    month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
  });
}

function section(id: string): HTMLElement | null {
  return document.getElementById(id);
}

function setMeta(id: string, count: number): void {
  const meta = section(id);
  if (meta) meta.textContent = count > 0 ? String(count) : "";
}

function showBanner(text: string | null): void {
  const banner = section("memory-banner");
  if (!banner) return;
  if (text === null) {
    banner.hidden = true;
    return;
  }
  banner.hidden = false;
  banner.textContent = text;
}

function setUnavailable(unavailable: boolean): void {
  const notice = section("memory-unavailable");
  const body = section("memory-body");
  if (notice) notice.hidden = !unavailable;
  if (body) body.hidden = unavailable;
}

/** A single clickable row: title, a muted subtitle, and a "when" on the
 * right — the shape shared by memories, projects and journal entries. */
function docRow(
  titleText: string, subText: string, whenText: string,
  onOpen: () => void, highlight = false,
): HTMLElement {
  const r = row({
    onOpen,
    label: titleText,
    tone: highlight ? "accent" : undefined,
  });
  r.setTitle(titleText);
  if (subText) r.setSub(subText);
  r.addMeta(whenText, { cls: "memory-row-when" });
  return r.root;
}

function openDoc(kind: MemoryKind, slug: string, titleText: string): void {
  const pane = section("memory-detail");
  if (!pane) return;
  const token = ++openDocToken;

  pane.hidden = false;
  pane.replaceChildren();

  const head = el("header", "pane-head");
  const close = button("Close", closeDoc, { quiet: true });
  close.classList.add("pane-close");
  head.append(el("h2", "pane-title", titleText), close);

  const body = el("div", "pane-body");
  const doc = el("pre", "doc", "Loading…");
  body.append(el("div", "pane-meta", `${kind} · ${slug}`), doc);

  pane.append(head, body);

  getMemoryDoc(kind, slug)
    .then((loaded) => {
      if (token !== openDocToken) return; // superseded by another open/close
      doc.textContent = loaded.text;
    })
    .catch((e) => {
      if (token !== openDocToken) return;
      if (!(e instanceof ApiError && e.status === 404)) {
        console.error("[memory] doc fetch failed", e);
      }
      doc.textContent = "Could not load this file. It may have been moved or deleted on disk.";
    });
}

function closeDoc(): void {
  openDocToken++;
  const pane = section("memory-detail");
  if (!pane) return;
  pane.hidden = true;
  pane.replaceChildren();
}

function paintPath(path: string): void {
  const holder = section("memory-path");
  if (!holder) return;
  holder.replaceChildren();

  const code = el("code", "memory-path-value", path);
  const copy = button("Copy path", () => {
    navigator.clipboard?.writeText(path).then(
      () => { copy.textContent = "Copied"; setTimeout(() => { copy.textContent = "Copy path"; }, 1500); },
      () => { copy.textContent = "Could not copy"; setTimeout(() => { copy.textContent = "Copy path"; }, 1500); },
    );
  });

  const pathRow = el("div", "memory-path-row");
  pathRow.append(code, copy);

  holder.append(
    el("div", "memory-hint", "Plain Markdown on disk. Edit it directly — JARVIS reads it fresh every conversation."),
    pathRow,
  );
}

function paintIndex(entries: MemoryIndexEntry[]): void {
  const list = section("memory-index-list");
  if (!list) return;
  list.replaceChildren();
  setMeta("memory-index-meta", entries.length);
  if (entries.length === 0) {
    list.append(emptyState("Nothing indexed yet.", true));
    return;
  }
  for (const entry of entries) {
    list.append(docRow(
      entry.title, entry.hook, "",
      () => openDoc("memory", entry.slug, entry.title),
    ));
  }
}

function paintMemories(entries: MemoryFileEntry[]): void {
  const list = section("memory-files-list");
  if (!list) return;
  list.replaceChildren();
  setMeta("memory-files-meta", entries.length);
  if (entries.length === 0) {
    list.append(emptyState("No memory files yet.", true));
    return;
  }
  const sorted = [...entries].sort((a, b) => b.modified - a.modified);
  for (const m of sorted) {
    list.append(docRow(
      m.title, m.slug, fmtWhen(m.modified),
      () => openDoc("memory", m.slug, m.title),
    ));
  }
}

function paintProjects(entries: ProjectNoteEntry[]): void {
  const list = section("memory-projects-list");
  if (!list) return;
  list.replaceChildren();
  setMeta("memory-projects-meta", entries.length);
  if (entries.length === 0) {
    list.append(emptyState("No project notes yet.", true));
    return;
  }
  const sorted = [...entries].sort((a, b) => b.modified - a.modified);
  for (const p of sorted) {
    list.append(docRow(
      p.title, p.slug, fmtWhen(p.modified),
      () => openDoc("project", p.slug, p.title),
    ));
  }
}

function paintJournalList(entries: JournalEntry[], latestSlug: string | null): void {
  const list = section("memory-journal-list");
  if (!list) return;
  list.replaceChildren();
  setMeta("memory-journal-meta", entries.length);
  if (entries.length === 0) {
    list.append(emptyState("No journal entries yet.", true));
    return;
  }
  const sorted = [...entries].sort((a, b) => b.when - a.when);
  for (const j of sorted) {
    const isLatest = j.slug === latestSlug;
    list.append(docRow(
      j.reason || "(handover)", j.slug, fmtWhen(j.when),
      () => openDoc("journal", j.slug, j.reason || j.slug),
      isLatest,
    ));
  }
}

async function paintLatestJournal(latestSlug: string | null): Promise<void> {
  const wrap = section("journal-latest-section");
  const body = section("journal-latest-body");
  const meta = section("journal-latest-meta");
  if (!wrap || !body) return;

  if (!latestSlug) {
    wrap.hidden = true;
    return;
  }
  wrap.hidden = false;
  if (meta) meta.textContent = latestSlug;
  body.textContent = "Loading…";
  try {
    const doc = await getMemoryDoc("journal", latestSlug);
    body.textContent = doc.text;
  } catch (e) {
    if (!(e instanceof ApiError && e.status === 404)) {
      console.error("[memory] latest journal fetch failed", e);
    }
    body.textContent = "Could not load the latest journal entry.";
  }
}

async function reconcile(): Promise<void> {
  let snap: MemorySnapshot;
  try {
    snap = await getMemory();
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) {
      // Expected until the backend endpoint ships — a calm empty state,
      // not an error. No console noise.
      setUnavailable(true);
      showBanner(null);
      return;
    }
    console.error("[memory] reconcile failed", e);
    showBanner("Cannot reach the JARVIS server.");
    return;
  }

  setUnavailable(false);
  showBanner(null);
  paintPath(snap.path);
  paintIndex(snap.index);
  paintMemories(snap.memories);
  paintProjects(snap.projects);
  paintJournalList(snap.journal, snap.latest_journal_slug);
  void paintLatestJournal(snap.latest_journal_slug);
}

export function initMemory(): void {
  if (started) return;
  started = true;
  void reconcile();
}

/** Re-fetch on demand — e.g. when the user switches back to this tab,
 * since the folder is user-edited on disk and there is no live socket
 * pushing changes the way runs/sessions get. */
export function refreshMemory(): void {
  void reconcile();
}
