/**
 * Live session updates.
 *
 * Same discipline as live.ts: the socket is a hint, never the source of
 * truth. A "snapshot" message on connect (or reconnect) triggers a full
 * reconcile against GET /api/sessions rather than trusting the payload it
 * carries, and a "event" message just patches the one session it names.
 */
import { createSocket } from "../ws";
import type { SessionRow } from "./api";

export interface SessionLiveHandlers {
  onReconcile: () => void;
  onSessionEvent: (kind: string, session: SessionRow) => void;
  onConnectionChange: (connected: boolean) => void;
}

export function connectSessionsLive(handlers: SessionLiveHandlers): void {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const socket = createSocket(`${proto}://${location.host}/ws/sessions`);

  socket.onMessage((msg) => {
    switch (msg.type) {
      case "snapshot":
        handlers.onConnectionChange(true);
        handlers.onReconcile();
        break;
      case "event":
        handlers.onSessionEvent(msg.kind as string, msg.session as SessionRow);
        break;
    }
  });

  // createSocket reconnects on its own; poll its state to surface a banner
  // and force a reconcile whenever the link comes back.
  let wasConnected = false;
  setInterval(() => {
    const now = socket.isConnected();
    if (now !== wasConnected) {
      handlers.onConnectionChange(now);
      if (now) handlers.onReconcile();
      wasConnected = now;
    }
  }, 1000);
}
