/**
 * The results panel: what JARVIS looked up, shown on this page.
 *
 * Filled by a `display` frame from the server (the brain's `show_results`
 * tool). Everything in it came off the open web, so it is rendered as TEXT
 * only — no innerHTML anywhere — and a link is made only from an http(s)
 * address, opened in a new tab with no opener and no referrer.
 */

interface DisplayItem {
  title?: string;
  url?: string;
  host?: string;
  snippet?: string;
}

let panel: HTMLElement | null = null;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text) e.textContent = text;
  return e;
}

function safeUrl(raw: unknown): string | null {
  if (typeof raw !== "string") return null;
  try {
    const u = new URL(raw);
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : null;
  } catch {
    return null;
  }
}

export function hideDisplay() {
  panel?.remove();
  panel = null;
}

export function showDisplay(msg: Record<string, unknown>) {
  hideDisplay();
  panel = el("aside");
  panel.id = "display-panel";
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-label", "Results");
  // Clicks inside must not reach the document handler that closes the menu.
  panel.addEventListener("click", (e) => e.stopPropagation());

  const head = el("div", "dp-head");
  head.appendChild(el("h2", "dp-title", String(msg.title ?? "Results")));
  const close = el("button", "dp-close", "×");
  close.type = "button";
  close.title = "Close";
  close.setAttribute("aria-label", "Close results");
  close.addEventListener("click", hideDisplay);
  head.appendChild(close);
  panel.appendChild(head);

  const body = el("div", "dp-body");

  const text = typeof msg.text === "string" ? msg.text.trim() : "";
  if (text) {
    const block = el("div", "dp-text");
    for (const para of text.split(/\n{2,}/)) {
      const lines = para.split("\n");
      if (lines.every((l) => /^\s*[-*•]\s+/.test(l))) {
        const ul = el("ul");
        for (const l of lines) ul.appendChild(el("li", undefined, l.replace(/^\s*[-*•]\s+/, "")));
        block.appendChild(ul);
      } else {
        block.appendChild(el("p", undefined, para));
      }
    }
    body.appendChild(block);
  }

  const items = Array.isArray(msg.items) ? (msg.items as DisplayItem[]) : [];
  if (items.length) {
    const list = el("ol", "dp-items");
    for (const it of items) {
      const li = el("li", "dp-item");
      const url = safeUrl(it.url);
      const title = String(it.title ?? "").trim() || "Result";
      if (url) {
        const a = el("a", "dp-link", title);
        a.href = url;
        a.target = "_blank";
        a.rel = "noopener noreferrer";
        li.appendChild(a);
        li.appendChild(el("div", "dp-host", it.host ? String(it.host) : new URL(url).host));
      } else {
        li.appendChild(el("div", "dp-link", title));
      }
      if (it.snippet) li.appendChild(el("p", "dp-snippet", String(it.snippet)));
      list.appendChild(li);
    }
    body.appendChild(list);
  }

  panel.appendChild(body);
  document.body.appendChild(panel);
}

window.addEventListener("keydown", (e: KeyboardEvent) => {
  // Escape hushes him first (main.ts); with nothing playing it closes the panel.
  if (e.key === "Escape" && panel && !document.getElementById("hush")?.offsetParent) hideDisplay();
});
