/**
 * JARVIS dashboard — primitive builders.
 *
 * The TS half of the design system: one function per primitive in
 * `theme/primitives.css`, so every view produces identical markup and a
 * change to a shape happens in exactly one place.
 *
 * Everything here builds DOM with `createElement` / `textContent`. There is
 * no `innerHTML` in this file and there must never be one: the dashboard
 * renders arbitrary model output, file contents and other people's session
 * transcripts. That is a security boundary, not a style preference.
 *
 * Colour is only ever applied through a `Tone` — never a hue — and a tone is
 * always accompanied by a shape (dot geometry), a word (pill label) or a
 * position (the row's left rail), so nothing depends on colour alone.
 */

export type Tone = "accent" | "ok" | "warn" | "bad" | "idle" | "dim";

/** Shorthand for the createElement/className/textContent triple. */
export function el<K extends keyof HTMLElementTagNameMap>(
  tag: K, className?: string, text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function toneClass(tone?: Tone | null): string {
  return tone ? `tone-${tone}` : "";
}

function cls(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(" ");
}

/** Swap the tone-* class on a node, leaving every other class alone. */
export function setTone(node: HTMLElement, tone: Tone | null): void {
  for (const t of ["accent", "ok", "warn", "bad", "idle", "dim"]) {
    node.classList.toggle(`tone-${t}`, tone === t);
  }
}

// ── State vocabulary ────────────────────────────────────────────────────────
// The six run states plus the seven session states, mapped once. Shape and
// label carry the meaning; tone only reinforces it.

type Shape = "live" | "pending" | "done" | "fault" | "void";

interface StateStyle { tone: Tone; shape: Shape; label: string }

const STATE_STYLE: Record<string, StateStyle> = {
  // runs
  queued:    { tone: "warn",   shape: "pending", label: "queued" },
  running:   { tone: "accent", shape: "live",    label: "running" },
  succeeded: { tone: "ok",     shape: "done",    label: "succeeded" },
  failed:    { tone: "bad",    shape: "fault",   label: "failed" },
  timed_out: { tone: "bad",    shape: "fault",   label: "timed out" },
  cancelled: { tone: "idle",   shape: "void",    label: "cancelled" },
  // sessions
  needs_you: { tone: "bad",    shape: "fault",   label: "needs you" },
  working:   { tone: "accent", shape: "live",    label: "working" },
  idle:      { tone: "ok",     shape: "done",    label: "idle" },
  shell:     { tone: "warn",   shape: "pending", label: "shell" },
  fresh:     { tone: "dim",    shape: "pending", label: "fresh" },
  gone:      { tone: "idle",   shape: "void",    label: "gone" },
  unknown:   { tone: "idle",   shape: "void",    label: "unknown" },
};

export function stateStyle(state: string): StateStyle {
  return STATE_STYLE[state] ?? { tone: "idle", shape: "void", label: state };
}

/** The state glyph. Shape distinguishes the state with colour switched off. */
export function statusDot(state: string): HTMLElement {
  const s = stateStyle(state);
  const node = el("span", cls("dot", `dot--${s.shape}`, toneClass(s.tone)));
  node.setAttribute("aria-hidden", "true");
  return node;
}

/** The state word. Always render this (or equivalent text) next to a dot. */
export function statusPill(state: string): HTMLElement {
  const s = stateStyle(state);
  return pill(s.label, s.tone);
}

// ── Primitives ──────────────────────────────────────────────────────────────

export interface PanelParts {
  /** The <section class="panel"> itself. */
  root: HTMLElement;
  head: HTMLElement;
  /** Append view content here. */
  body: HTMLElement;
  /** Right-hand count/summary in the head; "" hides it. */
  setMeta(text: string): void;
  setTitle(text: string): void;
}

/**
 * PANEL — the titled holographic frame. The container every view is made of.
 * `alert` gives it a tone-coloured head for things that want the user.
 */
export function panel(title: string, opts: {
  meta?: string; tone?: Tone; alert?: boolean; flush?: boolean; quiet?: boolean;
} = {}): PanelParts {
  const root = el("section", cls(
    "panel",
    opts.alert && "panel--alert",
    opts.flush && "panel--flush",
    opts.quiet && "panel--quiet",
    toneClass(opts.tone),
  ));
  const head = el("header", "panel-head");
  const titleEl = el("h2", "panel-title", title);
  const meta = el("span", "panel-meta", opts.meta ?? "");
  meta.hidden = !opts.meta;
  head.append(titleEl, meta);

  const body = el("div", "panel-body");
  root.append(head, body);

  return {
    root, head, body,
    setMeta(text: string) { meta.textContent = text; meta.hidden = text === ""; },
    setTitle(text: string) { titleEl.textContent = text; },
  };
}

export interface ReadoutParts {
  root: HTMLElement;
  /** Sets the value, flashing it only when it actually changed. */
  set(value: string, tone?: Tone | null): void;
}

/**
 * READOUT — a labelled number. The stat blocks in the masthead are these.
 * Tone is opt-in: an untoned readout stays plain white, because most numbers
 * carry no state.
 */
export function readout(label: string, value: string, opts: {
  tone?: Tone; size?: "sm" | "md" | "lg"; unit?: string; align?: "start" | "end";
} = {}): ReadoutParts {
  const root = el("div", cls(
    "readout",
    opts.size === "lg" && "readout--lg",
    opts.size === "sm" && "readout--sm",
    opts.align === "start" && "readout--start",
    toneClass(opts.tone),
  ));
  const valueEl = el("div", "readout-value");
  const valueText = document.createTextNode(value);
  valueEl.append(valueText);
  if (opts.unit) valueEl.append(el("span", "readout-unit", opts.unit));
  root.append(valueEl, el("div", "readout-label", label));

  return {
    root,
    set(next: string, tone?: Tone | null) {
      if (tone !== undefined) setTone(root, tone);
      if (valueText.data === next) return;
      valueText.data = next;
      flashValue(valueEl);
    },
  };
}

export interface BarParts {
  root: HTMLElement;
  /** `null` switches the gauge to indeterminate (running, quantity unknown). */
  set(pct: number | null, valueText?: string): void;
  /**
   * There is no measurement. The track is hatched and the value reads as
   * words, because an empty gauge and a 0% gauge look identical and one of
   * them is a lie. Use this for a quantity nobody has measured yet — never
   * `set(0)`.
   */
  unknown(valueText?: string): void;
  /** The reading is real but old. Keeps the number, drains the fill. */
  setStale(stale: boolean): void;
}

/**
 * BAR — a horizontal gauge with a percentage. Build progress, budget burn,
 * context used: anything that is a fraction of a known whole.
 */
export function bar(label: string, opts: {
  tone?: Tone; slim?: boolean; pct?: number | null; value?: string;
} = {}): BarParts {
  const root = el("div", cls("bar", opts.slim && "bar--slim", toneClass(opts.tone)));
  root.setAttribute("role", "progressbar");
  root.setAttribute("aria-valuemin", "0");
  root.setAttribute("aria-valuemax", "100");

  const head = el("div", "bar-head");
  const valueEl = el("span", "bar-value", "");
  head.append(el("span", "bar-label", label), valueEl);

  const track = el("div", "bar-track");
  const fill = el("div", "bar-fill");
  track.append(fill);
  root.append(head, track);

  const api: BarParts = {
    root,
    set(pct: number | null, valueText?: string) {
      root.classList.remove("bar--unknown");
      root.removeAttribute("aria-valuetext");
      if (pct === null) {
        root.classList.add("bar--indeterminate");
        root.removeAttribute("aria-valuenow");
        fill.style.removeProperty("--pct");
        valueEl.textContent = valueText ?? "";
        return;
      }
      const clamped = Math.max(0, Math.min(100, pct));
      root.classList.remove("bar--indeterminate");
      root.setAttribute("aria-valuenow", String(Math.round(clamped)));
      fill.style.setProperty("--pct", `${clamped}%`);
      valueEl.textContent = valueText ?? `${Math.round(clamped)}%`;
    },
    unknown(valueText?: string) {
      const text = valueText ?? "not measured";
      root.classList.add("bar--unknown");
      root.classList.remove("bar--indeterminate");
      // No aria-valuenow at all: a screen reader must not be told a number
      // either. aria-valuetext carries the words instead.
      root.removeAttribute("aria-valuenow");
      root.setAttribute("aria-valuetext", text);
      fill.style.setProperty("--pct", "0%");
      valueEl.textContent = text;
    },
    setStale(stale: boolean) { root.classList.toggle("bar--stale", stale); },
  };
  api.set(opts.pct ?? 0, opts.value);
  return api;
}

export interface RowParts {
  root: HTMLElement;
  /** Column 1: the state dot, usually. */
  setLead(node: Node): void;
  setTitle(text: string): void;
  setSub(text: string): void;
  /** Column 3, right-aligned. `w` locks a width so metrics align down a list. */
  addMeta(text: string, opts?: { w?: string; cls?: string; strong?: boolean }): HTMLElement;
  /** Column 3, for a pill or a button rather than a number. */
  addTrail(node: Node): void;
  /** A second line under the main one: mono, dim, single line, truncated. */
  setNote(text: string): void;
  /** A block under the main line, spanning to the right edge. */
  addBody(node: Node): void;
}

/**
 * ROW — one record in a list. Runs, sessions and memory files are all rows,
 * which is what makes them read as one system.
 */
export function row(opts: {
  tone?: Tone; attn?: boolean; dim?: boolean; onOpen?: () => void; label?: string;
} = {}): RowParts {
  const root = el("div", cls(
    "row",
    opts.onOpen && "row--action",
    opts.attn && "row--attn",
    opts.dim && "row--dim",
    toneClass(opts.tone),
  ));

  const lead = el("span", "row-lead");
  const main = el("span", "row-main");
  const titleEl = el("span", "row-title");
  const subEl = el("span", "row-sub");
  subEl.hidden = true;
  main.append(titleEl, subEl);
  const trail = el("span", "row-trail");
  root.append(lead, main, trail);

  if (opts.onOpen) {
    const open = opts.onOpen;
    root.tabIndex = 0;
    root.setAttribute("role", "button");
    if (opts.label) root.setAttribute("aria-label", opts.label);
    root.addEventListener("click", open);
    root.addEventListener("keydown", (ev: KeyboardEvent) => {
      if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); open(); }
    });
  }

  let note: HTMLElement | null = null;

  return {
    root,
    setLead(node: Node) { lead.replaceChildren(node); },
    setTitle(text: string) { titleEl.textContent = text; },
    setSub(text: string) { subEl.textContent = text; subEl.hidden = text === ""; },
    addMeta(text, metaOpts = {}) {
      const node = el("span", cls("row-meta", metaOpts.cls, metaOpts.strong && "is-strong"), text);
      if (metaOpts.w) node.style.setProperty("--w", metaOpts.w);
      trail.append(node);
      return node;
    },
    addTrail(node: Node) { trail.append(node); },
    setNote(text: string) {
      if (!note) { note = el("div", "row-note"); root.append(note); }
      note.textContent = text;
      note.hidden = text === "";
    },
    addBody(node: Node) {
      const wrap = el("div", "row-body");
      wrap.append(node);
      root.append(wrap);
    },
  };
}

/** PILL — a short state word in a bordered capsule. */
export function pill(text: string, tone?: Tone, variant?: "ghost" | "solid"): HTMLElement {
  return el("span", cls("pill", variant && `pill--${variant}`, toneClass(tone)), text);
}

/** EMPTY — a calm "nothing here". Never an error. */
export function emptyState(text: string, small = false): HTMLElement {
  return el("div", cls("empty", small && "empty--sm"), text);
}

/** BUTTON — bare <button> is styled identically; this just wires the click. */
export function button(label: string, onClick: () => void, opts: {
  tone?: Tone; quiet?: boolean;
} = {}): HTMLButtonElement {
  const b = el("button", cls(opts.quiet && "btn--quiet", toneClass(opts.tone)), label);
  b.type = "button";
  b.addEventListener("click", onClick);
  return b;
}

// ── Change signalling ───────────────────────────────────────────────────────

/** Flash a whole record: use when a row's STATE changed (a run just failed). */
export function flash(node: HTMLElement, tone?: Tone): void {
  if (tone) setTone(node, tone);
  restart(node, "flash");
}

/** Flash a single value: use when a number ticked to something new. */
export function flashValue(node: HTMLElement): void {
  restart(node, "flash-value");
}

function restart(node: HTMLElement, className: string): void {
  node.classList.remove(className);
  void node.offsetWidth; // force a reflow so the animation replays
  node.classList.add(className);
  node.addEventListener("animationend", () => node.classList.remove(className), { once: true });
}

// ── Callout & split ─────────────────────────────────────────────────────────

export interface CalloutParts {
  root: HTMLElement;
  /** The sentence a reader must not miss. */
  body: HTMLElement;
  /** Pills and a second line under it; hidden until something is put in it. */
  foot: HTMLElement;
  setMeta(text: string): void;
}

/**
 * CALLOUT — one toned band that says, in words, the single most important
 * thing about the record below it.
 *
 * `loud` floods the band with the tone and is reserved for a state that is
 * costing the user something right now (a session blocked on them). Every
 * other state gets the quiet form, so the loud one still means something
 * when it appears.
 *
 * Tone is never the only signal: `lead` carries a dot whose SHAPE says the
 * state with colour switched off, and `label` says it in words.
 */
export function callout(opts: {
  tone?: Tone; label: string; lead?: Node; meta?: string; loud?: boolean;
}): CalloutParts {
  const root = el("div", cls("callout", opts.loud && "callout--loud", toneClass(opts.tone)));
  const head = el("div", "callout-head");
  if (opts.lead) head.append(opts.lead);
  head.append(el("span", "callout-label", opts.label));
  const meta = el("span", "callout-meta", opts.meta ?? "");
  meta.hidden = !opts.meta;
  head.append(meta);

  const body = el("p", "callout-body");
  const foot = el("div", "callout-foot");
  foot.hidden = true;
  root.append(head, body, foot);

  return {
    root, body, foot,
    setMeta(text: string) { meta.textContent = text; meta.hidden = text === ""; },
  };
}

export interface SplitParts {
  /** The `<div class="split">` grid. Put it in the view. */
  root: HTMLElement;
  /** Left column: the list. */
  master: HTMLElement;
  /** Right column: whatever the list has selected. */
  detail: HTMLElement;
  /** Show a selection. Animates in; marks the split open. */
  show(node: Node): void;
  /** Show the standing content for "nothing selected". No animation, and the
   * split reads as closed — which is what puts the narrow sheet away. */
  rest(node: Node): void;
  /**
   * Same selection, fresher content. Keeps the scroll position and does not
   * replay the entrance, because a live view repaints on a tick and on every
   * socket event: `show` on each of those would strobe the panel and throw
   * the reader back to the top of it several times a minute.
   */
  update(node: Node): void;
  isOpen(): boolean;
}

/**
 * SPLIT — the master-detail layout, built once so Projects, Specs and
 * Sessions are one arrangement rather than three lookalikes.
 *
 * BOTH COLUMNS ARE ALWAYS IN THE GRID. Selecting something swaps the detail
 * column's contents; it never changes the layout, so the list under the
 * cursor does not move while you are working down it.
 *
 * `dismiss` turns the detail into something you can put away: it wires
 * Escape, and on a narrow window lifts the detail into a right-hand sheet
 * over a scrim that dismisses on a tap. (The close control itself is the
 * caller's, because only the caller knows where its head is.) Omit `dismiss`
 * for a split that always has a selection — Projects and Specs — where
 * "closed" would be a state with nothing to show.
 */
export function splitView(opts: {
  /** "index" (default): a narrow list beside a wide document.
   *  "aside": a wide list beside a narrow inspector. */
  shape?: "index" | "aside";
  /** Which column stays put while the other scrolls. */
  stick?: "master" | "detail";
  dismiss?: () => void;
} = {}): SplitParts {
  const root = el("div", cls(
    "split",
    opts.shape === "aside" && "split--aside",
    opts.dismiss && "split--sheet",
    opts.dismiss && "is-closed",
  ));
  const master = el("div", cls("split-master", opts.stick === "master" && "split-stick"));
  const detail = el("div", cls("split-detail", opts.stick === "detail" && "split-stick"));

  // The scrim only exists for sheet mode; docked, it is `display: none` and
  // can never swallow a click.
  const scrim = el("div", "split-scrim");
  const dismiss = opts.dismiss;
  root.append(scrim, master, detail);

  /**
   * On a narrow window the detail leaves the grid and becomes a right-hand
   * sheet — and it has to leave the DOM there too, not just move visually.
   * `.shell-main` is `position: relative; z-index: 1`, which makes it a
   * stacking context: NOTHING inside it can paint above the sticky masthead
   * (z-index 10), so a `position: fixed` sheet left in place slid in with its
   * own head hidden behind the header. Reparenting to <body> is the fix, and
   * it is what the run detail pane has always done by being a body-level
   * <aside> in the first place.
   */
  const narrow = window.matchMedia("(max-width: 900px)");
  let watching = false;
  function place(): void {
    if (!dismiss) return;
    const sheet = narrow.matches;
    scrim.classList.toggle("is-sheet", sheet);
    detail.classList.toggle("is-sheet", sheet);
    const host = sheet ? document.body : root;
    if (scrim.parentElement !== host) host.append(scrim);
    if (detail.parentElement !== host) host.append(detail);

    // A sheet lives on <body>, so it would outlive its own view: hiding the
    // view (which is how the tab strip switches) would leave it hanging over
    // whatever came next. Wired on the first placement rather than at
    // construction, because a split is built before it is put in its view and
    // `closest` would find nothing.
    if (!watching && root.isConnected) {
      const view = root.closest<HTMLElement>(".view");
      if (view) {
        watching = true;
        new MutationObserver(() => { if (view.hidden) dismiss(); })
          .observe(view, { attributes: true, attributeFilter: ["hidden"] });
      }
    }
  }

  if (dismiss) {
    scrim.addEventListener("click", dismiss);
    document.addEventListener("keydown", (ev: KeyboardEvent) => {
      if (ev.key !== "Escape") return;
      if (!root.isConnected || root.classList.contains("is-closed")) return;
      // A view switched away from is `hidden`, and Escape is not its business
      // then — offsetParent is null for anything display:none.
      if (root.offsetParent === null) return;
      ev.preventDefault();
      dismiss();
    });
    narrow.addEventListener("change", place);
    place();
  }

  function setOpen(open: boolean): void {
    root.classList.toggle("is-closed", !open);
    // The sheet is detached from `root`, so it carries the state itself.
    detail.classList.toggle("is-open", open);
    scrim.classList.toggle("is-open", open);
  }

  return {
    root, master, detail,
    show(node: Node) {
      place();
      setOpen(true);
      if (node instanceof HTMLElement) node.classList.add("split-enter");
      detail.replaceChildren(node);
      // A panel that opens half-scrolled is a panel whose first line — the
      // one that says whether you are wanted — is the one you cannot see.
      detail.scrollTop = 0;
    },
    rest(node: Node) {
      place();
      setOpen(false);
      detail.replaceChildren(node);
      detail.scrollTop = 0;
    },
    update(node: Node) {
      const at = detail.scrollTop;
      detail.replaceChildren(node);
      detail.scrollTop = at;
    },
    isOpen() { return !root.classList.contains("is-closed"); },
  };
}

/** A vertical stack of rows. */
export function stack(tight = false): HTMLElement {
  return el("div", cls("stack", tight && "stack--tight"));
}

/** A sub-grouping inside a panel body: a titled, counted band of rows. */
export function group(name: string, count?: string): { root: HTMLElement; body: HTMLElement } {
  const root = el("div", "group");
  const head = el("div", "group-head");
  head.append(el("span", "group-name", name));
  if (count !== undefined) head.append(el("span", "group-count", count));
  const body = stack();
  root.append(head, body);
  return { root, body };
}

/**
 * KV — a key/value detail list. The CSS for `.kv` has always been there;
 * this is the builder, so views stop hand-assembling `<dl>`s.
 */
export function kv(pairs: [string, string][] = []): {
  root: HTMLElement; add(key: string, value: string, tone?: Tone): HTMLElement;
} {
  const root = el("dl", "kv");
  const api = {
    root,
    add(key: string, value: string, tone?: Tone): HTMLElement {
      const dd = el("dd", toneClass(tone) || undefined, value);
      root.append(el("dt", undefined, key), dd);
      return dd;
    },
  };
  for (const [k, v] of pairs) api.add(k, v);
  return api;
}

export interface SparkPoint { label: string; value: number }

export interface SparklineParts {
  root: HTMLElement;
  /** Redraw. An empty array draws the "no history" state — never a flat
   * line along the bottom, which is what "every day was zero" looks like. */
  set(points: SparkPoint[]): void;
}

/**
 * SPARKLINE — a small history, drawn as COLUMNS rather than a line.
 *
 * Columns, deliberately: these are discrete daily totals, and a line drawn
 * between two days asserts values for the hours in between that nobody
 * measured. A day with no data is a hairline gap in the row, not a dip to
 * zero — same reason `bar` has `unknown()`.
 *
 * Hand-authored SVG via createElementNS with attributes set one at a time:
 * no charting library, and no markup string anywhere near this data. The
 * per-column `<title>` is the browser's own tooltip, not rendered markup.
 */
export function sparkline(label: string, opts: {
  tone?: Tone; height?: number; max?: number;
} = {}): SparklineParts {
  const NS = "http://www.w3.org/2000/svg";
  const height = opts.height ?? 44;

  const root = el("div", cls("spark", toneClass(opts.tone)));
  const head = el("div", "spark-head");
  const range = el("span", "spark-range", "");
  head.append(el("span", "spark-label", label), range);

  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("class", "spark-svg");
  svg.setAttribute("preserveAspectRatio", "none");
  svg.setAttribute("role", "img");
  svg.setAttribute("focusable", "false");
  root.append(head, svg);

  return {
    root,
    set(points: SparkPoint[]) {
      svg.replaceChildren();
      const n = points.length;
      if (n === 0) {
        root.classList.add("spark--empty");
        svg.removeAttribute("aria-label");
        range.textContent = "no history";
        return;
      }
      root.classList.remove("spark--empty");

      // A fixed viewBox with one slot per point; CSS stretches it to
      // whatever width the panel gives, so nothing has to be measured.
      const slot = 4;
      const width = n * slot;
      svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
      svg.setAttribute("height", String(height));

      const peak = Math.max(opts.max ?? 0, ...points.map((p) => p.value), 1);
      let peakAt = 0;
      for (let i = 0; i < n; i++) {
        const p = points[i];
        if (p.value > points[peakAt].value) peakAt = i;
        const h = p.value > 0 ? Math.max(1, (p.value / peak) * (height - 2)) : 0;
        const rect = document.createElementNS(NS, "rect");
        rect.setAttribute("class", h > 0 ? "spark-bar" : "spark-bar is-gap");
        rect.setAttribute("x", String(i * slot));
        rect.setAttribute("y", String(height - (h || 1)));
        rect.setAttribute("width", String(slot - 1));
        rect.setAttribute("height", String(h || 1));
        const tip = document.createElementNS(NS, "title");
        tip.textContent = `${p.label}: ${p.value.toLocaleString()}`;
        rect.append(tip);
        svg.append(rect);
      }
      svg.setAttribute(
        "aria-label",
        `${label}: ${n} day${n === 1 ? "" : "s"} to ${points[n - 1].label}, `
        + `highest ${points[peakAt].value.toLocaleString()} on ${points[peakAt].label}`);
      range.textContent = n === 1 ? points[0].label
        : `${points[0].label} — ${points[n - 1].label}`;
    },
  };
}
