# Raspberry Pi 5 Photo Frame + Video Conferencing Project

## Hardware

| Component | Model | Notes |
|---|---|---|
| **Board** | Raspberry Pi 5 (4GB or 8GB) | Quad-core Cortex-A76, dual CSI + dual DSI |
| **Display** | Waveshare 10.1" DSI Touch (10.1-DSI-TOUCH-A) | 1280x800, capacitive touch, DSI1 recommended |
| **Camera** | Raspberry Pi Camera Module 3 | IMX708 sensor, autofocus, libcamera-only |
| **Deployment** | Two identical setups | Bidirectional video intercom between locations |

---

## Layer 1: Operating System

### Option 1: Raspberry Pi OS Bookworm — RECOMMENDED

The official OS from the Raspberry Pi Foundation, based on Debian 12. Uses Wayland/labwc compositor by default on Pi 5.

| Pros | Cons |
|------|------|
| Best hardware support out of the box — native dtoverlay for Waveshare DSI, native rpicam-apps/libcamera for Camera Module 3 | Desktop variant includes unused software; Lite variant requires manual display server setup |
| Kiosk mode is well-documented with multiple 2025/2026 guides for labwc + Chromium | Wayland transition friction — some older X11 kiosk scripts need updating |
| Largest community and ecosystem — most tutorials target this OS | No built-in digital signage or photo frame mode |
| Active maintenance with regular updates | |
| Chromium with hardware acceleration for browser-based video calls | |

### Option 2: Ubuntu Desktop for Raspberry Pi (24.04 LTS)

Canonical's Ubuntu with GNOME, officially supporting Pi 5. Full desktop Linux with Snap ecosystem.

| Pros | Cons |
|------|------|
| Full GNOME desktop with good touchscreen support | Camera Module 3 requires building the Pi fork of libcamera from source |
| Ubuntu 24.04 LTS supported until 2029 | GNOME is heavier on RAM/CPU — less headroom for video conferencing |
| Broad software availability via apt + Snaps | DSI display support is less tested; fewer community reports |
| Good Wayland support with hardware acceleration | Kiosk mode is more complex to configure than Pi OS |

### Option 3: DietPi

Highly optimized, minimal Debian-based OS with a software catalog installer. Supports Pi 5 since v9.1.

| Pros | Cons |
|------|------|
| Minimal footprint — maximum resources for applications | Waveshare DSI panels need manual config.txt edits |
| Built-in Chromium kiosk option via `dietpi-software` | Camera support lagged behind Pi OS initially; may need tweaks |
| Highly configurable — no bloat | Smaller community for Pi-specific DSI/camera issues |
| `dietpi-config` for easy hardware toggle | No desktop environment by default |

### Option 4: Fedora (Workstation/IoT)

Cutting-edge Linux with latest kernel and GNOME. Available for aarch64.

| Pros | Cons |
|------|------|
| Latest kernel, PipeWire, Wayland stack | Pi 5 is NOT officially supported — fragile unofficial builds |
| Strong security model (SELinux, OSTree rollbacks) | Waveshare DSI overlays missing from mainline kernel |
| Good general ARM64 support | Camera Module 3 drivers not present in Fedora's kernel |
| Broad package availability | **Not viable for this hardware combination** |

### Option 5: Manjaro ARM

Arch Linux-based rolling release for ARM devices. Multiple desktop editions.

| Pros | Cons |
|------|------|
| Rolling release — always latest packages | Pi 5 is NOT officially supported (only Pi 4/3 images exist) |
| Multiple desktop options (KDE, Sway, XFCE) | Camera modules require legacy libraries with 32-bit issues |
| AUR provides massive software catalog | DSI touchscreen support is undocumented |
| Good performance, lightweight | Rolling release is risky for "set and forget" appliance |

### Option 6: Anthias (by Screenly)

Purpose-built open-source digital signage platform for Raspberry Pi. Runs on Pi OS underneath.

| Pros | Cons |
|------|------|
| Out-of-the-box kiosk display with web management UI | Not designed for interactive apps like video conferencing |
| Pi 5 support confirmed with official images | Limited touch support — display-only focus |
| Schedule content remotely via browser | Microservices (Docker) overhead uses more resources |
| Inherits Pi OS hardware support | Switching dynamically to video calls is outside its design scope |

### OS Comparison

| Criterion | Pi OS Bookworm | Ubuntu Desktop | DietPi | Fedora | Manjaro ARM | Anthias |
|---|---|---|---|---|---|---|
| Pi 5 support | Official | Official | Official | Unofficial | Unofficial | Official |
| Waveshare DSI | Excellent | Fair | Fair | None | Unknown | Good |
| Camera Module 3 | Excellent | Poor (manual) | Fair | None | Poor | N/A |
| Video conferencing | Good | Good | Good | N/A | Possible | Poor |
| Resource efficiency | Good | Poor | Excellent | N/A | Good | Fair |
| Kiosk ↔ video switch | Excellent | Fair | Good | N/A | Unknown | Poor |

### OS Verdict

**Raspberry Pi OS Bookworm** is the clear winner. Only option with excellent out-of-the-box support for all three critical components (Waveshare DSI, Camera Module 3, browser-based conferencing). Use the **Lite** variant for a minimal base, then add labwc + Chromium.

---

## Layer 2: Photo Frame Software

### Option 1: Pi3D PictureFrame — RECOMMENDED

GPU-accelerated photo viewer built on Pi3D (OpenGL ES). Gold standard for Pi photo frames. 2026 Edition available.

| Pros | Cons |
|------|------|
| Best-in-class smooth GPU crossfade transitions | No built-in cloud/album management — relies on folder sync |
| Very lightweight (~100MB RAM), runs without a desktop | Configuration via YAML only, no graphical settings UI |
| Mature, well-documented, active community | No video playback support |
| One-click installer (6 min on Pi 5) | Touchscreen interaction is limited |
| Systemd + MQTT + Home Assistant integration | Python/OpenGL stack can be finicky with display driver changes |
| EXIF rotation, geolocation overlays, matting | |

**Start/Stop:** `systemctl --user start/stop picframe.service` — cleanest programmatic control.

### Option 2: Immich Kiosk

Docker-based slideshow server purpose-built for displaying photos from an Immich instance. Renders in Chromium kiosk mode.

| Pros | Cons |
|------|------|
| Purpose-built for kiosk photo frame use | Requires a separate Immich server (significant infrastructure) |
| Album/people/favorites selection via Immich API | CSS transitions less smooth than GPU-native |
| Burn-in protection, clock overlay, progress bar | Chromium uses ~300-400MB RAM |
| Docker-based, clean deployment | No offline mode — depends on network access |
| Per-device config via URL parameters | Browser-based rendering adds latency |

### Option 3: ImmichFrame

More feature-rich Immich kiosk alternative with multi-account support and advanced layouts.

| Pros | Cons |
|------|------|
| Multi-account / multi-library support | Requires Immich server |
| "On this day" memories, weather overlays, location data | Heavier than Immich Kiosk |
| Split-view for portrait images on landscape displays | Browser-based, same RAM concerns |
| Native Android app also available | Network-dependent, no offline fallback |

### Option 4: MagicMirror2 + MMM-BackgroundSlideshow

Modular smart mirror / info display platform built on Electron. Photo slideshow module displays behind info widgets.

| Pros | Cons |
|------|------|
| Extensible module ecosystem (clock, weather, calendar) | Heavy: Electron + Node.js uses 400-600MB RAM |
| Can combine photo frame with info dashboard | Overkill if you only want photos |
| Large community, hundreds of modules | CSS transitions, not GPU-accelerated |
| Good touchscreen support via modules | Setup complexity: Node.js, npm, module configs |

### Option 5: feh

Fast, lightweight X11 image viewer. Simplest possible slideshow — single command-line tool.

| Pros | Cons |
|------|------|
| Extremely lightweight (~30MB RAM) | **No transitions at all** — hard cut only |
| Dead simple setup, single command | No overlays (clock, date, location) |
| Supports all common formats + EXIF rotation | X11 only — issues on Wayland |
| Trivial to start/stop programmatically | No touch interaction or remote control |

### Option 6: PhotoPrism (with Chromium Kiosk)

Self-hosted AI-powered photo management platform. Built-in slideshow displayed via browser.

| Pros | Cons |
|------|------|
| Best album management: AI categorization, face recognition | Slideshow is a secondary feature, not purpose-built |
| Self-hosted Google Photos alternative | Very resource-heavy: needs 3-4GB RAM |
| Supports RAW formats, video, live photos | Basic/limited transition effects |
| Excellent for photo organization | No dedicated kiosk mode |

### Photo Frame Comparison

| Feature | Pi3D | Immich Kiosk | ImmichFrame | MagicMirror | feh | PhotoPrism |
|---|---|---|---|---|---|---|
| Transition quality | Excellent (GPU) | Good (CSS) | Good (CSS) | Good (CSS) | None | Basic |
| RAM usage | ~100MB | ~350MB | ~400MB | ~500MB | ~30MB | ~3GB |
| Album management | Folder-based | Immich API | Immich API | Folder/module | Folder | AI-powered |
| Setup complexity | Low | Medium | Medium | High | Very Low | High |
| Start/stop ease | systemd | docker | docker | pm2 | kill | docker+browser |
| Offline capable | Yes | No | No | Yes | Yes | Yes |

### Photo Frame Verdict

**Pi3D PictureFrame** for the best transitions and lightest resource usage. Pair with **rclone** to sync a cloud photo album to a local folder. Easiest to start/stop for switching to video calls.

If you already run or plan to run **Immich**, then **Immich Kiosk** is the best browser-based option.

---

## Layer 3: Video Conferencing

### Critical Note: Camera Module 3 + Browsers

The Camera Module 3 only works with libcamera — browsers (Chromium) don't natively see it. Workarounds:

1. **PipeWire + libcamera** — Firefox supports this; Chromium is improving
2. **v4l2loopback** — GStreamer pipes libcamera to a virtual V4L2 device for Chromium
3. **LD_PRELOAD shim** — Launch Chromium with `LD_PRELOAD=/usr/lib/aarch64-linux-gnu/libcamera/v4l2-compat.so`
4. **Native app** — Bypass browser entirely, talk to libcamera directly (GStreamer, pi-webrtc)

Browser-based options (1, 2, 5) need workarounds 1-3. Native options (3, 4, 6) avoid this entirely.

### Option 1: Jitsi Meet (Browser-Based)

Open-source video conferencing. Pi runs Chromium connecting to a Jitsi room (self-hosted or public).

| Pros | Cons |
|------|------|
| Mature project with large community | Camera Module 3 requires workarounds for browser |
| No software install beyond Chromium | Chromium uses 80-90% CPU during calls |
| Encrypted (SRTP/DTLS) | Requires a Jitsi server — not true P2P |
| Self-hosted or free public servers | Browser overhead adds latency (200-500ms) |
| | UI is overkill for 2-device intercom |

### Option 2: Jami (Fully P2P)

GNU-backed, fully decentralized peer-to-peer voice/video/chat. No server required.

| Pros | Cons |
|------|------|
| Fully peer-to-peer — no server infrastructure | Camera Module 3 compatibility unverified |
| End-to-end encrypted by design | Snap package on Pi is heavy and slow to start |
| Desktop GUI client for touchscreen | NAT traversal can be unreliable without TURN |
| D-Bus API for programmatic call initiation | UI is a general chat app, not optimized for intercom |
| Free and open source (GPLv3) | Limited Pi 5-specific documentation |

### Option 3: Custom GStreamer + WebRTC (webrtcbin) — BEST QUALITY

Build a custom bidirectional video call using GStreamer's `webrtcbin`. Native, non-browser solution.

| Pros | Cons |
|------|------|
| Direct libcamera integration — no workarounds needed | Significant development effort (building from scratch) |
| Lowest latency (<100ms LAN) with hardware H.264 encoding | Must implement/host a signaling server |
| Complete control over resolution, bitrate, codec, UI | Must handle NAT traversal (STUN/TURN) yourself |
| Lightweight — no browser overhead | No pre-built UI — must create call interface |
| Bidirectional examples exist (C, Python, Rust) | Debugging GStreamer pipelines is complex |
| Can integrate with Qt/GTK touchscreen UI | Ongoing maintenance burden |

### Option 4: pi-webrtc (TzuHuanTai/RaspberryPi-WebRTC) — RECOMMENDED

Purpose-built native WebRTC for Raspberry Pi with direct libcamera support and hardware encoding.

| Pros | Cons |
|------|------|
| Purpose-built for Pi + Camera Module 3 / libcamera | Designed as one-way camera — bidirectional needs modification |
| Native P2P WebRTC with hardware acceleration | Not a complete video call solution out of the box |
| Supports MQTT and WebSocket signaling | Smaller community than established projects |
| Lightweight, no browser needed | Needs custom UI for touchscreen intercom |
| Active development on GitHub | Signaling server (MQTT broker) still needed |
| Easy config via command-line flags | |

### Option 5: Chromium Kiosk + Hosted WebRTC Service (Daily.co, Whereby)

Chromium in kiosk mode connecting to a hosted WebRTC service. Minimal development.

| Pros | Cons |
|------|------|
| Minimal development — just point Chromium to a URL | Camera Module 3 requires workarounds |
| Many services offer free tiers for 2-party calls | Chromium resource usage is heavy (500MB+ RAM) |
| UI handled by service provider | Internet dependency — no pure LAN operation |
| Same browser for photo frame and video call | Less control over quality and reliability |
| | Privacy concerns with commercial services |

### Option 6: LiveKit (Self-Hosted WebRTC SFU)

Modern open-source WebRTC server in Go. Connect via browser or native Python/Node SDK.

| Pros | Cons |
|------|------|
| Modern, actively developed with strong community | Requires a LiveKit server running somewhere |
| Python SDK with native ARM64 — can bypass browser | More complex infrastructure than P2P |
| Excellent API — rooms, tokens, webhooks | Browser client still needs camera workarounds |
| Supports recording, data channels | Overkill SFU architecture for 2-device intercom |
| Well-documented developer experience | Custom native client requires dev effort |

### Video Conferencing Comparison

| Criteria | Jitsi | Jami | GStreamer+WebRTC | pi-webrtc | Chromium+Service | LiveKit |
|---|---|---|---|---|---|---|
| Camera Module 3 | Workaround | Workaround | Native | Native | Workaround | Native (Python) |
| Dev effort | Low | Low | High | Medium | Very Low | Medium |
| Latency (LAN) | 200-500ms | <100ms | <100ms | <100ms | 200-500ms | 100-300ms |
| Server required | Yes | No (P2P) | Signaling only | Signaling only | Yes | Yes |
| Resource usage | High | Medium | Low | Low | High | Low-Medium |
| Bidirectional | Built-in | Built-in | Must build | Must extend | Built-in | Built-in |
| Touchscreen UI | Web UI | GTK UI | Must build | Must build | Web UI | Must build |

### Video Conferencing Verdict

**Two tiers depending on your comfort with development:**

- **Minimal development:** **Jami** — closest to turnkey with no server, has GUI, D-Bus API for programmatic triggering. Camera workaround needed.
- **Best quality / lowest latency:** **pi-webrtc** or **GStreamer + webrtcbin** — native Camera Module 3 support, sub-100ms latency, minimal resources. Needs custom UI and signaling.
- **Middle ground:** **LiveKit with Python SDK** — modern framework, native ARM64, avoids browser. Needs a server and custom client.

---

## Layer 4: Hardware Integration

### config.txt

```ini
# Display driver (required for both DSI display and libcamera)
dtoverlay=vc4-kms-v3d

# Waveshare 10.1" DSI display on DSI1
dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a

# Camera auto-detection (Camera Module 3 / IMX708)
camera_auto_detect=1
```

In `/boot/firmware/cmdline.txt`, append:
```
consoleblank=0
```

### Key Integration Facts

- **DSI + CSI coexistence:** Both are physically separate MIPI lanes on Pi 5. They share an I2C bus for control, but `vc4-kms-v3d` + `libcamera` is the correct (working) combination.
- **Pi 5 camera cable:** Requires a 15-pin to 22-pin adapter cable (not included with Camera Module 3).
- **Display output name:** Under Wayland, the Waveshare appears as **DSI-2**.
- **Touch modes:** Mouse mode (click, long-press, double-click) or Multi-touch mode (swipe, gestures). Configurable in Screen Configuration.

### Touchscreen Rotation Calibration

If rotating the display, create `/etc/udev/rules.d/99-touch-calibration.rules`:

| Rotation | Matrix |
|----------|--------|
| 0° | Default, no rule needed |
| 90° | `0 -1 1 1 0 0` |
| 180° | `-1 0 1 0 -1 1` |
| 270° | `0 1 0 -1 0 1` |

### Known Issues

- Waveshare touch occasionally fails on boot ([linux#6538](https://github.com/raspberrypi/linux/issues/6538)) — reboot resolves it
- Camera not detected is almost always a cable issue (clip not fully engaged, wrong cable type)
- `vcgencmd display_power` does NOT work on Bookworm Wayland — use `wlr-randr` instead

---

## Layer 5: System Architecture — How It All Ties Together

### Recommended Stack

```
┌─────────────────────────────────────────────────────┐
│                  Raspberry Pi OS Lite (Bookworm)     │
├─────────────────────────────────────────────────────┤
│  Compositor: labwc (Wayland)                        │
├──────────────────────┬──────────────────────────────┤
│  MODE A: Photo Frame │  MODE B: Video Call          │
│  Pi3D PictureFrame   │  pi-webrtc + Qt/GTK UI      │
│  (systemd service)   │  OR Jami (if turnkey)        │
│  rclone album sync   │  OR Chromium + Jitsi/service │
├──────────────────────┴──────────────────────────────┤
│  Switching: systemd service toggle + wlrctl         │
├─────────────────────────────────────────────────────┤
│  Hardware: libcamera (Camera Module 3)              │
│           vc4-kms-v3d (Waveshare DSI + GPU)         │
└─────────────────────────────────────────────────────┘
```

### Mode Switching Logic

A control script handles the transition between photo frame and video call:

```bash
#!/bin/bash
# switch-mode.sh — toggle between photo frame and video call

CURRENT_MODE=$(cat /tmp/current-mode 2>/dev/null || echo "photoframe")

if [ "$1" = "call" ] || [ "$CURRENT_MODE" = "photoframe" ]; then
    # Stop photo frame, start video call
    systemctl --user stop picframe.service
    systemctl --user start videocall.service
    echo "videocall" > /tmp/current-mode
elif [ "$1" = "photoframe" ] || [ "$CURRENT_MODE" = "videocall" ]; then
    # Stop video call, start photo frame
    systemctl --user stop videocall.service
    systemctl --user start picframe.service
    echo "photoframe" > /tmp/current-mode
fi
```

This can be triggered by:
- A **touchscreen button** (overlay widget via labwc)
- A **physical GPIO button**
- An **MQTT message** from the other Pi ("incoming call")
- A **cron schedule**

### Screen Power Management

```bash
# Turn display off at night (via cron)
wlr-randr --output DSI-2 --off

# Turn display on in the morning
wlr-randr --output DSI-2 --on
```

Requires environment variables in cron:
```
WAYLAND_DISPLAY=wayland-1
XDG_RUNTIME_DIR=/run/user/1000
```

### Album Sync

Use **rclone** to sync a cloud album to the local photo directory:

```bash
# Sync Google Photos album (or Dropbox, S3, etc.)
rclone sync remote:PhotoFrameAlbum /home/pi/photos --transfers 4
```

Schedule via cron (e.g., every 30 minutes).

### Network Requirements

| Scenario | Requirement |
|---|---|
| Both Pis on same LAN | Minimal — P2P or local signaling server |
| Pis on different networks | STUN/TURN server needed for NAT traversal, or use a hosted service |
| Photo sync | Internet access for cloud album sync |

---

## Decision Matrix: Recommended Combinations

### Combo A: Minimal Development (Turnkey)

| Layer | Choice | Why |
|---|---|---|
| OS | Raspberry Pi OS Bookworm Lite | Best hardware support |
| Photo frame | Pi3D PictureFrame | GPU transitions, lightweight, systemd |
| Video call | Jami | No server, P2P, has GUI, D-Bus API |
| Album sync | rclone | Works with any cloud provider |
| Switching | systemd service toggle | Simple, reliable |

**Trade-off:** Jami's camera support may need v4l2loopback workaround. NAT traversal can be flaky across networks.

### Combo B: Best Quality (Some Development)

| Layer | Choice | Why |
|---|---|---|
| OS | Raspberry Pi OS Bookworm Lite | Best hardware support |
| Photo frame | Pi3D PictureFrame | GPU transitions, lightweight, systemd |
| Video call | pi-webrtc + simple Qt UI | Native libcamera, <100ms latency, HW encoding |
| Signaling | Mosquitto MQTT broker (on one Pi) | Lightweight, pi-webrtc supports it natively |
| Album sync | rclone | Works with any cloud provider |
| Switching | systemd + MQTT "ring" notification | Other Pi sends MQTT → auto-switches to call |

**Trade-off:** Requires building a simple UI and extending pi-webrtc for bidirectional use. Best result though.

### Combo C: Cloud-Integrated (If You Run Immich)

| Layer | Choice | Why |
|---|---|---|
| OS | Raspberry Pi OS Bookworm Desktop | Desktop for browser-based stack |
| Photo frame | Immich Kiosk (Chromium) | Direct Immich album integration |
| Video call | Chromium + Jitsi Meet (self-hosted) | Same browser, just switch URL/tab |
| Album sync | Immich handles it | Photos managed in Immich |
| Switching | Chromium tab switch via `wtype` | Both apps are browser tabs |

**Trade-off:** Heavier resource usage (Chromium always running). Camera needs v4l2loopback. Needs Immich server + Jitsi server.

---

## Sources

### OS
- [Raspberry Pi OS Downloads](https://www.raspberrypi.com/software/)
- [DietPi Pi 5 Support](https://www.tomshardware.com/raspberry-pi/raspberry-pi-5-support-arrives-with-dietpi-v91)
- [Anthias Pi 5 Support](https://www.screenly.io/blog/2024/12/28/adding-anthias-support-for-pi5/)
- [Fedora Pi 5 Status](https://discussion.fedoraproject.org/t/raspberry-pi-5-status/131251)

### Photo Frame
- [Pi3D PictureFrame 2026 Installer](https://www.thedigitalpictureframe.com/install-the-pi3d-pictureframe-software-with-one-click-2025-edition-raspberry-pi-2-3-4-5/)
- [Immich Kiosk Docs](https://docs.immichkiosk.app/)
- [ImmichFrame Overview](https://pixelunion.eu/blog/immichframe/)
- [MagicMirror](https://magicmirror.builders/)

### Video Conferencing
- [pi-webrtc GitHub](https://github.com/TzuHuanTai/RaspberryPi-WebRTC)
- [GStreamer webrtcbin Docs](https://gstreamer.freedesktop.org/documentation/webrtc/index.html)
- [LiveKit Self-Hosting](https://docs.livekit.io/transport/self-hosting/)
- [Jami on Raspberry Pi](https://snapcraft.io/install/jami/raspbian)
- [Jitsi on Raspberry Pi](https://jitsi.support/how-to/jitsi-on-raspberry-pi/)

### Hardware Integration
- [Waveshare 10.1-DSI-TOUCH-A Wiki](https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A)
- [Pi Camera Software Docs](https://www.raspberrypi.com/documentation/computers/camera_software.html)
- [Pi Kiosk Mode Tutorial](https://www.raspberrypi.com/tutorials/how-to-use-a-raspberry-pi-in-kiosk-mode/)
- [labwc Documentation](https://labwc.github.io/)
