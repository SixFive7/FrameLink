// LiveKit call client for FrameLink. Wraps livekit-client (loaded as the UMD global
// `LivekitClient` in index.html) behind a small event interface the UI listens to.
//
// Always-connected lifecycle: connect on start, stay connected while idle with camera+mic
// OFF, publish on call enter, unpublish on call leave. Resilient to LiveKit outages —
// connect/reconnect retry forever so a remote outage never disturbs the slideshow.

const LK = window.LivekitClient;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Validated production simulcast: three layers so a 5-up grid tile pulls 360p, not 720p.
// fps is the dev default (30); the real-camera build raises capture + layers to 56 (see todo).
function publishLayers(fps) {
  return [
    new LK.VideoPreset(320, 180, 180_000, fps),
    new LK.VideoPreset(640, 360, 500_000, fps),
    new LK.VideoPreset(1280, 720, 1_700_000, fps),
  ];
}

export class CallClient extends EventTarget {
  constructor(config) {
    super();
    this.config = config;
    this.fps = config.captureFps || 30;
    this.room = null;
    this.inCall = false;
  }

  _emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }

  async connect() {
    const room = new LK.Room({
      adaptiveStream: { pixelDensity: 1, pauseVideoInBackground: true },
      dynacast: true,
      disconnectOnPageLeave: false,
      stopLocalTrackOnUnpublish: true,
      publishDefaults: {
        videoCodec: 'vp8',
        simulcast: true,
        videoSimulcastLayers: publishLayers(this.fps),
        dtx: true,
        red: true,
      },
      reconnectPolicy: {
        // Never give up: a frame must self-heal after an hours-long outage.
        nextRetryDelayInMs: (ctx) => {
          const steps = [0, 300, 1200, 2700, 4800, 7000, 10000, 15000];
          return steps[Math.min(ctx.retryCount, steps.length - 1)] + Math.random() * 1000;
        },
      },
    });
    this.room = room;
    this._wire(room);
    await this._connectWithRetry();
  }

  async _connectWithRetry() {
    for (let attempt = 0; ; attempt++) {
      try {
        await this.room.connect(this.config.livekitUrl, this.config.token);
        this._emit('status', { state: 'connected' });
        return;
      } catch (e) {
        this._emit('status', { state: 'connecting', error: e && e.message });
        await sleep(Math.min(1000 * 2 ** attempt, 15000));
      }
    }
  }

  _wire(room) {
    const E = LK.RoomEvent;
    room
      .on(E.TrackSubscribed, (track, pub, p) => this._emit('tracks', { kind: 'sub', track, p }))
      .on(E.TrackUnsubscribed, (track, pub, p) => this._emit('tracks', { kind: 'unsub', track, p }))
      .on(E.ParticipantConnected, (p) => this._emit('participant', { kind: 'join', p }))
      .on(E.ParticipantDisconnected, (p) => this._emit('participant', { kind: 'leave', p }))
      .on(E.ActiveSpeakersChanged, (speakers) => this._emit('speakers', { speakers }))
      .on(E.ConnectionQualityChanged, (q, p) => this._emit('quality', { q, p }))
      .on(E.TrackMuted, (pub, p) => this._emit('mute', { p, muted: true }))
      .on(E.TrackUnmuted, (pub, p) => this._emit('mute', { p, muted: false }))
      .on(E.Disconnected, () => this._onDisconnected())
      .on(E.Reconnecting, () => this._emit('status', { state: 'reconnecting' }))
      .on(E.Reconnected, () => this._emit('status', { state: 'connected' }));
  }

  async _onDisconnected() {
    this._emit('status', { state: 'reconnecting' });
    await sleep(2000);
    await this._connectWithRetry();
  }

  remoteParticipants() {
    if (!this.room) return [];
    return Array.from(this.room.remoteParticipants.values());
  }

  async enableCall() {
    if (!this.room || this.inCall) return;
    this.inCall = true;
    const cap = { resolution: { width: 1280, height: 720, frameRate: this.fps }, frameRate: this.fps };
    try {
      const pub = await this.room.localParticipant.setCameraEnabled(true, cap);
      this._emit('selfTrack', { track: pub && pub.track ? pub.track : null });
    } catch (e) {
      this._emit('selfTrack', { track: null });   // no camera — still join and show others
    }
    try {
      await this.room.localParticipant.setMicrophoneEnabled(true, {
        echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 1,
      });
    } catch (e) { /* no mic — still join */ }
  }

  async disableCall() {
    if (!this.room || !this.inCall) return;
    this.inCall = false;
    await this.room.localParticipant.setCameraEnabled(false);
    await this.room.localParticipant.setMicrophoneEnabled(false);
    this._emit('selfTrack', { track: null });
  }
}
