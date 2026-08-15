// The browser half of version2.md §2.7's browser stage.
//
// Three jobs, in order of how badly they are needed:
//
//   1. Say the page rendered. The agent starts the GUI and then requires a check-in over this
//      channel within a short deadline; without one it tears the graphical session down and goes
//      back to narrating on the console, because a blank or broken desktop is never acceptable.
//      The check-in is sent after the first animation frame following `load`, so it means "this
//      document painted", not merely "a script ran".
//
//   2. Keep saying it. §2.10's kiosk liveness watches this channel for 90 s of silence, because an
//      OOM-killed renderer leaves an "Aw, Snap!" tab while systemd still reports the unit active —
//      `Restart=` never fires, and the dropped channel is the only honest signal. The heartbeat
//      rides `requestAnimationFrame`, so it stops when the document stops, which is the property
//      that makes the signal worth anything.
//
//   3. Render the agent's screen. When the agent says the product must not run, this puts the
//      repair narration on top of everything — what was detected, why it matters, what is being
//      done, the attempt number, and the countdown with its "Reboot now" button.
//
// No imports on purpose. This file must keep working when the rest of the app does not, and a
// broken vendor bundle must not be able to take the liveness signal with it.

const HEARTBEAT_MS = 15000;
const RECONNECT_MIN_MS = 500;
const RECONNECT_MAX_MS = 5000;

let socket = null;
let backoff = RECONNECT_MIN_MS;
let configuration = null;
let lastBeat = 0;
let painted = false;

function send(message) {
  try {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
  } catch (_) { /* the channel is gone; the agent's silence timer is what notices */ }
}

function report(kind) {
  send({
    kind,
    identity: configuration ? configuration.identity : null,
    room: configuration ? configuration.room : null,
    livekitUrl: configuration ? configuration.livekitUrl : null,
    immichKioskUrl: configuration ? configuration.immichKioskUrl : null,
    hasToken: !!(configuration && configuration.token),
  });
}

// The heartbeat rides the frame loop rather than a timer. `setInterval` keeps firing in a
// document whose renderer is wedged but not dead; `requestAnimationFrame` does not, which is
// exactly the distinction the 90 s rule is trying to draw.
function beat(now) {
  if (!painted) {
    painted = true;
    report('hello');
    lastBeat = now;
  } else if (now - lastBeat >= HEARTBEAT_MS) {
    report('alive');
    lastBeat = now;
  }

  requestAnimationFrame(beat);
}

function connect() {
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/local`;
  socket = new WebSocket(url);

  socket.onopen = () => {
    backoff = RECONNECT_MIN_MS;
    if (painted) report('hello');
  };

  socket.onmessage = (event) => {
    let stage;
    try { stage = JSON.parse(event.data); } catch (_) { return; }
    render(stage);
  };

  socket.onclose = () => {
    socket = null;
    setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, RECONNECT_MAX_MS);
  };

  socket.onerror = () => { try { if (socket) socket.close(); } catch (_) {} };
}

function overlay() {
  let element = document.getElementById('framelink-stage');
  if (element) return element;

  element = document.createElement('div');
  element.id = 'framelink-stage';
  element.style.cssText = [
    'position:fixed', 'inset:0', 'z-index:5000', 'display:none',
    'flex-direction:column', 'align-items:center', 'justify-content:center', 'gap:14px',
    'background:#0a0a0a', 'color:#e8e8e8', 'font-family:system-ui,sans-serif',
    'padding:6vh 8vw', 'box-sizing:border-box', 'text-align:center',
  ].join(';');
  document.body.appendChild(element);
  return element;
}

function line(text, style) {
  const node = document.createElement('div');
  node.textContent = text;
  node.style.cssText = style;
  return node;
}

function render(stage) {
  const element = overlay();

  // A supervised restart leaves the device InSync and must never blank the frame, so the
  // annotation is a small persistent strip and not a takeover — §2.10's "an annotation, not a
  // rung", rendered only once the fault rate says somebody has to look at this frame.
  banner(stage.supervisionOverlay);

  if (stage.productRuns) {
    element.style.display = 'none';
    return;
  }

  element.replaceChildren();
  element.style.display = 'flex';

  if (stage.headline) element.appendChild(line(stage.headline, 'font-size:30px;font-weight:650;letter-spacing:.01em'));
  if (stage.detail) element.appendChild(line(stage.detail, 'font-size:18px;color:#9a9a9a;max-width:40em'));
  if (stage.detected) element.appendChild(line(stage.detected, 'font-size:20px;color:#e8e8e8;max-width:40em;margin-top:10px'));
  if (stage.whyItMatters) element.appendChild(line(stage.whyItMatters, 'font-size:17px;color:#9a9a9a;max-width:40em'));
  if (stage.actionGloss) element.appendChild(line(stage.actionGloss, 'font-size:17px;color:#7fb2e5;max-width:40em'));
  if (stage.action) {
    element.appendChild(line(stage.action, 'font-size:13px;color:#6a6a6a;font-family:ui-monospace,monospace;max-width:60em;word-break:break-all'));
  }

  if (stage.attemptBudget > 0 && stage.attempt > 0) {
    element.appendChild(line(`Attempt ${stage.attempt} of ${stage.attemptBudget}`, 'font-size:15px;color:#9a9a9a'));
  }

  if (typeof stage.countdownSeconds === 'number') {
    element.appendChild(line(
      `Restarting in ${stage.countdownSeconds} s`,
      'font-size:17px;color:#9a9a9a;margin-top:8px',
    ));

    const button = document.createElement('button');
    button.textContent = 'Reboot now';
    button.style.cssText = [
      'margin-top:6px', 'padding:14px 30px', 'font-size:19px', 'border-radius:10px',
      'border:1px solid #3a3a3a', 'background:#1b1b1b', 'color:#e8e8e8', 'cursor:pointer',
      // §2.7 item 4: this is the one screen where v1's touch shield must not block input.
      'touch-action:manipulation', 'pointer-events:auto',
    ].join(';');
    button.addEventListener('click', () => send({ kind: 'reboot-now' }));
    element.appendChild(button);
  }

  if (stage.deviceId) {
    element.appendChild(line(stage.deviceId, 'font-size:14px;color:#5a5a5a;font-family:ui-monospace,monospace;margin-top:12px'));
  }
}

function banner(text) {
  let strip = document.getElementById('framelink-supervision');

  if (!text) {
    if (strip) strip.remove();
    return;
  }

  if (!strip) {
    strip = document.createElement('div');
    strip.id = 'framelink-supervision';
    strip.style.cssText = [
      'position:fixed', 'left:0', 'right:0', 'bottom:0', 'z-index:4500',
      'padding:8px 14px', 'background:rgba(20,20,20,.86)', 'color:#d8d8d8',
      'font-family:system-ui,sans-serif', 'font-size:14px', 'text-align:center',
      'pointer-events:none',
    ].join(';');
    document.body.appendChild(strip);
  }

  strip.textContent = text;
}

// The five configured values, from the agent rather than from a file on disk (§2.1). A 503 means
// this frame has not been issued them yet, which is a legitimate state for a pending device (§3.3)
// and not a reason to stop checking in.
async function loadConfiguration() {
  try {
    const response = await fetch('/config.json', { cache: 'no-store' });
    if (response.ok) configuration = await response.json();
  } catch (_) { /* reported as a null configuration, which the agent reads as "not issued" */ }
}

/** Tells the agent a call has ended — the event trigger of §2.10's camera recycle. */
export function callEnded() {
  send({ kind: 'call-ended' });
}

window.frameLinkStage = { callEnded };

loadConfiguration();
connect();
requestAnimationFrame(beat);
