import { LitElement, html, css } from './vendor/lit-all.min.js';
import { CallClient, callable } from './livekit.js';
import './frame-grid.js';

// No livekitUrl here, deliberately. The Fleet Manager owns the call server and mints the token,
// and both values reach this page as fields of the one document the agent serves from what its
// reconciler has recorded — so the address and the credential are always the same frame's answer
// to the same question. A default address would be a second source for one half of that pair: a
// page that failed to fetch the document, or was issued a token before anyone set call.livekitUrl,
// would hold a real credential and dial somewhere nobody chose. That fails as "the call does not
// work" rather than as "this frame has not been told where to call", which is the wrong sentence
// to put in front of somebody at 20:00 on a Sunday.
const DEFAULT_CONFIG = {
  identity: 'framelink-dev',
  room: 'family',
  livekitUrl: '',       // issued as call.livekitUrl; never guessed — see callable() in livekit.js
  immichKioskUrl: '',   // set in config.json to the local Immich Kiosk slideshow URL (guide 9)
  token: '',            // issued as call.token, minted by the Fleet Manager
};

// If the slideshow iframe hasn't reported a load within this window after a
// successful probe, retreat to probing. Covers a server that accepts connections
// but serves a broken page.
const IFRAME_LOAD_TIMEOUT_MS = 8000;

// Slideshow health probe: the iframe only ever navigates to the slideshow URL
// after a probe confirms the server answers. Handing Chromium a dead URL is not
// harmless — its subframe error pages auto-reload, and every navigation retains
// renderer state. Measured on hardware: ~50 MB/min of renderer growth against a
// refused port, ending in an OOM kill of the renderer or a full system stall.
const PROBE_TIMEOUT_MS = 4000;      // how long one probe waits for the server
const PROBE_BACKOFF_MIN_MS = 3000;  // first retry delay while the server is down
const PROBE_BACKOFF_MAX_MS = 30000; // retry delay cap
const HEALTHY_RECHECK_MS = 60000;   // re-probe cadence while the slideshow runs
const PROBE_MISSES_TO_UNLOAD = 2;   // consecutive failures before unloading a live iframe

class FrameApp extends LitElement {
  static properties = {
    mode: { state: true },            // 'slideshow' | 'call'
    slideshowReady: { state: true },
    _slideshowUp: { state: true },    // probe verdict: the slideshow server answers
    config: { state: true },
    participants: { state: true },    // [{identity, name, track, muted, quality}]
    selfTrack: { state: true },
    largeId: { state: true },
    callState: { state: true },       // 'connected' | 'connecting' | 'reconnecting'
    _iframeKey: { state: true },
    _status: { state: true },
  };

  static styles = css`
    :host {
      position: fixed; inset: 0; display: block;
      background: #000; color: #eee; font-family: system-ui, sans-serif;
    }
    [hidden] { display: none !important; }
    .layer { position: absolute; inset: 0; }
    .slideshow { border: 0; width: 100%; height: 100%; }

    /* Touch shield: blocks all input over the slideshow (the viewer can't disturb the
       cross-origin iframe). In call mode it passes taps through to the grid for
       tap-to-promote. */
    .touch-shield { position: absolute; inset: 0; z-index: 1000; background: transparent; touch-action: none; }
    .touch-shield.passthrough { pointer-events: none; }

    .splash {
      position: absolute; inset: 0; z-index: 2000;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 18px; background: #0a0a0a;
    }
    .splash .dot {
      width: 54px; height: 54px; border-radius: 50%;
      border: 5px solid #1d1d1d; border-top-color: #4a90d9; animation: spin 1s linear infinite;
    }
    .splash .msg { font-size: 20px; color: #888; letter-spacing: .03em; }
    @keyframes spin { to { transform: rotate(360deg); } }
  `;

  constructor() {
    super();
    this.mode = 'slideshow';
    this.slideshowReady = false;
    this.config = DEFAULT_CONFIG;
    this.participants = [];
    this.selfTrack = null;
    this.largeId = null;
    this.callState = 'connecting';
    this._iframeKey = 0;
    this._status = 'Starting…';
    this._iframeTimer = null;
    this._slideshowUp = false;
    this._probeTimer = null;
    this._probeMisses = 0;
    this._probeBackoff = PROBE_BACKOFF_MIN_MS;
    this._byId = new Map();
    this.call = null;
  }

  async connectedCallback() {
    super.connectedCallback();
    await this._loadConfig();
    this._startSlideshowWatch();
    window.addEventListener('keydown', this._onKey);
    // The call button. It used to be a WebSocket to a GPIO daemon on 127.0.0.1:8889; the daemon
    // is inside the agent now, so a press arrives on the same channel frame-stage.js already
    // holds and is re-broadcast as this event. The dev keyboard 'c' remains a fallback.
    window.addEventListener('framelink-command', this._onControl);
    this._reviewCall();
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    window.removeEventListener('keydown', this._onKey);
    window.removeEventListener('framelink-command', this._onControl);
    clearTimeout(this._iframeTimer);
    clearTimeout(this._probeTimer);
  }

  // The document the agent serves is the only source, and nothing is remembered beside it. There
  // used to be a stored token here, restored when the fetch came back empty or not at all — the
  // one field that survived a missing document. It is gone with the screen that wrote it: the
  // Fleet Manager mints every token and the agent records it as app.config.livekit-token, so a
  // copy in the browser is a second writer for a value one resource owns, and one that outlives
  // the document that issued it. A fetch that does not land is therefore no call rather than a
  // call placed on a credential from an earlier configuration — and it is not a silence either,
  // because a 503 here means this frame has not been issued its settings (§3.3) and §2.6's ladder
  // is already saying so, in words, on the panel above this page.
  async _loadConfig() {
    let cfg = { ...DEFAULT_CONFIG };
    try {
      const res = await fetch('./config.json', { cache: 'no-store' });
      if (res.ok) cfg = { ...cfg, ...(await res.json()) };
    } catch (_) { /* config.json is optional in dev */ }
    this.config = cfg;
  }

  // ---- LiveKit wiring -------------------------------------------------------

  // The one place that decides whether this frame calls at all, so the address and the credential
  // are checked together or not at all.
  //
  // Two outcomes, and there is deliberately no longer a third. A complete pair connects; anything
  // else runs the slideshow and says nothing about calls, which is what LiveKitOptions.PublicUrl
  // already specifies for a fleet nobody has set call.livekitUrl on: "the frame stays green and
  // silent about calls instead of retrying a URL nobody chose".
  //
  // The case that went was an address with no token, which used to open a screen asking a person
  // to paste one. It was the only half-pair a person could fix by typing, and it stopped being
  // that when the Fleet Manager took ownership of minting: the API secret lives inside the server,
  // is written 0600 and is shown on no surface, so there is nowhere left for anybody to obtain a
  // token from. A frame holding half its credential is a frame waiting for its Fleet Manager, not
  // one waiting for a person — and what a waiting frame needs is the sentence §2.6 already puts on
  // the panel, not a text box nobody can fill in.
  _reviewCall() {
    if (callable(this.config)) this._startCall();
  }

  _startCall() {
    // The reload hook returns the freshly fetched document rather than mutating this.config, so
    // the client owns when it adopts a rotated token. The agent serves /config.json from the
    // values its reconciler has recorded, so what comes back is a value that has been converged
    // on — not whatever the Fleet Manager said most recently.
    this.call = new CallClient(this.config, async () => {
      await this._loadConfig();
      return this.config;
    });
    this.call.addEventListener('status', (e) => { this.callState = e.detail.state; });
    this.call.addEventListener('tracks', (e) => this._onTracks(e.detail));
    this.call.addEventListener('participant', (e) => this._onParticipant(e.detail));
    this.call.addEventListener('mute', (e) => this._onMute(e.detail));
    this.call.addEventListener('quality', (e) => this._onQuality(e.detail));
    this.call.addEventListener('selfTrack', (e) => { this.selfTrack = e.detail.track; });
    this.call.connect();
  }

  _ensure(p) {
    let rec = this._byId.get(p.identity);
    if (!rec) {
      rec = { identity: p.identity, name: p.name || p.identity, track: null, muted: false, quality: '' };
      this._byId.set(p.identity, rec);
    }
    return rec;
  }

  _sync() { this.participants = Array.from(this._byId.values()); }

  _remove(id) {
    this._byId.delete(id);
    if (this.largeId === id) this.largeId = null;
    this._sync();
    if (this._byId.size === 0 && this.mode === 'call') this.exitCall();   // last peer left
  }

  _audioSink() {
    if (!this._sink) {
      this._sink = document.createElement('div');
      this._sink.style.display = 'none';
      document.body.appendChild(this._sink);
    }
    return this._sink;
  }

  _onTracks({ kind, track, p }) {
    if (track.kind === 'audio') {
      if (kind === 'sub') this._audioSink().appendChild(track.attach());
      else track.detach().forEach((el) => el.remove());
      return;
    }
    const rec = this._ensure(p);
    rec.track = kind === 'sub' ? track : null;
    this._sync();
    if (kind === 'sub' && this.mode === 'slideshow') this.enterCall();   // auto-answer
  }

  _onParticipant({ kind, p }) {
    if (kind === 'join') {
      this._ensure(p);
      this._sync();
      if (this.mode === 'slideshow') this.enterCall();   // auto-answer
    } else {
      this._remove(p.identity);
    }
  }

  _onMute({ p, muted }) { const r = this._byId.get(p.identity); if (r) { r.muted = muted; this._sync(); } }
  _onQuality({ p, q }) { const r = this._byId.get(p.identity); if (r) { r.quality = String(q || '').toLowerCase(); this._sync(); } }

  _onPromote(e) {
    const id = e.detail.id;
    this.largeId = this.largeId === id ? null : id;   // tap toggles fullscreen
  }

  // ---- Control channel (GPIO button) ----------------------------------------

  _onControl = (e) => this._onCommand(e.detail);

  _onCommand(cmd) {
    if (cmd === 'toggle') this.toggleMode();
    else if (cmd === 'call') this.enterCall();
    else if (cmd === 'hangup') this.exitCall();
  }

  // ---- Slideshow resilience -------------------------------------------------
  // The iframe never navigates to a URL that hasn't just answered a probe. While
  // the slideshow server is down the iframe sits on about:blank behind the splash
  // and a cheap no-cors fetch retries with capped backoff — Chromium is never
  // given a dead URL to churn on (see the constants block for why that matters).

  async _probeSlideshow() {
    try {
      await fetch(this.config.immichKioskUrl, {
        mode: 'no-cors', cache: 'no-store', signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
      });
      return true;   // opaque response = the server answered; that's all we need
    } catch (_) {
      return false;  // refused, unreachable, or timed out
    }
  }

  _startSlideshowWatch() {
    clearTimeout(this._probeTimer);
    this._probeBackoff = PROBE_BACKOFF_MIN_MS;
    this._probeMisses = 0;
    this._slideshowTick();
  }

  async _slideshowTick() {
    clearTimeout(this._probeTimer);
    if (!this.config.immichKioskUrl) return;
    if (this.mode !== 'slideshow') return;          // paused in call mode; exitCall re-arms
    const up = await this._probeSlideshow();
    if (this.mode !== 'slideshow') return;          // mode may have flipped mid-probe
    let next;
    if (up) {
      this._probeMisses = 0;
      this._probeBackoff = PROBE_BACKOFF_MIN_MS;
      if (!this._slideshowUp) {
        this._slideshowUp = true;                   // render() now hands the iframe the real URL
        this._iframeKey++;
        this._armIframeLoadGuard();
      }
      next = HEALTHY_RECHECK_MS;
    } else {
      this._probeMisses++;
      if (this._slideshowUp && this._probeMisses >= PROBE_MISSES_TO_UNLOAD) {
        this._slideshowUp = false;                  // unload a dead slideshow: blank + splash
        this.slideshowReady = false;
      }
      if (!this._slideshowUp) this._status = 'Waiting for the photo library…';
      next = this._probeBackoff;
      this._probeBackoff = Math.min(this._probeBackoff * 2, PROBE_BACKOFF_MAX_MS);
    }
    this._probeTimer = setTimeout(() => this._slideshowTick(), next);
  }

  _armIframeLoadGuard() {
    clearTimeout(this._iframeTimer);
    this._iframeTimer = setTimeout(() => {
      if (!this.slideshowReady && this.mode === 'slideshow') {
        // The server answered the probe but the page never finished loading —
        // retreat to about:blank and let the probe loop try again.
        this._slideshowUp = false;
        this._startSlideshowWatch();
      }
    }, IFRAME_LOAD_TIMEOUT_MS);
  }

  _onIframeLoad = () => {
    if (this.config.immichKioskUrl && this._slideshowUp) {
      this.slideshowReady = true;
      clearTimeout(this._iframeTimer);
    }
  };

  _slideshowSrc() {
    // Unload the slideshow while in a call: the hidden iframe otherwise keeps running
    // transitions, costing CPU and ~50-100 MB RAM exactly when the call needs both.
    if (this.mode === 'call') return 'about:blank';
    const url = this.config.immichKioskUrl;
    if (!url || !this._slideshowUp) return 'about:blank';
    return `${url}${url.includes('?') ? '&' : '?'}_k=${this._iframeKey}`;
  }

  // ---- Mode control ---------------------------------------------------------

  _onKey = (e) => { if (e.key === 'c') this.toggleMode(); };   // dev: simulate the call button

  toggleMode() { this.mode === 'call' ? this.exitCall() : this.enterCall(); }

  async enterCall() {
    if (this.mode === 'call') return;
    this.mode = 'call';
    // Told before the camera is published rather than after, so the window in which the agent
    // believes this frame is idle closes before there is a call to interrupt. §2.10 lets two
    // behaviours act on the browser while it is idle — the 03:00 restart and the page refresh —
    // and both of them read this.
    if (window.frameLinkStage) window.frameLinkStage.callStarted();
    clearTimeout(this._iframeTimer);
    clearTimeout(this._probeTimer);
    if (this.call) await this.call.enableCall();
  }

  async exitCall() {
    if (this.mode !== 'call') return;
    if (this.call) await this.call.disableCall();
    this.largeId = null;
    this.selfTrack = null;
    this.mode = 'slideshow';
    this.slideshowReady = false;
    this._slideshowUp = false;
    this._startSlideshowWatch();
    // The camera node's provide-mode stream wedges after a few acquire/release cycles
    // (measured). The agent restarts framelink-camera on this event, so the NEXT call
    // always acquires a freshly started node. It goes over the stage channel now — v1 sent
    // it to the GPIO daemon's own WebSocket, and that port is gone with the daemon.
    if (window.frameLinkStage) window.frameLinkStage.callEnded();
  }

  render() {
    const showSplash =
      this.mode === 'slideshow' && (!this.slideshowReady || !this.config.immichKioskUrl);
    return html`
      <iframe
        class="layer slideshow"
        ?hidden=${this.mode !== 'slideshow'}
        .src=${this._slideshowSrc()}
        @load=${this._onIframeLoad}
      ></iframe>

      <frame-grid
        class="layer"
        ?hidden=${this.mode !== 'call'}
        .participants=${this.participants}
        .selfTrack=${this.selfTrack}
        .largeId=${this.largeId}
        @promote=${this._onPromote}
      ></frame-grid>

      <div class="touch-shield ${this.mode === 'call' ? 'passthrough' : ''}"></div>

      <div class="splash" ?hidden=${!showSplash}>
        <div class="dot"></div>
        <div class="msg">${this._status}</div>
      </div>
    `;
  }
}
customElements.define('frame-app', FrameApp);
