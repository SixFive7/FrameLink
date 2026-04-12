# Video Calling Platform Research

## Decision

**LiveKit** is the primary video calling platform for FrameLink. **Janus WebRTC Gateway** is the fallback if LiveKit's memory footprint proves too high on the 2GB Pi 5.

## Requirements

The video calling system must support up to 6 Raspberry Pi 5 (2GB RAM) devices in a shared room. Each device runs Chromium in kiosk mode with zero UI — no mouse, keyboard, or touch interaction. A GPIO button is the only control. The server runs as Docker container(s) on dedicated infrastructure. Pi endpoints must require zero network configuration (outbound-only connections).

Specific needs:

- **SFU architecture** — P2P mesh fails at 6 participants on 2GB RAM (each Pi would encode 5 outgoing streams)
- **Built-in or bundled TURN** — Pi endpoints must work behind any NAT without port forwarding
- **Auto-reconnect** — devices run 24/7 unattended in non-technical households
- **Pre-warmable connection** — keep signaling alive at boot, join/leave room on GPIO press
- **Programmatic control** — JavaScript API for join/leave/mute without any user-facing UI
- **ARM64 Docker** — server image must run on ARM64 (or x86_64; ARM64 preferred for flexibility)
- **Open source** — self-hosted, no cloud dependencies

## Why SFU Over P2P

With P2P mesh networking, each device sends its video stream to every other participant individually:

| Participants | Streams sent per device | Streams received per device |
|---|---|---|
| 2 | 1 | 1 |
| 3 | 2 | 2 |
| 6 | 5 | 5 |

At 6 participants, each Pi encodes and sends 5 separate video streams — not viable on 2GB RAM with software-only encoding (Pi 5 has no hardware H.264 encoder).

With an SFU (Selective Forwarding Unit), each Pi sends exactly 1 stream to the server, and the server forwards other participants' streams back. The SFU does **not** transcode or composite — each client still decodes N-1 individual streams, but only encodes 1. Decoding is much cheaper than encoding.

| Participants | Streams sent per device | Streams received per device |
|---|---|---|
| 2 | 1 | 1 |
| 3 | 1 | 2 |
| 6 | 1 | 5 |

Migrating from P2P to SFU later means rewriting the entire video layer — so SFU from the start.

## Why LiveKit

Three factors made LiveKit the primary choice:

1. **Built-in auto-reconnect.** LiveKit's JavaScript SDK handles ICE restart and full reconnection fallback automatically. For a device running 24/7 in a household with imperfect WiFi, this is the most impactful feature. No other lightweight SFU offers this out of the box. (It is not perfect — known edge cases exist where the SDK enters a stuck state — but it is far better than implementing 300-500 lines of manual reconnection logic.)

2. **Adaptive bitrate per subscriber.** The SDK monitors the rendered size of each video element and automatically requests the matching simulcast layer from the server. On a Pi 5 showing 5 small tiles on a 10.1" display, the SDK will request the lowest quality layer without any manual configuration. This is critical for staying within the 2GB RAM budget. Additionally, `setVideoQuality('LOW')` can force-cap all incoming streams as a safety measure.

3. **Single Docker container with built-in TURN.** LiveKit bundles its own TURN server, eliminating the need for a separate coturn container. The Pi endpoints connect outbound on port 443 and require zero network configuration. This is the simplest deployment of any evaluated SFU.

Additional strengths:

- **Official ARM64 Docker images** — `livekit/livekit-server` supports multi-arch
- **Apache 2.0 license** — no copyleft concerns for device distribution
- **Webhooks** — participant joined/left events can notify other frames
- **JWT token auto-refresh** — server pushes refreshed tokens to connected clients, critical for 24/7 operation
- **Company-backed** — funded company with active development and excellent documentation
- **No `network_mode: host` needed** — proper port mapping works, simplifying Docker deployment

## Why Janus as Fallback

Janus has one property that no other SFU offers: **explicit subscribe control**. In LiveKit, clients are subscribed to all room participants by default (tracks can be muted but the subscription exists). In Janus, the client explicitly chooses when to subscribe to each publisher's media.

This means when a FrameLink unit is in slideshow mode (99% of the time), a Janus-connected Pi has:
- Zero subscriber handles
- Zero incoming media
- Zero video decode
- Zero bandwidth consumption
- Only a lightweight WebSocket heartbeat

If 2GB RAM proves too tight with LiveKit's always-subscribed model, Janus's zero-media-when-idle property may be the difference between fitting in memory and not. The SPA architecture and video grid remain the same — only the WebRTC client code changes.

## Candidates Evaluated

Twenty-one video calling solutions were evaluated. Each is summarized below with the primary reasons it was not selected.

### Galene

Single-binary Go SFU with built-in TURN. MIT license. 15MB Docker image, <100MB server RAM. The lightest SFU evaluated.

**Why not chosen:** The community Docker ARM64 image (`deburau/galene`) is abandoned — last updated July 2023, stuck at v0.7.2 while Galene is at v1.0. The project itself does not recommend Docker. Zero evidence of any kiosk, embedded, or IoT deployment was found — Galene is used for university lectures and conferences. The project has a bus factor of 1 (Juliusz Chroboczek accounts for 94% of all commits). No built-in auto-reconnect. The JS client API (`protocol.js`) is functional but has no npm package, no TypeScript types, and a thin ecosystem.

### Jitsi Meet

Full conferencing platform with IFrame API. Apache 2.0. Largest community. The "Grandma kiosk" Instructables project (2020) validated the Chromium-kiosk approach for a 1:1 call with a Pi.

**Why not chosen:** Requires 4-5 Docker containers (web, Prosody, Jicofo, JVB, optional Jibri) and ~2GB server RAM. The React frontend consumes 300-500MB on the Pi client, which combined with 5 incoming video streams would likely exceed 2GB. Slowest join latency (3-5 seconds). The UI is designed for interactive conferencing — stripping it for kiosk use is fragile, as config keys change across Jitsi updates. The "Grandma" prior art was 1:1, not 6-participant.

### MiroTalk SFU

SFU built on mediasoup with a polished built-in UI. AGPLv3. Direct-join URL pattern.

**Why not chosen:** ARM64 Docker availability is unconfirmed. The built-in UI is designed for interactive users and is hard to fully lock down for touch-free kiosk operation. AGPLv3 is the most restrictive license evaluated. Heaviest Docker image (~500MB). Needs separate coturn. Limited pre-warming support.

### PeerJS

Pure P2P with self-hosted signaling server. Lightweight (~45KB client library).

**Why not chosen:** P2P only — eliminated when the project scaled from 2 devices to 6. Each Pi would encode 5 outgoing streams at 6 participants. Known reconnection bugs. No SFU capability.

### Simple-Peer / WebRTC Direct

Thin wrappers over native WebRTC for P2P connections.

**Why not chosen:** P2P only. Same scaling problem as PeerJS at 6 participants.

### mediasoup

Low-level WebRTC SFU library (Node.js with C++ worker). Powerful but requires building an entire application around it.

**Why not chosen:** A library, not a product. Estimated months of custom development to build room management, signaling, authentication, and a deployment-ready server. MiroTalk SFU is built on mediasoup and demonstrates the effort required.

### GStreamer + WebRTC

Maximum-flexibility approach using GStreamer pipelines for WebRTC.

**Why not chosen:** Pi 5 GStreamer/libcamera integration (originally an issue on Bookworm, largely resolved on Trixie) would still require building everything from scratch — signaling, room management, client UI. More of a toolkit than a solution.

### OpenVidu

WebRTC platform built on top of mediasoup (previously Kurento).

**Why not chosen:** No ARM64 Docker images available.

### Daily.co

Cloud-hosted WebRTC platform with excellent APIs.

**Why not chosen:** Cloud-only. Violates the self-hosted requirement. Ongoing subscription cost.

### Zoom SDK

Commercial video calling SDK.

**Why not chosen:** No ARM64 support. Cloud-only. Proprietary. Heavy client. Not self-hostable.

### Matrix / Element Call

Decentralized communication protocol with WebRTC calling via Element Call.

**Why not chosen:** Requires running Synapse (Matrix homeserver) + LiveKit + coturn + Element Call. Too many moving parts for a video calling feature. Element Call itself is still maturing.

### Nextcloud Talk

Video calling feature within the Nextcloud platform.

**Why not chosen:** Requires a full Nextcloud installation. Massive overhead for a video call feature. Not designed for embedded/kiosk use.

### BigBlueButton

Open-source web conferencing platform for education.

**Why not chosen:** No ARM64 support. Requires 16GB RAM minimum on the server. Designed for classroom scenarios with screen sharing, whiteboard, and breakout rooms — far beyond the scope needed.

### UV4L WebRTC

Raspberry Pi camera-to-WebRTC bridge.

**Why not chosen:** Dead project. Does not work on Pi 5 / modern Debian (Bookworm or Trixie).

### Asterisk + WebRTC

Full PBX (private branch exchange) with WebRTC gateway.

**Why not chosen:** A telephone system is complete overkill for a video frame. The "originate" API for calling devices is powerful but the deployment and maintenance burden of a PBX is disproportionate.

### FreeSWITCH + WebRTC

Enterprise telephony platform with WebRTC support.

**Why not chosen:** Even heavier than Asterisk. Same PBX-overkill reasoning.

### Pion

Go library for WebRTC. Used internally by both Galene and LiveKit.

**Not a candidate:** A library, not a product. Evaluated only for context — it powers the recommended solutions.

### Meetecho

The company behind Janus Gateway. Offers commercial support and hosted Janus.

**Not a candidate:** Not a separate product from Janus. Evaluated only for context.

## LiveKit vs Janus Head-to-Head

| Aspect | LiveKit | Janus |
|---|---|---|
| Auto-reconnect | Built-in (imperfect) | Manual (300-500 lines) |
| TURN | Built-in | Separate coturn |
| Docker simplicity | 1 official container | 2 containers (community image) |
| Idle RAM on Pi | Connected, tracks muted | Zero media, zero decode |
| Join latency (pre-warmed) | ~1 second | 200-500ms |
| Server resources (6 users) | ~300 MB RAM, ~2-5% CPU | ~200 MB RAM, ~1-2% CPU |
| Client code | ~40-150 lines | ~150-250 lines + 300-500 reconnect |
| Adaptive bitrate | Automatic per subscriber | Manual simulcast layer selection |
| Documentation | Excellent, modern | Good but scattered |
| Community | 22k+ GitHub stars, funded | 9.1k stars, Meetecho company |
| License | Apache 2.0 | GPLv3 |
| Open issues | 147 | 14 |

## Known Risks

### LiveKit

- **Auto-reconnect edge cases** — SDK can enter a state where it believes it's disconnected while the connection works (GitHub issue #1163). Needs external watchdog.
- **TURN in Docker/cloud NAT** — recurring issues with external IP detection in Docker and cloud environments (issues #3826, #4095, #3971). Fine for simple self-hosted deployments.
- **Memory leaks** — documented in both server (#2319, #3614) and client SDK (#1654 fixed, #1432 open). Plan for periodic restarts.
- **Token management** — JWT tokens expire. Server pushes auto-refreshed tokens (10-min expiry), but if a client is disconnected >10 minutes, it needs a fresh token from a backend service.

### Janus

- **Reconnection complexity** — ICE restart is ~30 lines, but full production reconnection with WebSocket recovery, session reclaim, re-subscription, and error handling is 300-500 lines.
- **Memory leaks** — leak fixes appear in every release from 1.3.0 through 1.4.0. Plan for periodic server restarts.
- **GPLv3 license** — `janus.js` is GPL. If distributed on the device, copyleft may apply to custom code. Mitigations: serve janus.js from server URL, write own thin client, or obtain commercial license from Meetecho.
- **Silent session kills** — Janus destroys sessions when keepalives stop (background tab, network blip >60s) with no error callback.
- **coturn operational burden** — additional container, TLS certificates, credential rotation, monitoring.

## Prior Art

The "Automatic Video Conference for Grandma" Instructables project (2020) used Jitsi Meet in Chromium kiosk mode on a Raspberry Pi for a 90-year-old grandmother. This validates the general Chromium-kiosk + WebRTC approach but was 1:1 only, not multi-party.
