# Display Server & Compositor Research

## Decision

**Wayland with labwc** is the display server and compositor for FrameLink's kiosk setup.

## Requirements

The compositor must run Chromium in fullscreen kiosk mode on a Raspberry Pi 5 with a 10.1" DSI touchscreen. It must support WebRTC (getUserMedia, video decode/encode) in Chromium. It must hide the cursor, prevent window management escape, and work with a single-page application architecture. GPU-accelerated compositing is strongly preferred to reduce CPU load on the constrained 2GB system.

## Why labwc on Wayland

Three factors made this the clear winner:

1. **Official Raspberry Pi recommendation.** labwc has been the default Wayland compositor on Raspberry Pi OS since October 2024. The official Raspberry Pi kiosk tutorial at raspberrypi.com uses labwc with Chromium `--kiosk`. This means it receives the most testing, the most bug fixes, and the most community documentation for Pi 5 + DSI display setups.

2. **GPU-accelerated compositing.** Chromium on Pi 5 under Wayland gets GPU-accelerated CSS compositing, WebGL, and page rendering via the VideoCore VII GPU (OpenGL ES 3.1, Vulkan 1.2). Under X11, the Raspberry Pi Foundation states that "X11 does not work well with hardware acceleration" and that development is focused on DRM/KMS and Wayland. Note: video decode remains software-only regardless of display server (Pi 5 dropped H.264 hardware decode — see [RAM feasibility research](ram-feasibility.md)).

3. **No need for X11 tools.** The SPA architecture (see [kiosk architecture research](kiosk-architecture.md)) uses a single Chromium window with a single page. There is no tab switching, no window switching, and no need for `xdotool` or any X11-specific tooling. Even if tab switching were needed, `wtype` (Wayland equivalent of xdotool) and CDP (Chrome DevTools Protocol) both work under Wayland.

## Candidates Evaluated

### labwc (Wayland) -- chosen

Wayland stacking compositor based on wlroots. Default on Raspberry Pi OS since October 2024. Autostart config at `~/.config/labwc/autostart`.

### cage (Wayland)

Purpose-built Wayland kiosk compositor based on wlroots. Runs a single application fullscreen with no window management. Hides cursor after inactivity.

**Why not chosen:** While cage is a natural fit for single-app kiosk use, it has less community support on Pi than labwc. The official RPi kiosk tutorial does not use cage. labwc achieves the same kiosk behavior with Chromium's `--kiosk` flag and has the advantage of being the default — meaning more testing, more documentation, and more bug reports specific to Pi 5 + DSI displays.

### wayfire (Wayland)

3D Wayland compositor. Was the default on Raspberry Pi OS before labwc.

**Why not chosen:** Deprecated on Raspberry Pi OS. Replaced by labwc in October 2024.

### matchbox-wm (X11)

Minimal X11 window manager commonly used in embedded kiosk setups.

**Why not chosen:** X11 does not provide GPU-accelerated compositing in Chromium on Pi 5. The Raspberry Pi Foundation's development focus is on Wayland/DRM/KMS. matchbox-wm is a legacy fallback path with no future investment.

### weston (Wayland)

Reference Wayland compositor from freedesktop.org. Used in some Yocto/embedded setups.

**Why not chosen:** Not the default on Pi OS. Less Pi-specific community support than labwc. No advantages over labwc for kiosk use.

## Kiosk Setup

### Installation

```bash
sudo apt install labwc chromium
```

### Autostart (cage-equivalent kiosk behavior)

File: `~/.config/labwc/autostart`

```bash
chromium \
  --kiosk \
  --noerrdialogs \
  --disable-infobars \
  --disable-session-crashed-bubble \
  --no-first-run \
  --auto-accept-camera-and-microphone-capture \
  --autoplay-policy=no-user-gesture-required \
  --disable-background-timer-throttling \
  --disable-renderer-backgrounding \
  --use-fake-ui-for-media-stream \
  http://localhost:8888
```

### Chromium Flags Explained

| Flag | Purpose |
|---|---|
| `--kiosk` | Fullscreen, no address bar, no close button |
| `--noerrdialogs` | Suppress error dialogs |
| `--disable-infobars` | Hide info bars (e.g., "Chrome is being controlled by automated software") |
| `--disable-session-crashed-bubble` | Suppress "Chrome didn't shut down correctly" dialog |
| `--no-first-run` | Skip first-run wizard |
| `--auto-accept-camera-and-microphone-capture` | Auto-grant camera/mic permissions (no prompt) |
| `--autoplay-policy=no-user-gesture-required` | Allow autoplay without user gesture |
| `--disable-background-timer-throttling` | Prevent timer throttling (important for WebRTC) |
| `--disable-renderer-backgrounding` | Prevent renderer deprioritization |
| `--use-fake-ui-for-media-stream` | Hide the camera/mic recording indicator |

> On Trixie, Chromium 130+ enables PipeWire camera capture by default, so the previously used `--enable-features=WebRTCPipeWireCapturer` flag has been dropped.

### Known Issues: Pi 5 + DSI Display

| Issue | Fix |
|---|---|
| Touch coordinate doubling | Disable specific framebuffer settings in config.txt |
| Screen rotation crashes labwc | Use `panel_orientation` in config.txt instead of `rotate` |
| Touch rotation independent of display | Configure separately via `panel_orientation` |
| Chromium exits kiosk on monitor power cycle | Handle DPMS events |
