// WebSocket client to the GPIO button daemon. The daemon is the server on
// 127.0.0.1:8889; the app connects and reacts to {"cmd":"toggle"|"call"|"hangup"}.
// Auto-reconnects with capped backoff — the daemon may start after the app, or
// restart under it, and the frame runs for days.
const WS_URL = 'ws://127.0.0.1:8889';
const MAX_BACKOFF = 5000;

export function connectControl(handlers) {
  let ws;
  let backoff = 250;
  let closed = false;

  function open() {
    if (closed) return;
    ws = new WebSocket(WS_URL);
    ws.onopen = () => { backoff = 250; if (handlers.onStatus) handlers.onStatus(true); };
    ws.onmessage = (e) => {
      let cmd;
      try { cmd = JSON.parse(e.data).cmd; } catch (_) { cmd = String(e.data).trim(); }
      if (cmd) handlers.onCommand(cmd);
    };
    ws.onclose = () => {
      if (handlers.onStatus) handlers.onStatus(false);
      if (!closed) { setTimeout(open, backoff); backoff = Math.min(backoff * 2, MAX_BACKOFF); }
    };
    ws.onerror = () => { try { ws.close(); } catch (_) {} };
  }

  open();
  return { close() { closed = true; try { if (ws) ws.close(); } catch (_) {} } };
}
