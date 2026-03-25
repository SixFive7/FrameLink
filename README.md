# FrameLink

A Raspberry Pi-based digital photo frame with one-button video calling. Designed for elderly family members with dementia — always showing familiar photos, with a simple physical button to start a face-to-face video call with up to 6 devices in a shared room.

## Concept

Each FrameLink unit runs [Immich Kiosk](https://github.com/damongolding/immich-kiosk) to display a slideshow of family photos on a touchscreen. A physical GPIO button on the device joins a shared video call room, temporarily replacing the photo slideshow. When the call ends (second button press or all others leave), the slideshow resumes automatically.

The goal is zero-interaction operation for the viewer — photos play continuously, and answering a call requires no action. The only interaction is pressing the button to *initiate* a call.

## Hardware

Each unit consists of (x2 ordered, sourced from [Waveshare](https://www.waveshare.com/)):

| Component | Product | Part Number | Brand | SKU |
|---|---|---|---|---|
| Single-board computer | [Raspberry Pi 5 (2GB RAM)](https://www.waveshare.com/raspberry-pi-5.htm?sku=28316) | Raspberry Pi 5-2GB | Raspberry Pi Foundation | 28316 |
| Display | [10.1" DSI Capacitive Touch Display, 800x1280, IPS, Optical Bonding, 10-Point Touch](https://www.waveshare.com/10.1-dsi-touch-a.htm) | 10.1-DSI-TOUCH-A | Waveshare | 30052 |
| Camera | [Raspberry Pi Camera Module 3, 12MP, Auto-Focus, IMX708](https://www.waveshare.com/raspberry-pi-camera-module-3.htm?sku=23943) | Raspberry Pi Camera module 3 | Raspberry Pi Foundation | 23943 |
| Power supply | [Official 27W USB Type-C Power Supply (White, EU)](https://www.waveshare.com/raspberry-pi-5-official-27w-psu.htm?sku=25910) | Raspberry Pi 5 Official 27W PSU White EU | Raspberry Pi Foundation | 25910 |
| Cooling | [Aluminum Heatsink with Thermal Pads and Spring-Loaded Push Pins](https://www.waveshare.com/pi5-active-cooler-c.htm) | Pi5-Active-Cooler-C | Waveshare | 26415 |
| Call button | GPIO momentary push button (TBD) | — | — | — |
| Audio | USB speakerphone (TBD) | — | — | — |

## Software Stack

- **OS**: Raspberry Pi OS Lite (Bookworm) — first-party hardware support, built-in overlayfs for SD card longevity, largest Pi kiosk community
- **Display server**: Wayland with labwc compositor — official default since Oct 2024, GPU-accelerated compositing
- **Photo slideshow**: [Immich Kiosk](https://github.com/damongolding/immich-kiosk) — connects to an existing Immich server, runs inside an iframe in the SPA
- **Video calling**: [LiveKit](https://github.com/livekit/livekit) SFU — auto-reconnect, built-in TURN, adaptive bitrate, single Docker container
- **Kiosk shell**: Custom SPA serving as the parent page — WebRTC client in parent, Immich Kiosk in iframe, CSS toggle between modes
- **Camera bridge**: v4l2loopback + GStreamer pipeline — bridges Pi Camera Module 3 (libcamera) to Chromium's getUserMedia()
- **GPIO handler**: Python daemon (gpiozero) — detects button press, sends toggle command to SPA via localhost WebSocket

## Architecture

### Terminal (Each Pi)

```
[RPi OS Lite] + [labwc] + [Chromium --kiosk]
  +-- Custom SPA (localhost:8888)
       +-- Touch shield (blocks all touch events)
       +-- Immich Kiosk iframe (slideshow mode)
       +-- CSS Grid video tiles (call mode, 1-5 auto-layout)
       +-- LiveKit client (always connected, muted when idle)

[systemd services]
  +-- v4l2loopback camera bridge
  +-- GPIO watcher (Python -> WebSocket -> SPA)
  +-- Chromium kiosk (auto-restart on crash)
  +-- Watchdog (memory monitor, scheduled restart)
  +-- ZRAM (compressed swap)
```

### Server (Docker)

```
[Docker host]
  +-- LiveKit server (SFU + built-in TURN)
  +-- Token service (JWT generation for device auth)
  +-- nginx reverse proxy (SSL + Authelia)
```

### Network

```
+-----------------------------------+         +-----------------------------+
|  Docker Server (24/7)             |         |  Pi Endpoint (each of 6)    |
|                                   |         |                             |
|  +-----------------------------+  |  WSS    |  Chromium --kiosk           |
|  |  LiveKit Server             |<-+---------|  SPA: slideshow + video     |
|  |  (SFU + built-in TURN)      |  |         |                             |
|  +-----------------------------+  |  UDP    |  WebRTC media streams       |
|  +-----------------------------+  |<--------|  (outbound only)            |
|  |  nginx + Authelia           |  |         |                             |
|  +-----------------------------+  |         |  GPIO daemon                |
+-----------------------------------+         |  Camera bridge              |
                                              +-----------------------------+
```

Pi endpoints require **zero inbound ports** — all connections are outbound (HTTPS + UDP).

## Research

Key design decisions and the reasoning behind them:

- [Operating System](research/os-choice.md) — why Raspberry Pi OS Lite over 11 other candidates
- [Video Calling Platform](research/video-calling.md) — why LiveKit over 20 other candidates, with Janus as fallback
- [Display Server & Compositor](research/display-server.md) — why Wayland with labwc over X11 and cage
- [Kiosk Switching Architecture](research/kiosk-architecture.md) — why SPA with iframe over tab switching
- [Camera & Audio](research/camera-audio.md) — v4l2loopback bridge for Camera Module 3, USB speakerphone for audio
- [2GB RAM Feasibility](research/ram-feasibility.md) — budget analysis, mitigations, and mandatory hardware validation plan

## Build Guide

The full step-by-step implementation plan is in [docs/build-guide.md](docs/build-guide.md). It covers 6 phases:

1. **Hardware Validation** — go/no-go gate proving 2GB RAM can handle the workload
2. **Server Setup** — LiveKit + token service + SSL on Docker
3. **SPA Development** — the custom kiosk shell (slideshow iframe + video grid + LiveKit client)
4. **Pi System Services** — camera bridge, GPIO daemon, Chromium launcher as systemd services
5. **Reliability Hardening** — watchdog, scheduled restarts, SD card protection
6. **Multi-Device Deployment** — golden image, per-device config, household rollout

## Key Risk

**2GB RAM + 5 software-decoded WebRTC streams is unvalidated.** Pi 5 has no hardware H.264 decode. Estimated 700-1000 MB during a 6-way call, but Chromium leaks memory over hours and nobody has publicly run this workload. ZRAM, adaptive bitrate, and low resolution (360-480p) are planned mitigations. Hardware validation on a real Pi 5 prototype is the **first step before any production code** — see the [validation plan](research/ram-feasibility.md#hardware-validation-plan).
