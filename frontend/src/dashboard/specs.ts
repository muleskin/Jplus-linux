/**
 * The SPECS view — where a human reads what JARVIS proposes and what JARVIS
 * produced, and answers out loud.
 *
 * The user's words, and the whole brief:
 *
 *   "JARVIS needs to open a clean/simple UI for specs and plans for people to
 *    actually see it and communicate feedback to JARVIS by voice."
 *   "you go do it, and then you report back when it's done, you pull it up
 *    for them so it's easy for them to review"
 *
 * So this is not a spec viewer. It is one surface with two states: a document
 * AWAITING APPROVAL, and finished work AWAITING REVIEW. Both are the same
 * page, because they are the same act — a person looking at something and
 * saying what they think of it.
 *
 * TWO RULES SHAPE EVERYTHING HERE.
 *
 * 1. THE NUMBERS ARE THE INTERFACE. Every top-level section is rendered with
 *    a large, unmissable number, so the user can say "change three", "drop
 *    five", "approved" and be understood. Those numbers are computed once, in
 *    `specs.py`, and reach JARVIS through `review_document` unchanged. This
 *    file must therefore never renumber, reorder, filter or collapse
 *    sections: whatever it draws out of order stops being an address.
 *
 * 2. THE PAGE IS FOR READING. There is no comment box, no editor, no inline
 *    edit and no approve button. The user talks, JARVIS revises the file or
 *    records the approval, and the page notices the file changed. The only
 *    control on it is which document you are looking at.
 *
 * Everything rendered here is model-written text off someone's disk, so it
 * goes through `textContent` and the safe Markdown renderer — never
 * `innerHTML`. That is the reason the rule exists, not an exception to it.
 */
import {
  listSpecs, getSpecDocument, ApiError,
  type SpecProject, type SpecDocument, type SpecDocumentMeta,
  type ReviewState, type ApprovalState, type PlanProgress,
} from "./api";
import { connectSpecsLive } from "./specs-live";
import { markdown } from "./markdown";
import { el, row, bar, pill, emptyState, type Tone } from "./ui";

let started = false;
let projects: SpecProject[] = [];
/**
 * WHICH COPY is open, by directory — not by name.
 *
 * A project with a Claude Code worktree appears under one name in two
 * directories, so the name alone cannot say which row the user clicked.
 */
let openRoot: string | null = null;
let openPath: string | null = null;
/** Guards against a slow fetch painting over a newer selection. */
let openToken = 0;
/** The document currently PAINTED, so a repaint of the same one can keep
 * the reader's place. See `paintDetail`. */
let paintedKey: string | null = null;

function openedProject(): SpecProject | undefined {
  return projects.find((p) => p.path === openRoot);
}

// ── The vocabulary ──────────────────────────────────────────────────────────
//
// Four project states and three approval states, each with a tone, a word,
// and a sentence that says what the user is expected to DO. Nothing here
// depends on colour alone.

interface StateCopy { tone: Tone; label: string; line: string }

const PROJECT_STATE: Record<ReviewState, StateCopy> = {
  awaiting: {
    tone: "warn", label: "needs you",
    line: "Waiting on your approval.",
  },
  planning: {
    tone: "accent", label: "planning",
    line: "Approved. JARVIS is writing the plan.",
  },
  building: {
    tone: "accent", label: "building",
    line: "Approved and under way.",
  },
  review: {
    tone: "ok", label: "for review",
    line: "Finished. Waiting on your review.",
  },
};

const APPROVAL: Record<ApprovalState, StateCopy> = {
  awaiting: {
    tone: "warn", label: "awaiting approval",
    line: "Nobody has approved this yet. Read it, then tell JARVIS "
        + "“approved” — or name a section to change.",
  },
  approved: {
    tone: "ok", label: "approved",
    line: "You approved these words. A revision will say so here.",
  },
  superseded: {
    tone: "warn", label: "revised since approval",
    line: "This has been rewritten since you approved it. Read it again "
        + "before it is built.",
  },
};

// The dot's SHAPE carries the state with colour switched off, and it is
// built here rather than through `statusDot` because these four states are
// not run states: `statusDot` would pick its own tone off the run/session
// vocabulary and the dot would end up disagreeing with the row it sits in.
const DOT_SHAPE: Record<ReviewState, string> = {
  awaiting: "fault",     // a wrong-shaped mark: something is stopped on you
  planning: "pending",   // an outline: agreed, not started
  building: "live",      // the one ambient animation in the system
  review: "done",        // filled: complete
};

function stateDot(state: ReviewState, tone: Tone): HTMLElement {
  const dot = el("span", `dot dot--${DOT_SHAPE[state] ?? "pending"} tone-${tone}`);
  dot.setAttribute("aria-hidden", "true");
  return dot;
}

function fmtWhen(epochSec: number): string {
  if (!epochSec) return "";
  return new Date(epochSec * 1000).toLocaleString([], {
    month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
  });
}

function id(name: string): HTMLElement | null {
  return document.getElementById(name);
}

function showBanner(text: string | null): void {
  const banner = id("specs-banner");
  if (!banner) return;
  banner.hidden = text === null;
  banner.textContent = text ?? "";
}

function setUnavailable(unavailable: boolean): void {
  const notice = id("specs-unavailable");
  const body = id("specs-split");
  if (notice) notice.hidden = !unavailable;
  if (body) body.hidden = unavailable;
}

// ── Master: the projects, then that project's documents ─────────────────────

function projectRow(project: SpecProject): HTMLElement {
  const copy = PROJECT_STATE[project.state] ?? PROJECT_STATE.building;
  const r = row({
    tone: copy.tone,
    attn: project.state === "awaiting",
    onOpen: () => selectProject(project.path),
    label: project.where
      ? `${project.name}, ${project.where} — ${copy.label}`
      : `${project.name} — ${copy.label}`,
  });
  r.setLead(stateDot(project.state, copy.tone));
  r.setTitle(project.name);
  // `where` is only sent when the name lives in more than one directory, so
  // this replaces the state word exactly when the state word alone would
  // leave two rows indistinguishable.
  r.setSub(project.where ? `${project.where} · ${copy.label}` : copy.label);
  r.addMeta(`${project.documents.length} doc${project.documents.length === 1 ? "" : "s"}`,
            { cls: "specs-count" });
  if (project.path === openRoot) r.root.classList.add("is-open");

  const progress = project.progress;
  if (progress && progress.total > 0) {
    const gauge = bar("Tasks", { tone: copy.tone, slim: true });
    gauge.set((progress.done / progress.total) * 100,
              `${progress.done}/${progress.total}`);
    r.addBody(gauge.root);
  }
  return r.root;
}

function documentRow(project: SpecProject, doc: SpecDocumentMeta): HTMLElement {
  const copy = APPROVAL[doc.approval.state] ?? APPROVAL.awaiting;
  const r = row({
    tone: copy.tone,
    onOpen: () => selectDocument(project.path, doc.path),
    label: `${doc.title} — ${copy.label}`,
  });
  r.setLead(pill(doc.kind, doc.kind === "spec" ? "accent" : "idle", "ghost"));
  r.setTitle(doc.title);
  r.setSub(`${doc.sections} section${doc.sections === 1 ? "" : "s"}`);
  r.addTrail(pill(copy.label, copy.tone));
  r.addMeta(fmtWhen(doc.modified), { cls: "specs-when" });
  if (doc.path === openPath && project.path === openRoot) {
    r.root.classList.add("is-open");
  }
  return r.root;
}

/**
 * The badge on the tab: how many projects are waiting on the USER.
 *
 * Both states count. "Awaiting approval" and "finished, waiting to be
 * reviewed" are the same ask — a person has to look at something — and this
 * tab exists for exactly that ask. Anything mid-flight is JARVIS's problem,
 * not the user's, and is not counted.
 */
function paintBadge(): void {
  const badge = id("specs-tab-badge");
  if (!badge) return;
  const waiting = projects.filter(
    (p) => p.state === "awaiting" || p.state === "review").length;
  badge.hidden = waiting === 0;
  badge.textContent = String(waiting);
  badge.setAttribute("aria-label", `${waiting} waiting on you`);
}

function paintMaster(): void {
  paintBadge();
  const list = id("specs-projects");
  const meta = id("specs-projects-meta");
  if (!list) return;
  list.replaceChildren();
  if (meta) meta.textContent = projects.length ? String(projects.length) : "";

  if (projects.length === 0) {
    list.append(emptyState(
      "Nothing to review yet. Talk an idea through with JARVIS and the "
      + "design he writes down will appear here."));
  } else {
    for (const project of projects) list.append(projectRow(project));
  }

  const section = id("specs-docs-section");
  const docs = id("specs-docs");
  const docsMeta = id("specs-docs-meta");
  const project = openedProject();
  if (!section || !docs) return;
  section.hidden = project === undefined;
  if (!project) return;

  docs.replaceChildren();
  if (docsMeta) docsMeta.textContent = String(project.documents.length);
  for (const doc of project.documents) docs.append(documentRow(project, doc));
}

// ── Detail: the numbered document ───────────────────────────────────────────

/** The band at the top of the document: what state it is in, in words. */
function statusBand(doc: SpecDocument): HTMLElement {
  const copy = APPROVAL[doc.approval.state] ?? APPROVAL.awaiting;
  const band = el("div", `specs-band tone-${copy.tone}`);

  const head = el("div", "specs-band-head");
  head.append(pill(copy.label, copy.tone));
  if (doc.approval.state !== "awaiting" && doc.approval.approved_at) {
    head.append(el("span", "specs-band-when",
                   `approved ${fmtWhen(doc.approval.approved_at)}`));
  }
  band.append(head, el("p", "specs-band-line", copy.line));

  const progress = doc.progress;
  if (progress && progress.total > 0) {
    band.append(progressBlock(progress));
  }
  return band;
}

/**
 * A plan in flight, or a plan that is finished. The same block says both:
 * the gauge is the fraction of tasks ticked, and the line underneath says
 * which numbered section is being worked on — the number the user can see
 * on the page and say out loud.
 */
function progressBlock(progress: PlanProgress): HTMLElement {
  const done = progress.done >= progress.total;
  const wrap = el("div", "specs-progress");
  const gauge = bar("Tasks done", { tone: done ? "ok" : "accent" });
  gauge.set((progress.done / progress.total) * 100,
            `${progress.done} of ${progress.total}`);
  wrap.append(gauge.root);

  if (done) {
    wrap.append(el("p", "specs-progress-note",
                   "Every task ticked. This is the work to review."));
  } else if (progress.current) {
    const note = el("p", "specs-progress-note");
    note.append(el("span", "specs-progress-now", "Now"));
    if (progress.current_section) {
      note.append(el("span", "specs-jump-num",
                     String(progress.current_section).padStart(2, "0")));
    }
    note.append(document.createTextNode(` ${progress.current}`));
    if (progress.steps_total > 0) {
      note.append(el("span", "specs-progress-steps",
                     `${progress.steps_done}/${progress.steps_total} steps`));
    }
    wrap.append(note);
  }
  return wrap;
}

/** One numbered section. The number is the addressing mechanism, so it is
 * the loudest thing in the block and it is selectable text. */
function sectionBlock(section: SpecDocument["sections"][number]): HTMLElement {
  const article = el("article", "specs-section");
  article.id = `specs-section-${section.number}`;

  const num = el("div", "specs-section-num",
                 String(section.number).padStart(2, "0"));
  const body = el("div", "specs-section-body");
  body.append(el("h3", "specs-section-title", section.title));
  if (section.body.trim()) body.append(markdown(section.body, "md"));

  article.append(num, body);
  return article;
}

/**
 * Draw the open document.
 *
 * KEEPS THE READER'S PLACE. A repaint of the SAME document — which is what
 * every socket hint causes, and during a build a checkbox ticks every few
 * seconds — restores the scroll offset it found. Only a different document
 * starts at the top. This used to end in an unconditional
 * `body.scrollTop = 0`, directly under a comment saying that losing your
 * place because a session ticked a checkbox would make the page unusable.
 */
function paintDetail(doc: SpecDocument | null, message?: string): void {
  const title = id("specs-doc-title");
  const meta = id("specs-doc-meta");
  const body = id("specs-doc-body");
  if (!title || !body) return;

  if (doc === null) {
    paintedKey = null;
    title.textContent = "Document";
    if (meta) meta.textContent = "";
    body.replaceChildren(emptyState(
      message ?? "Pick a project on the left to read what JARVIS wrote."));
    return;
  }

  const key = `${openRoot ?? ""} ${doc.path}`;
  const wasAt = key === paintedKey ? body.scrollTop : 0;
  paintedKey = key;

  title.textContent = doc.title;
  if (meta) {
    meta.textContent = `${doc.kind} · ${doc.sections.length} section`
      + `${doc.sections.length === 1 ? "" : "s"}`;
  }

  const parts: Node[] = [statusBand(doc)];

  // The instruction, once, at the top. It is not decoration: without it the
  // numbers look like ornament instead of the way to talk about the page.
  parts.push(el("p", "specs-howto",
    "Say a number to talk about a section — “read me three”, "
    + "“change five”, “drop the last one”. Say "
    + "“approved” when it is right."));

  parts.push(el("p", "specs-path", doc.path));

  if (doc.preamble.trim()) {
    parts.push(markdown(doc.preamble, "md specs-preamble"));
  }

  if (doc.sections.length === 0) {
    parts.push(emptyState(
      "This document has no headings, so there is nothing to number yet.",
      true));
  } else {
    const list = el("div", "specs-sections");
    for (const section of doc.sections) list.append(sectionBlock(section));
    parts.push(list);
  }

  body.replaceChildren(...parts);
  // A document that shrank cannot be scrolled as far as it was; the browser
  // clamps this for us, which is the behaviour we want.
  body.scrollTop = wasAt;
}

// ── Selection ───────────────────────────────────────────────────────────────

/** The document a project most wants a person to look at. */
function defaultDocument(project: SpecProject): string {
  const wanted = project.documents.find(
    (d) => d.approval.state !== "approved");
  return (wanted ?? project.documents[0])?.path ?? "";
}

function selectProject(root: string): void {
  const project = projects.find((p) => p.path === root);
  if (!project) return;
  openRoot = root;
  openPath = defaultDocument(project);
  paintMaster();
  void loadDocument();
}

function selectDocument(root: string, path: string): void {
  openRoot = root;
  openPath = path;
  paintMaster();
  void loadDocument();
}

async function loadDocument(): Promise<void> {
  const token = ++openToken;
  const project = openedProject();
  if (!project || !openPath) {
    paintDetail(null);
    return;
  }
  try {
    const doc = await getSpecDocument(project.name, openPath, project.path);
    if (token !== openToken) return;      // superseded by a newer selection
    paintDetail(doc);
  } catch (e) {
    if (token !== openToken) return;
    if (!(e instanceof ApiError && e.status === 404)) {
      console.error("[specs] document fetch failed", e);
    }
    paintDetail(null,
      "Could not read that document. It may have been moved or renamed on "
      + "disk — JARVIS rewrites these files as you talk.");
  }
}

// ── Reconcile ───────────────────────────────────────────────────────────────

async function reconcile(): Promise<void> {
  let fresh: SpecProject[];
  try {
    fresh = await listSpecs();
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) {
      setUnavailable(true);
      showBanner(null);
      return;
    }
    console.error("[specs] reconcile failed", e);
    showBanner("Cannot reach the JARVIS server.");
    return;
  }

  setUnavailable(false);
  showBanner(null);
  projects = fresh;

  // Keep whatever the user was reading, if it is still there. Losing your
  // place because a session ticked a checkbox would make the page unusable
  // exactly while the work is happening — which is why `paintDetail` also
  // restores the scroll offset when it repaints the same document.
  let project = openedProject();
  if (!project) {
    // Nothing chosen, or it disappeared: open whatever most wants an eye.
    project = projects.find((p) => p.state === "awaiting")
      ?? projects.find((p) => p.state === "review")
      ?? projects[0];
    openRoot = project?.path ?? null;
    openPath = project ? defaultDocument(project) : null;
  } else if (!project.documents.some((d) => d.path === openPath)) {
    openPath = defaultDocument(project);
  }

  paintMaster();
  await loadDocument();
}

export function initSpecs(): void {
  if (started) return;
  started = true;
  void reconcile();
  // A spec is revised by JARVIS writing to disk and an approval is a file
  // appearing beside it, so the socket only ever says "something moved" and
  // the truth is re-read from /api/specs.
  connectSpecsLive({
    onReconcile: () => void reconcile(),
    onConnectionChange: (connected) => {
      if (!connected) showBanner("Reconnecting to JARVIS…");
    },
  });
}

/** Re-read on demand — e.g. when the user switches back to this tab. */
export function refreshSpecs(): void {
  void reconcile();
}
