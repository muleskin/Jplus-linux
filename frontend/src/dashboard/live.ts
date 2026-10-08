/**
 * Live run updates.
 *
 * The socket is a hint, never the source of truth: every connect triggers a
 * full reconcile against /api/runs, and a gap in `seq` triggers a backfill.
 */
import { createSocket } from "../ws";
import type { RunRow } from "./api";

export interface LiveHandlers {
  onReconcile: () => void;
  onRunChanged: (run: RunRow) => void;
  onRunEvent: (runId: string, seq: number, kind: string, payload: unknown) => void;
  onConnectionChange: (connected: boolean) => void;
}

export function connectLive(handlers: LiveHandlers): void {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const socket = createSocket(`${proto}://${location.host}/ws/runs`);

  socket.onMessage((msg) => {
    switch (msg.type) {
      case "hello":
        handlers.onConnectionChange(true);
        handlers.onReconcile();
        break;
      case "run_started":
      case "run_updated":
      case "run_finished":
        handlers.onRunChanged(msg.run as RunRow);
        break;
      case "run_event":
        handlers.onRunEvent(
          msg.run_id as string,
          msg.seq as number,
          msg.kind as string,
          msg.payload,
        );
        break;
    }
  });

  // createSocket reconnects on its own; poll its state to surface the banner
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
