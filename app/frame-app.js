import { LitElement, html, css } from './vendor/lit-all.min.js';
import { CallClient } from './livekit.js';
import { connectControl } from './control.js';
import './frame-grid.js';
import './frame-setup.js';

const DEFAULT_CONFIG = {
  identity: 'framelink-dev',
  room: 'family',
  livekitUrl: 'ws://10.20.30.250:7880',
  immichKioskUrl: '',   // set in config.json to the local Immich Kiosk slideshow URL (guide 9)
  token: '',            // local long-lived LiveKit token
};

// If the slideshow iframe hasn't reported a load within this window, reload it.
// Covers the local-server / Immich-Kiosk startup race on a cold boot.
const IFRAME_LOAD_TIMEOUT_MS = 8000;

class FrameApp extends LitElement {
  static properties = {
    mode: { state: true },            // 'slideshow' | 'call'
    slideshowReady: { state: true },
    config: { state: true },
    needsToken: { state: true },
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
    this.needsToken = false;
    this.participants = [];
    this.selfTrack = null;
    this.largeId = null;
    this.callState = 'connecting';
    this._iframeKey = 0;
    this._status = 'Starting…';
    this._iframeTimer = null;
    this._byId = new Map();
    this.call = null;
    this._control = null;
  }

  async connectedCallback() {
    super.connectedCallback();
    await this._loadConfig();
    this._armIframeRetry();
    window.addEventListener('keydown', this._onKey);
    // GPIO button daemon (Phase C). The dev keyboard 'c' remains a fallback.
    this._control = connectControl({ onCommand: (cmd) => this._onCommand(cmd) });
    if (!this.config.token) { this.needsToken = true; return; }
    this._startCall();
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    window.removeEventListener('keydown', this._onKey);
    clearTimeout(this._iframeTimer);
    if (this._control) this._control.close();
  }

  async _loadConfig() {
    let cfg = { ...DEFAULT_CONFIG };
    try {
      const res = await fetch('./config.json', { cache: 'no-store' });
      if (res.ok) cfg = { ...cfg, ...(await res.json()) };
    } catch (_) { /* config.json is optional in dev */ }
    if (!cfg.token) {
      // Session fallback for a token entered via the setup screen.
      // (Persistent on-disk storage is wired in Phase D; tmpfs wipes localStorage on reboot.)
      const t = localStorage.getItem('framelink_token');
      if (t) cfg.token = t;
    }
    this.config = cfg;
  }

  // ---- LiveKit wiring -------------------------------------------------------

  _startCall() {
    this.call = new CallClient(this.config);
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

  _onToken(e) {
    const token = e.detail.token;
    localStorage.setItem('framelink_token', token);
    this.config = { ...this.config, token };
    this.needsToken = false;
    this._startCall();
  }

  // ---- Control channel (GPIO button) ----------------------------------------

  _onCommand(cmd) {
    if (cmd === 'toggle') this.toggleMode();
    else if (cmd === 'call') this.enterCall();
    else if (cmd === 'hangup') this.exitCall();
  }

  // ---- Slideshow resilience -------------------------------------------------

  _armIframeRetry() {
    clearTimeout(this._iframeTimer);
    if (this.slideshowReady || !this.config.immichKioskUrl) return;
    this._iframeTimer = setTimeout(() => {
      if (!this.slideshowReady) {
        this._status = 'Connecting to slideshow…';
        this._iframeKey++;
        this._armIframeRetry();
      }
    }, IFRAME_LOAD_TIMEOUT_MS);
  }

  _onIframeLoad = () => {
    if (this.config.immichKioskUrl) {
      this.slideshowReady = true;
      clearTimeout(this._iframeTimer);
    }
  };

  _slideshowSrc() {
    const url = this.config.immichKioskUrl;
    if (!url) return 'about:blank';
    return `${url}${url.includes('?') ? '&' : '?'}_k=${this._iframeKey}`;
  }

  // ---- Mode control ---------------------------------------------------------

  _onKey = (e) => { if (e.key === 'c') this.toggleMode(); };   // dev: simulate the call button

  toggleMode() { this.mode === 'call' ? this.exitCall() : this.enterCall(); }

  async enterCall() {
    if (this.mode === 'call') return;
    this.mode = 'call';
    if (this.call) await this.call.enableCall();
  }

  async exitCall() {
    if (this.call) await this.call.disableCall();
    this.largeId = null;
    this.selfTrack = null;
    this.mode = 'slideshow';
  }

  render() {
    const showSplash =
      this.mode === 'slideshow' && !this.needsToken &&
      (!this.slideshowReady || !this.config.immichKioskUrl);
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

      ${this.needsToken
        ? html`<frame-setup .identity=${this.config.identity} @token=${this._onToken}></frame-setup>`
        : ''}

      <div class="splash" ?hidden=${!showSplash}>
        <div class="dot"></div>
        <div class="msg">${this._status}</div>
      </div>
    `;
  }
}
customElements.define('frame-app', FrameApp);
