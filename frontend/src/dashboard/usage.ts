/**
 * The Usage view: what the subscription has left, and who spent it.
 *
 * THE UNIT IS NOT DOLLARS. JARVIS runs on the user's Claude subscription —
 * `claude_env` scrubs every ANTHROPIC_* variable so the CLI can never bill a
 * key — so a cost figure here is a price list for API calls nobody made. It
 * was the largest number on the masthead this morning and was removed for
 * exactly that reason. Tokens and share-of-limit are the honest units. The
 * one dollar figure on this page sits in JARVIS's own runs panel, small,
 * labelled as an API-list equivalent, and never as a headline.
 *
 * THE OTHER RULE: never render a number we do not have. Three separate
 * sources feed this view and each can be absent on its own —
 *
 *   /api/usage/limits    the subscription windows. Only exists once the
 *                        brain has taken a turn.
 *   /api/usage/sessions  the transcript scan. Only exists if there are
 *                        transcripts on disk.
 *   /api/runs/stats      JARVIS's own runs.
 *
 * — so each panel says what it does not know, in words, rather than
 * borrowing another panel's confidence.
 *
 * Everything below is read off someone's disk: project names, model ids,
 * subagent prompts. Every one of them goes through textContent.
 */
import {
  getUsageLimits, getUsageSessions, getStats,
  type AgentUsage, type RunStats, type SessionUsage, type Tokens,
  type UsageSessions, type UsageSnapshot,
} from "./api";
import {
  el, bar, pill, panel, readout, row, sparkline, stack, emptyState, setTone,
  type SparklineParts, type Tone,
} from "./ui";

let limits: UsageSnapshot | null | undefined;    // undefined = not fetched yet
let scan: UsageSessions | null | undefined;
let runs: RunStats | null | undefined;
let scanError = "";
let started = false;

/** How many conversations the list shows before it stops being a list. */
const SESSION_ROWS = 25;

// ── formatting ──────────────────────────────────────────────────────────────

/** 812 · 12.4k · 3.1M · 17.0B. Tabular, so a ticking number stays put. */
function fmtCount(n: number): string {
  if (!Number.isFinite(n)) return "—";
  if (n < 1000) return String(Math.round(n));
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
  if (n < 1_000_000_000) return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0)}M`;
  return `${(n / 1_000_000_000).toFixed(1)}B`;
}

function fmtAge(sec: number): string {
  if (sec < 45) return "just now";
  if (sec < 3600) return `${Math.round(sec / 60)}m ago`;
  if (sec < 36 * 3600) return `${Math.round(sec / 3600)}h ago`;
  return `${Math.round(sec / 86_400)}d ago`;
}

function fmtTime(d: Date): string {
  return d
    .toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
    .replace(/\s?([AaPp])\.?[Mm]\.?$/, (_m, half: string) => `${half.toLowerCase()}m`);
}

function fmtWhen(epochSec: number, now = new Date()): string {
  const d = new Date(epochSec * 1000);
  if (d.toDateString() === now.toDateString()) return fmtTime(d);
  const days = Math.abs(d.getTime() - now.getTime()) / 86_400_000;
  if (days < 6) return `${d.toLocaleDateString([], { weekday: "short" })} ${fmtTime(d)}`;
  return `${d.toLocaleDateString([], { day: "numeric", month: "short" })} ${fmtTime(d)}`;
}

/** "12 Sep" from a YYYY-MM-DD day key, for the sparkline's ends. */
function fmtDay(day: string): string {
  const [y, m, d] = day.split("-").map(Number);
  if (!y || !m || !d) return day;
  return new Date(y, m - 1, d)
    .toLocaleDateString([], { day: "numeric", month: "short" });
}

/** The CLI warns at 75% of a window; past 90% the next long run is the one
 * that gets cut off. Those are the two lines — same as the masthead. */
function usageTone(pct: number): Tone {
  if (pct >= 90) return "bad";
  if (pct >= 75) return "warn";
  return "accent";
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}

// ── section 1: the subscription's limits ────────────────────────────────────

/**
 * The windows the CLI actually reports — and a plain statement of what it
 * does NOT report, because the alternative is a reader assuming a missing
 * gauge means a limit that isn't being approached.
 */
function renderLimits(host: HTMLElement): void {
  host.replaceChildren();

  if (limits === undefined) {
    host.append(emptyState("Reading the subscription limits…"));
    return;
  }
  if (limits === null) {
    host.append(emptyState(
      "Cannot read the subscription limits. This is not the same as having "
      + "used none of them."));
    return;
  }

  const note = el("div", "usage-note");
  if (!limits.measured) {
    note.append(pill("no reading", "warn"));
    note.append(el("span", undefined,
      "JARVIS only learns where the windows stand while a turn is running, "
      + "and none has run since he started. Nothing has been measured — "
      + "which is not the same as nothing being used."));
  } else if (limits.stale) {
    note.append(pill("stale", "warn"));
    note.append(el("span", undefined,
      `Last measured ${fmtAge(limits.age_sec ?? 0)}. A five-hour window can `
      + "move a long way in that time."));
  } else {
    note.append(el("span", undefined, `Measured ${fmtAge(limits.age_sec ?? 0)}.`));
  }
  host.append(note);

  const gauges = el("div", "usage-window-grid");
  for (const w of limits.windows) {
    const b = bar(w.label, { slim: false });
    const usable = w.utilization !== null && !w.expired;
    if (usable) {
      const pct = w.utilization as number;
      setTone(b.root, usageTone(pct));
      b.set(pct, `${Math.round(pct)}%`);
      b.setStale(w.stale);
    } else {
      setTone(b.root, "dim");
      b.unknown(w.expired ? "reset since" : "not measured");
    }

    const when = el("div", "usage-when");
    if (w.resets_at !== null) {
      when.textContent = w.expired
        ? `reset ${fmtWhen(w.resets_at)}` : `resets ${fmtWhen(w.resets_at)}`;
    } else {
      when.textContent = usable ? "reset time unknown" : "no reading yet";
    }

    const cell = el("div", "usage-window");
    cell.append(b.root, when);
    gauges.append(cell);
  }
  host.append(gauges);

  // What the payload contains, stated. The user asked for a "Fable limit";
  // there is no such window in the data, and inventing an empty gauge for
  // one would be worse than saying so.
  const named = limits.windows.map((w) => w.label).join(" and ");
  const contains = el("div", "usage-contains");
  contains.append(el("span", "usage-contains-label", "What the CLI reports"));
  contains.append(el("span", undefined, limits.windows.length === 0
    ? "No windows at all have been named yet."
    : `${limits.windows.length} window${limits.windows.length === 1 ? "" : "s"}`
      + `: ${named}. There is no separate per-model window — no Fable limit, `
      + `no Opus limit — in the rate-limit payload. What each model costs is `
      + `under By Model below, measured from the transcripts.`));
  host.append(contains);
}

// ── section 2: overall ──────────────────────────────────────────────────────

let spark: SparklineParts | null = null;

/** Clear the sparkline AND forget it, so a later good fetch rebuilds one.
 * Emptying the host while keeping the handle leaves a chart that can never
 * be drawn again. */
function clearSpark(host: HTMLElement): void {
  host.replaceChildren();
  spark = null;
}

function renderOverall(host: HTMLElement, sparkHost: HTMLElement): void {
  host.replaceChildren();

  if (scan === undefined) {
    host.append(emptyState("Reading the transcripts…"));
    clearSpark(sparkHost);
    return;
  }
  if (scan === null) {
    host.append(emptyState(scanError
      || "Cannot read the transcripts. Nothing here is a measurement."));
    clearSpark(sparkHost);
    return;
  }
  if (!scan.measured) {
    host.append(emptyState(
      "No Claude Code transcripts found on this machine, so there is nothing "
      + "to measure yet — not zero usage."));
    clearSpark(sparkHost);
    return;
  }

  const t = scan.totals;
  const figures = el("div", "usage-figures");
  figures.append(
    readout("Tokens, all time", fmtCount(t.total), { size: "lg" }).root,
    readout("Today", fmtCount(scan.today.total)).root,
    readout("Output", fmtCount(t.output)).root,
    readout("Conversations", String(scan.session_count)).root,
    readout("Projects", String(scan.project_count)).root,
  );
  host.append(figures);

  // The four counts, kept apart: cache reads are most of a long
  // conversation's input and rolling them into "input" misstates both.
  const split = el("div", "usage-split");
  for (const [label, value] of [
    ["fresh input", t.input], ["output", t.output],
    ["cache read", t.cache_read], ["cache written", t.cache_creation],
  ] as [string, number][]) {
    const cell = el("div", "usage-split-cell");
    cell.append(
      el("span", "usage-split-value", fmtCount(value)),
      el("span", "usage-split-label", label),
    );
    split.append(cell);
  }
  host.append(split);

  host.append(el("div", "usage-note", provenance(scan)));

  if (!spark) {
    spark = sparkline("Daily tokens", { tone: "accent" });
    sparkHost.replaceChildren(spark.root);
  }
  spark.set(scan.daily.map((d) => ({
    label: fmtDay(d.day), value: d.tokens.total,
  })));
}

function provenance(s: UsageSessions): string {
  const files = `${s.files.toLocaleString()} transcript${s.files === 1 ? "" : "s"}`;
  return `Read from ${files} across ${s.roots.length} config root`
    + `${s.roots.length === 1 ? "" : "s"}, ${fmtAge(Date.now() / 1000 - s.scanned_at)}. `
    + "Counted per message from the CLI's own usage blocks; the two roots are "
    + "hardlinked on this machine, so each file is counted once.";
}

// ── section 3: by model ─────────────────────────────────────────────────────

function renderModels(host: HTMLElement, meta: (t: string) => void): void {
  host.replaceChildren();
  if (!scan || !scan.measured || scan.models.length === 0) {
    meta("");
    host.append(emptyState(
      scan && scan.measured ? "No model reported any usage."
        : "Nothing read yet.", true));
    return;
  }
  meta(String(scan.models.length));

  const peak = Math.max(...scan.models.map((m) => m.tokens.total), 1);
  for (const m of scan.models) {
    const r = row({ tone: "accent" });
    r.setTitle(m.model || "(unnamed model)");
    r.addMeta(`${fmtCount(m.tokens.total)} tok`, { w: "78px", strong: true });
    r.addMeta(`${Math.round((m.tokens.total / peak) * 100)}%`, { w: "44px" });

    const share = bar("", { slim: true, tone: "accent" });
    share.set((m.tokens.total / peak) * 100);
    r.addBody(share.root);
    host.append(r.root);
  }
}

// ── section 4: per conversation ─────────────────────────────────────────────

function sessionRow(s: SessionUsage, peak: number): HTMLElement {
  const r = row({ tone: s.active_agents > 0 ? "accent" : "idle" });
  r.setTitle(s.project || "(unknown project)");
  r.setSub(shortId(s.session_id));

  if (s.active_agents > 0) {
    r.addTrail(pill(
      `${s.active_agents} agent${s.active_agents === 1 ? "" : "s"} active`,
      "accent"));
  } else if (s.agents.length > 0) {
    r.addTrail(pill(`${s.agents.length} dispatched`, "idle", "ghost"));
  }

  r.addMeta(`${fmtCount(s.total_tokens.total)} tok`, { w: "76px", strong: true });
  r.addMeta(s.agent_tokens.total > 0
    ? `${fmtCount(s.agent_tokens.total)} sub` : "—", { w: "72px" });
  // Absence, not zero: a conversation with no assistant turn has no context.
  r.addMeta(s.context_tokens === null
    ? "ctx unknown" : `${fmtCount(s.context_tokens)} ctx`, { w: "84px" });
  r.addMeta(s.last_at === null ? "—" : fmtWhen(s.last_at), { w: "66px" });

  const share = bar("", { slim: true, tone: s.active_agents > 0 ? "accent" : "idle" });
  share.set((s.total_tokens.total / peak) * 100);
  r.addBody(share.root);

  const live = s.agents.filter((a) => a.active);
  if (live.length > 0) r.setNote(`└ ${agentLine(live[0])}`);
  return r.root;
}

/** What an agent IS, in the fewest words it has. The sidecar's one-line
 * description if there is one, else the brief it was launched with, else
 * the model — never a guess, and never a bare hex id where anything else
 * exists. */
function agentName(a: AgentUsage): string {
  return a.description || a.prompt.slice(0, 120) || a.model || a.agent_id;
}

function agentLine(a: AgentUsage): string {
  return `${agentName(a)} · ${fmtCount(a.tokens.total)} tok`;
}

function renderSessions(host: HTMLElement, meta: (t: string) => void): void {
  host.replaceChildren();
  if (scan === undefined) { meta(""); host.append(emptyState("Reading…")); return; }
  if (scan === null) {
    meta("");
    host.append(emptyState("Cannot read the transcripts."));
    return;
  }
  if (scan.sessions.length === 0) {
    meta("");
    host.append(emptyState(scan.measured
      ? "No conversations of yours on this machine — only JARVIS's own work, "
        + "which is below."
      : "Nothing measured yet."));
    return;
  }
  meta(`${scan.session_count} in ${scan.project_count} projects`);

  // Ordered by what each conversation SPENT, not by when it was last
  // touched. The server returns them most-recent-first, which is the right
  // order for the Sessions tab and the wrong one here: on this machine that
  // buries a 2-billion-token conversation under a dozen one-turn ones.
  // `last active` stays a column, so recency is still readable.
  host.append(el("div", "usage-note",
    "Largest first. A conversation's own spend and its subagents' are "
    + "counted together here and split in the columns."));

  const ranked = [...scan.sessions]
    .sort((a, b) => b.total_tokens.total - a.total_tokens.total);
  const shown = ranked.slice(0, SESSION_ROWS);
  const peak = Math.max(...shown.map((s) => s.total_tokens.total), 1);
  for (const s of shown) host.append(sessionRow(s, peak));

  // "Smaller" is a claim about conversations that are not on the page, and
  // it is only true as far as `largest_listed` goes: the server guarantees
  // that many of the machine's biggest spenders are in the payload, and
  // beyond that it has no ranking to offer. It used to say "smaller" about
  // a thousand conversations that were merely OLDER, off a truncation by
  // recency the page then re-sorted by spend.
  if (scan.session_count > shown.length) {
    const rest = scan.session_count - shown.length;
    const proven = shown.length <= scan.largest_listed;
    host.append(el("div", "usage-more",
      proven
        ? `${rest} smaller conversations not listed. `
          + "The totals above count every one of them."
        : `${rest} other conversations not listed. `
          + "The totals above count every one of them."));
  }
}

// ── section 5: background work ──────────────────────────────────────────────

interface LiveAgent { agent: AgentUsage; session: SessionUsage }

function renderBackground(host: HTMLElement, meta: (t: string) => void): void {
  host.replaceChildren();
  if (!scan || !scan.measured) {
    meta("");
    host.append(emptyState("Nothing read yet.", true));
    return;
  }

  const live: LiveAgent[] = [];
  for (const s of [...scan.sessions, ...scan.own_sessions]) {
    for (const a of s.agents) if (a.active) live.push({ agent: a, session: s });
  }
  live.sort((a, b) => (b.agent.last_at ?? 0) - (a.agent.last_at ?? 0));

  const window = Math.round(scan.active_within_sec);
  meta(live.length > 0 ? String(live.length) : "");

  host.append(el("div", "usage-note",
    `"Active" here means a subagent's transcript was written in the last `
    + `${window} seconds. That is a file's age, not a live process — there is `
    + `no process to ask. A subagent that finished a minute ago has already `
    + `dropped off this list. The name and type come from the sidecar the CLI `
    + `writes at spawn; an agent without one shows the brief it was given `
    + `instead, and no type at all rather than a guessed one.`));

  if (live.length === 0) {
    host.append(emptyState(
      `Nothing dispatched in the last ${window} seconds.`, true));
    return;
  }

  const list = stack();
  for (const { agent, session } of live) {
    const r = row({ tone: "accent" });
    r.setLead(el("span", "dot dot--live tone-accent"));
    r.setTitle(agentName(agent));
    r.setSub(session.project || shortId(session.session_id));

    if (session.own) r.addTrail(pill("JARVIS's own", "idle", "ghost"));
    // An agent dispatched by another agent, not by the conversation. Only
    // shown when the sidecar actually said so — depth 0 means no sidecar,
    // which is unknown depth, not top level.
    if (agent.depth > 1) r.addTrail(pill(`nested ×${agent.depth}`, "warn", "ghost"));
    if (agent.agent_type) r.addTrail(pill(agent.agent_type, "idle", "ghost"));

    r.addMeta(`${fmtCount(agent.tokens.total)} tok`, { w: "78px", strong: true });
    r.addMeta(`${agent.turns} turn${agent.turns === 1 ? "" : "s"}`, { w: "58px" });
    r.addMeta(agent.last_at === null ? "—" : fmtWhen(agent.last_at), { w: "62px" });
    // The full brief under the one-line name, when they differ.
    if (agent.prompt && agent.prompt !== agent.description) {
      r.setNote(`└ ${agent.prompt}`);
    }
    list.append(r.root);
  }
  host.append(list);
}

// ── section 6: JARVIS's own machinery ───────────────────────────────────────

function renderOwn(host: HTMLElement): void {
  host.replaceChildren();
  host.append(el("div", "usage-note",
    "JARVIS's brain and the one-shot runs he spawns register as Claude Code "
    + "sessions like any other, and were once counted as the user's "
    + "conversations — 12 became 16. They are kept here so they are visible "
    + "without being mixed into the figures above."));

  if (scan && scan.measured) {
    const figures = el("div", "usage-figures");
    figures.append(
      readout("Tokens", fmtCount(scan.own_totals.total)).root,
      readout("Sessions", String(scan.own_session_count)).root,
    );
    host.append(figures);
  }

  if (runs === undefined) {
    host.append(emptyState("Reading the run log…", true));
    return;
  }
  if (runs === null) {
    host.append(emptyState("Cannot read the run log.", true));
    return;
  }

  const ok = runs.by_status.succeeded ?? 0;
  const bad = (runs.by_status.failed ?? 0) + (runs.by_status.timed_out ?? 0);
  const figures = el("div", "usage-figures");
  figures.append(
    readout("Runs today", String(runs.total_runs)).root,
    readout("Succeeded", String(ok), { tone: ok > 0 ? "ok" : undefined }).root,
    readout("Failed", String(bad), { tone: bad > 0 ? "bad" : "dim" }).root,
    readout("Run tokens",
      fmtCount(runs.total_input_tokens + runs.total_output_tokens)).root,
  );
  host.append(figures);

  // The only dollar figure on the page, and it is a footnote on purpose.
  host.append(el("div", "usage-footnote",
    `The CLI priced today's runs at $${runs.total_cost_usd.toFixed(2)} — that `
    + "is what these tokens WOULD have cost through the API. Nothing was "
    + "billed: JARVIS runs on the subscription and the brain scrubs every "
    + "ANTHROPIC_* variable so the CLI cannot reach a key. Treat it as a "
    + "sense of scale, not a bill."));
}

// ── paint ───────────────────────────────────────────────────────────────────

interface Sections {
  limits: HTMLElement; overall: HTMLElement; spark: HTMLElement;
  models: HTMLElement; modelsMeta: (t: string) => void;
  sessions: HTMLElement; sessionsMeta: (t: string) => void;
  background: HTMLElement; backgroundMeta: (t: string) => void;
  own: HTMLElement;
}

let sections: Sections | null = null;

function build(): Sections | null {
  if (sections) return sections;
  const host = document.getElementById("usage-view");
  if (!host) return null;

  const limitsPanel = panel("Subscription", { tone: "accent" });
  const overallPanel = panel("Overall");
  const sparkHost = el("div", "usage-spark");
  const modelsPanel = panel("By Model");
  const sessionsPanel = panel("By Conversation");
  const backgroundPanel = panel("Background & Subagents");
  const ownPanel = panel("JARVIS's Own Work", { quiet: true });

  const overallBody = el("div");
  overallPanel.body.replaceChildren(overallBody, sparkHost);
  const modelsBody = stack();
  modelsPanel.body.append(modelsBody);
  const sessionsBody = stack();
  sessionsPanel.body.append(sessionsBody);
  const backgroundBody = el("div");
  backgroundPanel.body.append(backgroundBody);

  host.replaceChildren(
    limitsPanel.root, overallPanel.root, modelsPanel.root,
    sessionsPanel.root, backgroundPanel.root, ownPanel.root,
  );

  sections = {
    limits: limitsPanel.body,
    overall: overallBody,
    spark: sparkHost,
    models: modelsBody, modelsMeta: modelsPanel.setMeta,
    sessions: sessionsBody, sessionsMeta: sessionsPanel.setMeta,
    background: backgroundBody, backgroundMeta: backgroundPanel.setMeta,
    own: ownPanel.body,
  };
  return sections;
}

function paint(): void {
  const s = build();
  if (!s) return;
  renderLimits(s.limits);
  renderOverall(s.overall, s.spark);
  renderModels(s.models, s.modelsMeta);
  renderSessions(s.sessions, s.sessionsMeta);
  renderBackground(s.background, s.backgroundMeta);
  renderOwn(s.own);
}

/**
 * Three fetches, each settled on its own. A failed transcript scan must not
 * blank the limit gauges, and a missing limit reading must not hide what the
 * conversations spent — they answer different questions from different
 * sources and only one of them may be broken.
 */
export async function refreshUsageView(): Promise<void> {
  const [limitsRes, scanRes, runsRes] = await Promise.allSettled([
    getUsageLimits(), getUsageSessions(), getStats("day"),
  ]);

  limits = limitsRes.status === "fulfilled" ? limitsRes.value : null;
  if (scanRes.status === "fulfilled") {
    scan = scanRes.value;
    scanError = "";
  } else {
    console.error("[usage] transcript scan failed", scanRes.reason);
    scan = null;
    scanError = "";
  }
  runs = runsRes.status === "fulfilled" ? runsRes.value : null;
  paint();
}

export function initUsage(): void {
  if (started) return;
  started = true;
  paint();                       // draw the "reading…" states immediately
  void refreshUsageView();
  // No socket backs any of this: the limit reading arrives when the brain
  // takes a turn, and the transcripts are written by other processes. So it
  // polls — and the ages on screen go on telling the truth about how old
  // the numbers are rather than freezing at whatever they said on load.
  setInterval(() => void refreshUsageView(), 60_000);
}
