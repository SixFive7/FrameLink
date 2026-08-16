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
//   4. Say how old this document is, and reload it when the agent says it is out of date. version2.md
//      §2.1 puts the app inside the agent binary and §2.8 replaces that binary hourly, so a browser
//      that has been up since before the last update is showing a page nobody serves any more —
//      measured on 2026-08-16, where a new stage was served correctly and never reached the screen.
//      The socket cannot tell the agent that: a reconnect and a load look identical from its end,
//      which is exactly why the frame's journal recorded "The page checked in after 0 s" about a
//      document an hour and a half old. `performance.now()` counts from *this document's*
//      navigation, so reporting it is the one thing that separates the two.
//
// No imports on purpose. This file must keep working when the rest of the app does not, and a
// broken vendor bundle must not be able to take the liveness signal with it. The reload lands here
// for the same reason and it is the sharper case: the whole point of a stale page is that the app
// on it may be the part that is out of date, so the reload must not be routed through anything the
// app owns.

const HEARTBEAT_MS = 15000;
const RECONNECT_MIN_MS = 500;
const RECONNECT_MAX_MS = 5000;

// The agent's console palette, in the medium this surface has. The accent is *composed by the
// agent* and arrives named (`stage.accent`), because the two stages must not each work out what
// colour a frame is: the console painted a repairing frame green under a headline saying it was
// fixing something for exactly as long as it derived its accent from the ladder alone. This page
// derives nothing — it looks the name up, and an unfamiliar one falls through to the ordinary
// headline colour rather than to a guess.
const ACCENTS = {
  green: '#4fd6a0',
  amber: '#f0a52a',
  blue: '#4aa3e8',
  red: '#e58f8f',
  grey: '#9a9a9a',
};

let socket = null;
let backoff = RECONNECT_MIN_MS;
let configuration = null;
let lastBeat = 0;
let painted = false;
let inCall = false;
let reloadWanted = false;

// Both new fields are stamped here rather than at each call site, because the agent reads them as
// *levels* and a message that omitted them would read as "not in a call, document age unknown" —
// which a button press on the repair screen must not be able to say.
//
//   documentAgeMs — how long ago *this document* started loading. Monotonic, and unaffected by the
//     clock step a Pi with no RTC takes seconds after every boot: a wall-clock instant sent from
//     here would be compared against an agent whose clock had moved underneath it.
//   inCall — a level and not an edge, so a lost message costs one heartbeat of accuracy instead of
//     leaving the agent permanently wrong about whether somebody is talking through this frame.
function send(message) {
  try {
    if (socket && socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({
        ...message,
        documentAgeMs: Math.round(performance.now()),
        inCall,
      }));
    }
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

    // The one command this file acts on itself. A stale document is a document, so the narrowest
    // repair is to fetch it again — no unit restart, no compositor teardown, no black frame. It is
    // handled here rather than dispatched because a page whose app half failed to load is still a
    // page that has to be replaceable, and that page never listens for `framelink-command`.
    if (stage.command === 'reload') {
      reload();
      return;
    }

    // A frame carrying a command is the call button, not narration: version2.md's catalog retires
    // the GPIO daemon's WebSocket server on 127.0.0.1:8889 ("with both inside one binary there is
    // no port"), so a press arrives here and is re-broadcast for the app to act on. The frame is
    // still a complete, current stage frame, so rendering it as well would be correct — it is
    // skipped only because a command never changes what is on screen by itself.
    if (stage.command) {
      window.dispatchEvent(new CustomEvent('framelink-command', { detail: stage.command }));
      return;
    }

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

// The headline with the agent's accent beside it, the way the console stage puts its glyph beside
// the same sentence. A dot and not a spinner: nothing on this screen may suggest motion that is
// not happening, and the console's own glyph goes still for the same reason.
function headline(text, accent) {
  // Typed rather than truthiness-checked, so a name that happens to exist on Object.prototype
  // resolves to no accent instead of to a function.
  const colour = typeof ACCENTS[accent] === 'string' ? ACCENTS[accent] : null;
  const row = document.createElement('div');
  row.style.cssText = 'display:flex;align-items:center;justify-content:center;gap:14px;max-width:40em';

  if (colour) {
    const dot = document.createElement('span');
    dot.style.cssText = `flex:none;width:16px;height:16px;border-radius:50%;background:${colour}`;
    row.appendChild(dot);
  }

  row.appendChild(line(text, 'font-size:30px;font-weight:650;letter-spacing:.01em'));
  return row;
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

  if (stage.headline) element.appendChild(headline(stage.headline, stage.accent));
  if (stage.detail) element.appendChild(line(stage.detail, 'font-size:18px;color:#9a9a9a;max-width:40em'));
  if (stage.detected) element.appendChild(line(stage.detected, 'font-size:20px;color:#e8e8e8;max-width:40em;margin-top:10px'));
  if (stage.whyItMatters) element.appendChild(line(stage.whyItMatters, 'font-size:17px;color:#9a9a9a;max-width:40em'));
  if (stage.actionGloss) element.appendChild(line(stage.actionGloss, 'font-size:17px;color:#7fb2e5;max-width:40em'));
  if (stage.action) {
    element.appendChild(line(stage.action, 'font-size:13px;color:#6a6a6a;font-family:ui-monospace,monospace;max-width:60em;word-break:break-all'));
  }

  // version2.md §2.7 item 5. One item at a time with its attempt count, worded by the agent
  // (ReconcileVoice) rather than assembled here, so the console and this page cannot disagree.
  // The agent sends it only while something is actually being attempted — a frame that has given
  // up sends none, which is what stops this screen animating work that is not happening.
  if (stage.progressLine) {
    element.appendChild(line(stage.progressLine, 'font-size:15px;color:#9a9a9a'));
  } else if (!stage.canRetry && stage.attemptBudget > 0 && stage.attempt > 0) {
    element.appendChild(line(`Attempt ${stage.attempt} of ${stage.attemptBudget}`, 'font-size:15px;color:#9a9a9a'));
  }

  // §2.7 items 7, 8 and 9 — the stopped frame. Everything here is static: no counter, no bar, no
  // countdown, because nothing is happening and a screen that suggests otherwise is what made a
  // frame look like it was rebooting for ever.
  if (stage.stoppedLine) element.appendChild(line(stage.stoppedLine, 'font-size:18px;color:#e58f8f;max-width:50em;margin-top:10px'));
  if (stage.escalationLine) element.appendChild(line(stage.escalationLine, 'font-size:16px;color:#9a9a9a;max-width:40em'));
  if (stage.contactLine) element.appendChild(line(stage.contactLine, 'font-size:19px;color:#e8e8e8;max-width:40em;margin-top:6px'));

  // §2.5 rung 5: retry is pressable by whoever is standing at the frame, not only from the Fleet
  // Manager. It sends the same reset the Fleet Manager's retry sends, over the channel that is
  // already open, and it is offered only when something has actually given up — a button that
  // resets nothing teaches the person that the button does nothing.
  if (stage.canRetry) {
    const retry = document.createElement('button');
    retry.textContent = 'Try again';
    retry.style.cssText = [
      'margin-top:14px', 'padding:16px 34px', 'font-size:20px', 'border-radius:10px',
      'border:1px solid #3a3a3a', 'background:#1b1b1b', 'color:#e8e8e8', 'cursor:pointer',
      // The same exemption §2.7 item 4 gives "Reboot now": this is a screen where v1's touch
      // shield must not block input.
      'touch-action:manipulation', 'pointer-events:auto',
    ].join(';');
    retry.addEventListener('click', () => {
      retry.disabled = true;
      retry.textContent = 'Trying again…';
      send({ kind: 'retry' });
    });
    element.appendChild(retry);
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

// The agent asks for a reload only after checking that no call is in progress, but its copy of that
// is up to one heartbeat old — so a call that started a second ago is not in it yet. This is the
// other half of that guard, and it is the half that cannot be out of date: the page knows whether
// somebody is talking through it right now. The request is kept rather than refused, and taken the
// moment the call ends, which is exactly how §2.10's daily restart waits out a call.
function reload() {
  if (inCall) {
    reloadWanted = true;
    return;
  }
  location.reload();
}

/** Tells the agent a call has started, so nothing that would interrupt it fires. */
export function callStarted() {
  inCall = true;
  report('alive');   // on the change rather than at the next heartbeat: the guard is time-sensitive
}

/** Tells the agent a call has ended — the event trigger of §2.10's camera recycle. */
export function callEnded() {
  inCall = false;
  send({ kind: 'call-ended' });
  if (reloadWanted) location.reload();
}

window.frameLinkStage = { callStarted, callEnded };

loadConfiguration();
connect();
requestAnimationFrame(beat);
