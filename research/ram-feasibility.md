# 2GB RAM Feasibility Research

## Decision

2GB RAM is **tight but potentially feasible** for 5 incoming WebRTC video streams at 480p or lower. This is the single highest risk in the project and **must be validated on real hardware before writing production code.** No one has publicly reported running this workload on a Pi 5 with 2GB RAM.

## The Constraint

Each FrameLink unit may receive up to 5 simultaneous video streams from other participants. The Pi 5 (2GB) has **no hardware decode for WebRTC codecs** — H.264 hardware decode was removed from the Pi 5 silicon, and VP8/VP9 never had hardware support. All video decoding is software-only on the quad Cortex-A76 cores.

## RAM Budget Estimate (During a 6-way call at 480p)

| Component | Estimated RAM | Source |
|---|---|---|
| RPi OS Lite + labwc | ~150 MB | Tom's Hardware benchmarks |
| Chromium main + GPU process | ~150 MB | Pi forum reports |
| Renderer: SPA + WebRTC JS heap | ~200-300 MB | General WebRTC estimates |
| 5x VP8 480p software decode | ~100-200 MB | Per-stream analysis (~20-40 MB each) |
| 1x camera encode (outgoing 480p) | ~50 MB | Estimated |
| Immich Kiosk iframe (hidden during call) | ~50-100 MB | Chromium renderer baseline |
| **Total estimated** | **~700-1000 MB** | |
| **System total** | **2048 MB** | |
| **Headroom** | **~1000-1300 MB** | |

### Why This Looks Feasible

- Budget shows 1000+ MB headroom on paper
- On a 10.1" display showing 5 tiles, each tile is ~360x256 pixels — 480p is overkill; 360p would suffice
- The Cortex-A76 quad-core at 2.4 GHz handles software VP8 decode well
- LiveKit's adaptive bitrate can automatically request the lowest simulcast layer matching rendered element size

### Why This Might Not Be Feasible

- **Chromium memory grows over time.** Documented reports on Pi forums of Chromium consuming 1.2-1.7 GB after hours of video streaming, eventually causing system hangs.
- **Tom's Hardware benchmark:** Chromium with 5 basic tabs (no WebRTC) on Pi 5 2GB uses **1297 MB total.** With WebRTC decode overhead, this could approach 2 GB.
- **CPU may be the actual bottleneck.** 5x software VP8 decode + 1x software VP8 encode could saturate all 4 cores before RAM is exhausted.
- **No real-world validation exists.** Zero published reports of anyone running 5 concurrent incoming WebRTC video streams on a Pi 5 with 2GB (or even 4GB).

## Essential Mitigations

### ZRAM (Critical)

ZRAM creates compressed swap in RAM using CPU-based compression (zstd). It effectively multiplies usable memory by 2-3x at the cost of some CPU. One benchmark showed a Pi 4 with 4GB + ZRAM running Chromium using 7GB (compressed) while remaining responsive.

**ZRAM is strongly recommended for any 2GB system running Chromium.** It provides a safety net against memory spikes without the catastrophic performance degradation of SD card swap.

### Adaptive Bitrate (LiveKit)

LiveKit's `adaptiveStream` option monitors the rendered size of video elements using `ResizeObserver` and `IntersectionObserver`. If a video element is 360x256 pixels on screen, the SDK tells the server to send the closest matching simulcast layer — not full resolution. Additionally:
- `setVideoQuality('LOW')` can force-cap all incoming streams
- `pixelDensity: 1` (or lower) prevents high-DPI upscaling
- Hidden/offscreen video elements are automatically paused

### Iframe Destruction During Calls

Instead of hiding the Immich Kiosk iframe with CSS `display:none` (which keeps the renderer alive), remove it from the DOM entirely during calls to free ~50-100 MB. Recreate it when the call ends.

### Reduce Resolution

480p is already more than sufficient for a 10.1" display with 5 tiles. Consider 360p or even 240p — at ~360x256 pixels per tile, 240p is visually adequate and dramatically reduces decode CPU and memory.

### Reduce Framerate

15fps instead of 30fps halves the decode work per stream. For a video call between family members (not gaming or sports), 15fps is acceptable.

### Scheduled Chromium Restart

Restart Chromium at a quiet time (e.g., 3 AM daily) to reset accumulated memory leaks. This is a systemd timer that kills and restarts the Chromium process. The SPA will re-initialize, reconnect to the video server, and restore slideshow mode.

### Watchdog Process

A systemd service that monitors Chromium's memory usage (via `/proc/<pid>/status`) and force-restarts it if RSS exceeds a threshold (e.g., 1.5 GB). Also monitors connection liveness and triggers a page reload if the WebRTC connection is stuck.

### SD Card Swap (Emergency)

1 GB of traditional swap on SD card as an emergency overflow beyond ZRAM. SD card swap is slow (~25 MB/s random) and degrades card lifespan, but prevents hard OOM kills. This should rarely be hit if ZRAM and the other mitigations are in place.

## Janus as RAM Escape Hatch

If LiveKit's always-subscribed model proves too memory-heavy, Janus's explicit subscribe model offers a fundamentally different memory profile:

| State | LiveKit | Janus |
|---|---|---|
| Idle (slideshow) | Connected, subscribed, tracks muted | Connected, zero subscribers, zero media |
| Active (call) | Same connection, tracks unmuted | Subscribe to all publishers, start decode |

With Janus, a Pi in slideshow mode (99% of the time) consumes only a WebSocket heartbeat — no video decode, no bandwidth, minimal memory. Media resources are allocated only when the GPIO button is pressed.

## Hardware Validation Plan

**These tests must pass before writing production code.**

### Test 1: Bare WebRTC RAM/CPU

1. Install RPi OS Lite + labwc + Chromium on Pi 5 (2GB)
2. Open a WebRTC test page receiving 5x 480p streams
3. Measure RAM, CPU, and stability over 4 hours
4. **Pass:** Total RAM <1.5 GB, CPU <80%, no OOM kill

### Test 2: LiveKit Client

1. Deploy LiveKit server, connect Pi 5 Chromium client with 5 simulated participants
2. Measure RAM, CPU, join latency, adaptive bitrate behavior
3. **Pass:** Stable connection, RAM <1.5 GB, acceptable video quality

### Test 3: 24-Hour Soak

1. Full stack: labwc + Chromium + SPA + LiveKit + camera + GPIO
2. Simulate periodic calls (join/leave every 30 minutes)
3. Monitor RAM growth, connection stability, video quality over 24 hours
4. **Pass:** No crashes, no OOM, no stuck states

### Decision Gate

If tests fail:
1. Try 360p or 240p resolution
2. Try Janus with explicit subscribe
3. Consider Pi 5 4GB ($10 more per unit)
