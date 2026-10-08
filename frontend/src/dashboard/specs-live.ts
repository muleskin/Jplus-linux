/**
 * Live hints for the SPECS view.
 *
 * Same discipline as live.ts and sessions-live.ts, and stricter: the message
 * carries no content whatsoever. A spec is revised by JARVIS writing to
 * disk, and an approval is a file appearing beside it — there is nothing to
 * push, so the server watches the files and says only that something moved.
 * Every message means one thing: reconcile against /api/specs.
 */
import { createSocket } from "../ws";

export interface SpecsLiveHandlers {
  onReconcile: () => void;
  onConnectionChange: (connected: boolean) => void;
}

export function connectSpecsLive(handlers: SpecsLiveHandlers): void {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const socket = createSocket(`${proto}://${location.host}/ws/specs`);

  socket.onMessage((msg) => {
    // "hello" and "changed" mean the same thing to a client that never
    // trusts the socket for content: go and read the truth.
    if (msg.type === "hello" || msg.type === "changed") {
      if (msg.type === "hello") handlers.onConnectionChange(true);
      handlers.onReconcile();
    }
  });

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
