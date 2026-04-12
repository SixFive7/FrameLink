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
| Mic array | [ReSpeaker XVF3800 USB 4-Mic Array](https://thepihut.com/products/respeaker-xmos-xvf3800-ai-powered-4-mic-array-for-clear-voice-even-in-noise) | ReSpeaker XVF3800 | Seeed Studio | 101991441 |
| Speaker (option A) | [Adafruit Mono Enclosed Speaker 3W 4Ω](https://www.digikey.nl/nl/products/detail/adafruit-industries-llc/3351/6612456) | 3351 | Adafruit | 3351 |
| Speaker (option B) | [PUI Audio Enclosed Oval Speaker 3W 4Ω](https://www.digikey.nl/nl/products/detail/pui-audio-inc/AS07104PO-LW152-R/4835136) | AS07104PO-LW152-R | PUI Audio | AS07104PO-LW152-R |
| Speaker cable | [Adafruit JST PH 2-Pin Cable 100mm](https://www.digikey.nl/nl/products/detail/adafruit-industries-llc/261/5353586) | 261 | Adafruit | 261 |
| Call button | GPIO momentary push button (TBD) | — | — | — |
| Network cable | RJ45 Cat5e/Cat6 Ethernet cable — preferred over WiFi for reliability | — | — | — |

## Software Stack

- **OS**: Raspberry Pi OS Lite (Trixie, Debian 13) — first-party hardware support, built-in overlayfs for SD card longevity, largest Pi kiosk community
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

[USB devices]
  +-- ReSpeaker XVF3800 (4-mic array + speaker amp, USB audio)
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
- [Camera & Audio](research/camera-audio.md) — v4l2loopback bridge for Camera Module 3, ReSpeaker XVF3800 mic array + enclosed speaker for audio
- [2GB RAM Feasibility](research/ram-feasibility.md) — budget analysis, mitigations, and mandatory hardware validation plan

## Build Guide

The build is split into one hardware guide and ten software guides, each stepping through validated instructions only:

1. [Hardware assembly](docs/1-hardware-build-guide.md) — Pi + display + camera + speaker
2. [SD card flashing & first boot](docs/2-sd-flash-first-boot.md) — Trixie Lite, SSH, base updates
3. [Hardware configuration](docs/3-hardware-configuration.md) — DSI display, rotation, kernel parameters
4. [Kiosk base](docs/4-kiosk-base.md) — labwc + Chromium fullscreen
5. [Camera bridge](docs/5-camera-bridge.md) — v4l2loopback + libcamera pipeline
6. [WebRTC hardware validation](docs/6-webrtc-validation.md) — the 2 GB go/no-go gate
7. [LiveKit server deployment](docs/7-livekit-server.md) — Docker, token service, SSL
8. [Kiosk SPA](docs/8-spa.md) — slideshow iframe, video grid, LiveKit client
9. [GPIO button daemon](docs/9-gpio-button.md) — Python gpiozero → WebSocket toggle
10. [systemd services & reliability](docs/10-systemd-and-reliability.md) — services, watchdog, SD protection
11. [Multi-device deployment](docs/11-multi-device-deploy.md) — golden image, per-device identity, household rollout

## To Do

- [ ] **A/B test speakers** — compare Adafruit 3351 vs PUI AS07104PO-LW152-R for voice clarity, volume, and AEC performance with the XVF3800. Pick one.
- [ ] **Design 3D-printed case** — wall-mounted enclosure holding display, Pi, XVF3800 + camera assembly (top), and speaker (bottom). Three acoustically separated chambers for AEC. See [case design notes](research/camera-audio.md#aec-acoustic-echo-cancellation-design).
- [ ] **Tune XVF3800 AEC** — adjust `AUDIO_MGR_SYS_DELAY`, amp enable (`GPO_WRITE_VALUE 31 0`), and ALSA mixer levels during Phase 1 hardware validation.
- [ ] **Source remaining parts** — USB-C to USB-A cables (×2), GPIO buttons (×2), microSD cards (×2), foam tape for speaker isolation.
- [ ] **If neither enclosed speaker is loud/clear enough** — consider Tectonic TEBM28C10-4/B BMR driver with 3D-printed sealed chamber (~80-110ml). See [speaker research](research/camera-audio.md#speaker-selection).

## Key Risk

**2GB RAM + 5 software-decoded WebRTC streams is unvalidated.** Pi 5 has no hardware H.264 decode. Estimated 700-1000 MB during a 6-way call, but Chromium leaks memory over hours and nobody has publicly run this workload. ZRAM, adaptive bitrate, and low resolution (360-480p) are planned mitigations. Hardware validation on a real Pi 5 prototype is the **first step before any production code** — see the [validation plan](research/ram-feasibility.md#hardware-validation-plan).
