/**
 * A small, deliberately partial Markdown renderer.
 *
 * It exists for one reason: the SPECS view renders documents written by a
 * language model, read off someone's disk, and the dashboard's rule is that
 * such content never touches `innerHTML`. Everything below is built with
 * `createElement` and `textContent`, so there is no parse path at all that
 * can produce markup — a `<script>` in a spec is characters on a page and
 * nothing else. A complete renderer that reached for `innerHTML` would be
 * the wrong trade; a partial one that cannot inject is the right one.
 *
 * No dependency, by constraint and by choice: what a spec or a plan actually
 * contains is headings, paragraphs, lists, checkboxes, code and emphasis.
 * That is the whole grammar here.
 *
 * WHAT IS SUPPORTED
 *   #..###### headings · paragraphs · fenced code (``` and ~~~)
 *   - / * / + and 1. lists, nested by indent · - [ ] / - [x] task items
 *   > blockquotes · --- rules · `inline code` · **bold** · *italic*
 *
 * WHAT IS NOT, and why
 *   Links. `[text](url)` stays as the characters it is: these documents are
 *   model-written, and an anchor is the one Markdown construct that can
 *   carry a destination. Not rendering one is a smaller loss than auditing
 *   every scheme.
 *   Underscore emphasis. `__slots__`, `snake_case` and `_private` are what
 *   these documents are FULL of, and treating those as emphasis mangles
 *   more text than it decorates. Asterisks only.
 *   Tables, footnotes, HTML blocks. Absent from specs; absent here.
 */

import { el } from "./ui";

/** Render Markdown into a fresh element. Never returns markup. */
export function markdown(text: string, className = "md"): HTMLElement {
  const root = el("div", className);
  for (const node of blocks(splitLines(text))) root.append(node);
  return root;
}

function splitLines(text: string): string[] {
  return (text ?? "").replace(/\r\n?/g, "\n").split("\n");
}

const FENCE = /^(\s*)(```|~~~)(.*)$/;
const HEADING = /^(#{1,6})\s+(.*?)\s*#*\s*$/;
const RULE = /^\s*([-*_])(\s*\1){2,}\s*$/;
const QUOTE = /^\s*>\s?(.*)$/;
const BULLET = /^(\s*)[-*+]\s+(.*)$/;
const NUMBER = /^(\s*)(\d{1,3})[.)]\s+(.*)$/;
const TASK = /^\[([ xX])\]\s*(.*)$/;

/** One pass over the lines, emitting a node per block. */
function blocks(lines: string[]): Node[] {
  const out: Node[] = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (!line.trim()) { i++; continue; }

    const fence = FENCE.exec(line);
    if (fence) {
      const marker = fence[2];
      const body: string[] = [];
      i++;
      while (i < lines.length && !lines[i].trimStart().startsWith(marker)) {
        body.push(lines[i]); i++;
      }
      i++; // the closing fence, or the end of the document
      const pre = el("pre", "md-code");
      // The language tag is not rendered as a class: it is model output, and
      // it has no business naming a stylesheet selector.
      pre.append(el("code", undefined, body.join("\n")));
      out.push(pre);
      continue;
    }

    if (RULE.test(line)) { out.push(el("hr", "md-rule")); i++; continue; }

    const heading = HEADING.exec(line);
    if (heading) {
      // h1..h6 are capped at h4 so a document heading can never out-shout
      // the panel it sits inside.
      const level = Math.min(6, Math.max(1, heading[1].length));
      const tag = (["h3", "h4", "h5", "h6", "h6", "h6"][level - 1]) as "h3";
      const node = el(tag, `md-h md-h${level}`);
      for (const part of inline(heading[2])) node.append(part);
      out.push(node);
      i++;
      continue;
    }

    if (QUOTE.test(line)) {
      const body: string[] = [];
      while (i < lines.length && QUOTE.test(lines[i])) {
        body.push(QUOTE.exec(lines[i])![1]); i++;
      }
      const quote = el("blockquote", "md-quote");
      for (const node of blocks(body)) quote.append(node);
      out.push(quote);
      continue;
    }

    if (BULLET.test(line) || NUMBER.test(line)) {
      const [list, next] = listAt(lines, i);
      out.push(list);
      i = next;
      continue;
    }

    // A paragraph: everything up to a blank line or the start of another
    // block. Soft line breaks inside it collapse, as Markdown does.
    const body: string[] = [];
    while (i < lines.length && lines[i].trim()
           && !FENCE.test(lines[i]) && !HEADING.test(lines[i])
           && !RULE.test(lines[i]) && !QUOTE.test(lines[i])
           && !BULLET.test(lines[i]) && !NUMBER.test(lines[i])) {
      body.push(lines[i].trim()); i++;
    }
    const p = el("p", "md-p");
    for (const part of inline(body.join(" "))) p.append(part);
    out.push(p);
  }

  return out;
}

/** Indentation width, tabs counted as four. */
function indentOf(text: string): number {
  return text.replace(/\t/g, "    ").length;
}

/**
 * One list starting at `start`, with anything indented under it nested
 * inside. Returns the list and the index of the first line after it.
 */
function listAt(lines: string[], start: number): [HTMLElement, number] {
  const first = BULLET.exec(lines[start]) ?? NUMBER.exec(lines[start])!;
  const ordered = NUMBER.test(lines[start]);
  const depth = indentOf(first[1]);
  const list = el(ordered ? "ol" : "ul", "md-list");

  let i = start;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) {
      // A blank line ends the list unless the next line continues it.
      const next = lines[i + 1] ?? "";
      const cont = BULLET.exec(next) ?? NUMBER.exec(next);
      if (!cont || indentOf(cont[1]) < depth) break;
      i++;
      continue;
    }
    const bullet = BULLET.exec(line);
    const numbered = bullet ? null : NUMBER.exec(line);
    const match = bullet ?? numbered;
    if (!match) break;
    const at = indentOf(match[1]);
    if (at < depth) break;
    if (at > depth) {
      const [nested, next] = listAt(lines, i);
      (list.lastElementChild ?? list).append(nested);
      i = next;
      continue;
    }
    // `1.` after `-` at the same indent is a NEW list, not another item of
    // this one. Without this the digit became the item's whole text.
    if ((numbered !== null) !== ordered) break;

    const raw = numbered ? match[3] : match[2];
    const item = el("li", "md-item");
    const task = TASK.exec(raw);
    if (task) {
      // A plan's checkboxes are its progress. Rendered as a state, with a
      // shape as well as a colour, and never as an input: nothing on this
      // page is editable — the answer comes back by voice.
      const done = task[1].toLowerCase() === "x";
      item.classList.add("md-task", done ? "is-done" : "is-open");
      const box = el("span", `md-box ${done ? "tone-ok" : "tone-dim"}`,
                     done ? "✓" : "");
      box.setAttribute("aria-hidden", "true");
      item.append(box);
      const label = el("span", "md-task-text");
      for (const part of inline(task[2])) label.append(part);
      item.append(label);
      item.setAttribute("aria-label", `${done ? "done" : "not done"}: ${task[2]}`);
    } else {
      for (const part of inline(raw)) item.append(part);
    }
    list.append(item);
    i++;
  }
  return [list, i];
}

// Code first so emphasis inside a span is left alone; then bold, so `**x**`
// is never read as two italics. Asterisks only — see the header.
const INLINE = /(`+)([\s\S]+?)\1|\*\*([\s\S]+?)\*\*/g;

/** Inline spans as text nodes and elements. Never markup. */
function inline(text: string): Node[] {
  const out: Node[] = [];
  let last = 0;
  INLINE.lastIndex = 0;
  for (let m = INLINE.exec(text); m; m = INLINE.exec(text)) {
    if (m.index > last) out.push(...emphasis(text.slice(last, m.index)));
    if (m[2] !== undefined) {
      out.push(el("code", "md-inline-code", m[2].trim()));
    } else {
      const strong = el("strong", "md-strong");
      for (const part of emphasis(m[3])) strong.append(part);
      out.push(strong);
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(...emphasis(text.slice(last)));
  return out;
}

/** `*italic*`, and nothing that looks like a bullet or a footnote marker. */
const ITALIC = /\*(?!\s)([^*\n]+?)(?<!\s)\*/g;

function emphasis(text: string): Node[] {
  const out: Node[] = [];
  let last = 0;
  ITALIC.lastIndex = 0;
  for (let m = ITALIC.exec(text); m; m = ITALIC.exec(text)) {
    if (m.index > last) out.push(document.createTextNode(text.slice(last, m.index)));
    out.push(el("em", "md-em", m[1]));
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(document.createTextNode(text.slice(last)));
  return out;
}
