// LiveKit call client for FrameLink. Wraps livekit-client (loaded as the UMD global
// `LivekitClient` in index.html) behind a small event interface the UI listens to.
//
// Always-connected lifecycle: connect on start, stay connected while idle with camera+mic
// OFF, publish on call enter, unpublish on call leave. Resilient to LiveKit outages —
// connect/reconnect retry forever so a remote outage never disturbs the slideshow.

const LK = window.LivekitClient;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Validated production simulcast (measured in the July camera sweep): H.264 top layer at
// 1920x1080@30 — the highest mode this hardware sustains. OpenH264 is single-thread-bound
// (~60 MP/s ceiling) but holds 1080p30 solid at ~117%/400 CPU, where VP8 needed 165% for a
// wobbly 27 fps. Capturing at 1080 lines also forces the full-FoV 2304x1296 sensor mode
// (requests of <=900 lines select a cropped ~1.5x-zoom mode). The 180p/360p layers keep
// 5-up grid tiles cheap for receivers. 4 Mbps compensates H.264 constrained-baseline
// efficiency versus VP8 at equal quality.
function publishLayers(fps) {
  return [
    new LK.VideoPreset(320, 180, 180_000, fps),
    new LK.VideoPreset(640, 360, 500_000, fps),
    new LK.VideoPreset(1920, 1080, 4_000_000, fps),
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
        videoCodec: 'h264',
        simulcast: true,
        videoSimulcastLayers: publishLayers(this.fps),
        // livekit-client builds the TOP simulcast encoding from videoEncoding, not from the
        // presets above — without this it clamps the top layer to its stock preset table.
        videoEncoding: publishLayers(this.fps)[2].encoding,
        degradationPreference: 'maintain-framerate',
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
    const cap = { resolution: { width: 1920, height: 1080, frameRate: this.fps }, frameRate: this.fps };
    try {
      const pub = await this.room.localParticipant.setCameraEnabled(true, cap);
      // Bias encoder overload decisions toward smoothness: faces in motion degrade better
      // by softening than by stuttering (pairs with degradationPreference above).
      if (pub && pub.track && pub.track.mediaStreamTrack) pub.track.mediaStreamTrack.contentHint = 'motion';
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
