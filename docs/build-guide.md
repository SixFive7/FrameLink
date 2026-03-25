# FrameLink Build Guide

Step-by-step instructions for building a FrameLink unit from scratch. Each phase builds on the previous one. Do not skip ahead — Phase 1 is a go/no-go gate for the entire project.

For the reasoning behind each technical choice, see the [research documents](../research/).

---

## Phase 1: Hardware Validation (Go/No-Go Gate)

> **Purpose:** Prove that the 2GB Pi 5 can handle 5 incoming WebRTC video streams before writing any production code. This is the single highest risk in the project. If this phase fails, either switch to Pi 5 4GB or reduce the maximum participant count.

### 1.1 Assemble the Pi

1. Attach the heatsink to the Pi 5 with thermal pads and push pins
2. Connect the 10.1" DSI display via the DSI ribbon cable
3. Connect the Camera Module 3 via the camera ribbon cable
4. Insert a 32GB (or larger) microSD card
5. Connect the 27W USB-C power supply

### 1.2 Flash Raspberry Pi OS Lite

1. Download [Raspberry Pi Imager](https://www.raspberrypi.com/software/) on your computer
2. Select **Raspberry Pi OS Lite (64-bit, Bookworm)** — no desktop environment
3. In the imager settings (gear icon), configure:
   - Hostname: `framelink-01`
   - Enable SSH with password or public key
   - Set username and password
   - Configure WiFi (SSID, password, country)
   - Set locale and timezone
4. Flash to the microSD card
5. Insert card into Pi, power on, SSH in

### 1.3 Initial System Setup

```bash
# Update system
sudo apt update && sudo apt full-upgrade -y

# Set up ZRAM (critical for 2GB — see research/ram-feasibility.md)
sudo apt install zram-tools -y
# Edit /etc/default/zramswap: set PERCENTAGE=50 (or higher)
sudo systemctl enable zramswap
sudo systemctl start zramswap

# Verify ZRAM is active
swapon --show
# Should show /dev/zram0 with ~1GB
```

### 1.4 Install Kiosk Stack

```bash
# Install labwc compositor and Chromium
sudo apt install labwc chromium-browser -y

# Enable console autologin (required for labwc to start)
sudo raspi-config nonint do_boot_behaviour B2

# Create labwc autostart
mkdir -p ~/.config/labwc
cat > ~/.config/labwc/autostart << 'EOF'
chromium-browser \
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
  --enable-features=WebRTCPipeWireCapturer \
  https://webrtc.github.io/samples/
EOF

# Reboot — should boot into Chromium fullscreen on the DSI display
sudo reboot
```

**Checkpoint:** Chromium loads fullscreen on the DSI display. Touch works. No desktop environment visible.

### 1.5 Test Camera Bridge

```bash
# Install camera bridge dependencies
sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-libcamera v4l2loopback-dkms -y

# Load v4l2loopback module
sudo modprobe v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1

# Make persistent
echo "v4l2loopback" | sudo tee /etc/modules-load.d/v4l2loopback.conf
sudo tee /etc/modprobe.d/v4l2loopback.conf << 'EOF'
options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1
EOF

# Test the pipeline manually
gst-launch-1.0 libcamerasrc ! "video/x-raw,width=1280,height=720,framerate=30/1" ! videoconvert ! v4l2sink device=/dev/video8

# In another terminal, verify the device exists
ls -la /dev/video8
```

**Checkpoint:** `/dev/video8` exists and GStreamer pipeline runs without errors. Open `chrome://settings/content/camera` in Chromium (temporarily disable kiosk mode) — "Chromium device" should appear in the dropdown.

### 1.6 WebRTC Stress Test

This is the critical validation. You need 5 simulated video streams hitting the Pi.

**Option A: Use a public WebRTC test service**

Open a multi-party WebRTC test page in Chromium on the Pi and join from 5 other devices (phones, laptops) sending 480p video. Monitor the Pi:

```bash
# In SSH session, monitor RAM and CPU while the call runs
watch -n 2 'free -m && echo "---" && top -bn1 | head -20'
```

**Option B: Deploy a test LiveKit server (recommended)**

```bash
# On your Docker server:
docker run --rm -p 7880:7880 -p 7881:7881 -p 50000-50020:50000-50020/udp \
  -e LIVEKIT_KEYS="devkey:secret" \
  livekit/livekit-server --dev

# Generate test tokens (install livekit-cli on your machine first)
# Create 6 tokens for 6 participants in room "test"
livekit-cli create-token --api-key devkey --api-secret secret --join --room test --identity pi-01
livekit-cli create-token --api-key devkey --api-secret secret --join --room test --identity phone-01
# ... repeat for phone-02 through phone-05
```

Join the room from the Pi + 5 other devices. Let it run for **4 hours minimum**.

**Pass criteria:**
- Total RAM stays below 1.5 GB
- CPU stays below 80% average
- No OOM kills
- No Chromium crashes
- Video remains smooth (no persistent freezing)

**If it fails:** Try 360p or 240p. Try 3 participants instead of 5. If nothing works at 2GB, the project needs Pi 5 4GB units.

---

## Phase 2: Server Setup

> **Purpose:** Get LiveKit running on your Docker server with proper auth, SSL, and TURN.

### 2.1 LiveKit Server

Create the project directory on your Docker host:

```
server/
  docker-compose.yml
  livekit.yaml
  token-service/
    Dockerfile
    server.js (or server.py)
```

`docker-compose.yml`:
```yaml
services:
  livekit:
    image: livekit/livekit-server:latest
    ports:
      - "7880:7880"
      - "7881:7881"
      - "443:443/udp"
      - "50000-50100:50000-50100/udp"
    volumes:
      - ./livekit.yaml:/etc/livekit.yaml
    command: --config /etc/livekit.yaml
    restart: unless-stopped
```

`livekit.yaml`:
```yaml
port: 7880
rtc:
  port_range_start: 50000
  port_range_end: 50100
  use_external_ip: true
  tcp_port: 7881
turn:
  enabled: true
  udp_port: 443
  tls_port: 5349
keys:
  your-api-key: your-api-secret
```

```bash
docker compose up -d
```

### 2.2 Token Service

LiveKit requires JWT tokens for client authentication. Build a minimal token generation service that issues tokens for known device identities. This can be a simple HTTP endpoint:

```
GET /token?identity=framelink-01&room=family
-> returns JWT token
```

Secure this endpoint (e.g., behind Authelia, or with a shared secret). Each Pi will request a token at boot.

### 2.3 Reverse Proxy & SSL

Put LiveKit behind your existing nginx reverse proxy with SSL. LiveKit's WebSocket endpoint (`/`) needs to be proxied with WebSocket upgrade support. The TURN/UDP ports must be exposed directly (cannot be proxied).

### 2.4 Verify Server

```bash
# From any machine, test the HTTP API
curl http://your-server:7880

# Test WebSocket connectivity
# Use livekit-cli to join a room and verify it works
livekit-cli join-room --url ws://your-server:7880 --api-key your-api-key --api-secret your-api-secret --room test --identity test-user
```

**Checkpoint:** LiveKit server is running. You can join a room from livekit-cli. TURN is working (test from a network that requires TURN — e.g., a phone on mobile data).

---

## Phase 3: SPA Development

> **Purpose:** Build the custom single-page application that runs in Chromium on each Pi. This is the brain of the terminal — it manages the slideshow iframe, video grid, LiveKit connection, and GPIO commands.

### 3.1 Project Structure

```
spa/
  index.html        # Main SPA page
  app.js            # Mode toggle, LiveKit client, WebSocket listener
  style.css         # Video grid layout, touch shield
  package.json      # If using npm for livekit-client
```

### 3.2 Core HTML Structure

```html
<!DOCTYPE html>
<html>
<head>
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="style.css">
</head>
<body>
  <!-- Touch shield: blocks all touch events in both modes -->
  <div id="touch-shield"></div>

  <!-- Slideshow mode: Immich Kiosk in iframe -->
  <iframe id="kiosk-iframe" src="http://localhost:3000"></iframe>

  <!-- Call mode: video grid (hidden by default) -->
  <div id="video-grid"></div>

  <script src="app.js"></script>
</body>
</html>
```

### 3.3 Key Implementation Tasks

1. **LiveKit client connection** — connect to the LiveKit room at page load with tracks muted. Reconnect on failure with infinite retry.
2. **Track subscription handling** — when a remote participant publishes, attach their video track to a new `<video>` element in the grid. Remove it when they leave.
3. **Mode toggle** — `enableCall()` unhides the video grid, hides the iframe, unmutes local tracks. `disableCall()` reverses this. Optionally destroy/recreate the iframe to free memory.
4. **GPIO WebSocket listener** — listen on `ws://localhost:8889` for toggle commands from the GPIO daemon.
5. **Auto-answer** — when the first remote participant joins while in slideshow mode, automatically switch to call mode (for the "family calls elder" use case).
6. **Touch shield** — a fixed, full-viewport div at z-index 999 that absorbs all touch events. Always active.

### 3.4 Serve the SPA

Use a lightweight static file server running on the Pi:

```bash
# Option A: Python (already on the system)
python3 -m http.server 8888 --directory /home/framelink/spa/

# Option B: Node.js serve, caddy, or any static server
```

Run as a systemd service that starts before Chromium.

### 3.5 Test Locally

Before deploying to the Pi, test the SPA in a desktop browser:
1. Verify Immich Kiosk loads in the iframe
2. Verify LiveKit connection and video grid with test participants
3. Verify mode toggle works
4. Verify touch shield blocks interaction

Then deploy to the Pi and test with Chromium in kiosk mode.

**Checkpoint:** SPA loads in Chromium on Pi. Slideshow displays. Joining a LiveKit room from another device shows their video. Mode toggle works.

---

## Phase 4: Pi System Services

> **Purpose:** Set up the systemd services that make the Pi function as an appliance — camera bridge, GPIO daemon, Chromium launcher, and reliability services.

### 4.1 Camera Bridge Service

`/etc/systemd/system/camera-bridge.service`:
```ini
[Unit]
Description=v4l2loopback camera bridge
After=network.target

[Service]
ExecStart=/usr/bin/gst-launch-1.0 libcamerasrc ! "video/x-raw,width=1280,height=720,framerate=30/1" ! videoconvert ! v4l2sink device=/dev/video8
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable camera-bridge
sudo systemctl start camera-bridge
```

### 4.2 GPIO Daemon

A Python script using `gpiozero` that watches the button pin and sends toggle commands via WebSocket:

`/home/framelink/gpio-daemon.py`:
```python
from gpiozero import Button
import asyncio
import websockets

BUTTON_PIN = 17  # Adjust to your wiring
WS_URL = "ws://localhost:8889"

# ... button press detection -> WebSocket send "toggle"
```

`/etc/systemd/system/gpio-daemon.service`:
```ini
[Unit]
Description=FrameLink GPIO daemon
After=network.target

[Service]
ExecStart=/usr/bin/python3 /home/framelink/gpio-daemon.py
Restart=always
RestartSec=5
User=framelink

[Install]
WantedBy=multi-user.target
```

### 4.3 SPA Server Service

`/etc/systemd/system/spa-server.service`:
```ini
[Unit]
Description=FrameLink SPA static server
After=network.target

[Service]
ExecStart=/usr/bin/python3 -m http.server 8888 --directory /home/framelink/spa
Restart=always
RestartSec=5
User=framelink

[Install]
WantedBy=multi-user.target
```

### 4.4 Chromium Kiosk Service

Instead of using labwc autostart directly, wrap Chromium in a systemd service for crash recovery:

`/etc/systemd/system/chromium-kiosk.service`:
```ini
[Unit]
Description=Chromium kiosk
After=spa-server.service camera-bridge.service
Wants=spa-server.service camera-bridge.service

[Service]
Environment=XDG_RUNTIME_DIR=/run/user/1000
ExecStartPre=/bin/bash -c "sed -i 's/\"exited_cleanly\":false/\"exited_cleanly\":true/' ~/.config/chromium/Default/Preferences 2>/dev/null || true"
ExecStart=/usr/bin/labwc -s "chromium-browser --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --no-first-run --auto-accept-camera-and-microphone-capture --autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding --use-fake-ui-for-media-stream --enable-features=WebRTCPipeWireCapturer http://localhost:8888"
Restart=always
RestartSec=5
User=framelink

[Install]
WantedBy=graphical.target
```

### 4.5 Service Start Order

```
camera-bridge.service  (starts first, creates /dev/video8)
spa-server.service     (starts, serves SPA on :8888)
gpio-daemon.service    (starts, listens for button presses)
chromium-kiosk.service (starts last, loads SPA in fullscreen)
```

**Checkpoint:** Reboot the Pi. All services start automatically. Chromium opens fullscreen showing the Immich Kiosk slideshow. Pressing the GPIO button toggles to video call mode. Camera and audio work in the call.

---

## Phase 5: Reliability Hardening

> **Purpose:** Make the Pi survive weeks/months of unattended 24/7 operation.

### 5.1 Watchdog Service

A systemd timer that monitors Chromium memory and restarts it if it exceeds a threshold:

```bash
# /home/framelink/watchdog.sh
#!/bin/bash
CHROMIUM_PID=$(pgrep -f "chromium-browser.*kiosk" | head -1)
if [ -z "$CHROMIUM_PID" ]; then
    systemctl restart chromium-kiosk
    exit 0
fi
RSS_KB=$(awk '/VmRSS/{print $2}' /proc/$CHROMIUM_PID/status 2>/dev/null || echo 0)
if [ "$RSS_KB" -gt 1536000 ]; then  # 1.5 GB
    systemctl restart chromium-kiosk
fi
```

Run every 5 minutes via a systemd timer.

### 5.2 Scheduled Chromium Restart

`/etc/systemd/system/chromium-restart.timer`:
```ini
[Unit]
Description=Daily Chromium restart at 3 AM

[Timer]
OnCalendar=*-*-* 03:00:00
Persistent=true

[Install]
WantedBy=timers.target
```

`/etc/systemd/system/chromium-restart.service`:
```ini
[Unit]
Description=Restart Chromium kiosk

[Service]
Type=oneshot
ExecStart=/bin/systemctl restart chromium-kiosk
```

### 5.3 SD Card Longevity

```bash
# Enable overlayfs (read-only root with RAM overlay)
# NOTE: Known to be buggy on Pi 5 as of early 2025 — test first
sudo raspi-config nonint do_overlayfs 0

# If overlayfs is broken, manually reduce writes:
# 1. tmpfs for /tmp
echo "tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0" | sudo tee -a /etc/fstab

# 2. Disable swap to SD card (ZRAM replaces it)
sudo systemctl disable dphys-swapfile

# 3. Reduce journal writes
sudo mkdir -p /etc/systemd/journald.conf.d/
echo -e "[Journal]\nStorage=volatile\nRuntimeMaxUse=30M" | sudo tee /etc/systemd/journald.conf.d/volatile.conf
```

### 5.4 Unattended Upgrades (Optional)

```bash
sudo apt install unattended-upgrades -y
sudo dpkg-reconfigure -plow unattended-upgrades
# Only enable security updates, not feature updates
```

**Checkpoint:** Pi survives a 48-hour soak test with periodic simulated calls. No OOM kills, no SD card errors, Chromium restarts cleanly at 3 AM, watchdog catches stuck states.

---

## Phase 6: Multi-Device Deployment

> **Purpose:** Scale from 1 prototype to all 6 FrameLink units.

### 6.1 Create a Golden Image

Once the prototype Pi is fully working:

1. Disable overlayfs temporarily if enabled
2. Clean up any test data, SSH keys, logs
3. Use `sudo dd` or [Pi Imager](https://www.raspberrypi.com/software/) to capture the SD card image
4. Alternatively, script the entire setup with a shell script or Ansible playbook for reproducibility

### 6.2 Per-Device Configuration

Each Pi needs a unique identity:

| Setting | Per-device |
|---|---|
| Hostname | `framelink-01` through `framelink-06` |
| LiveKit identity | `framelink-01` through `framelink-06` |
| WiFi | May vary per household |
| GPIO pin | Same across all units (unless wiring differs) |

Store the device identity in a config file (e.g., `/home/framelink/config.json`) that the SPA reads at startup to request the correct token.

### 6.3 Flash and Test Each Unit

1. Flash the golden image to each SD card
2. Boot each Pi, set hostname and device identity
3. Join all units to a test call simultaneously
4. Verify: video grid shows all participants, audio works, GPIO toggles mode
5. Run a multi-device soak test overnight

### 6.4 Deploy to Households

1. Pre-configure WiFi for each household
2. Test connectivity to the LiveKit server from each household's network
3. Place the unit, plug in power, verify it boots into slideshow
4. Test a call between two units across different households

**Checkpoint:** All units boot into slideshow, join calls reliably, and survive 24-hour unattended operation in their final locations.

---

## Phase Summary

| Phase | What | Estimated Effort | Depends On |
|---|---|---|---|
| **1. Hardware Validation** | Prove 2GB can handle the workload | 2-3 days | Hardware delivery |
| **2. Server Setup** | LiveKit + token service + SSL | 1 day | Phase 1 pass |
| **3. SPA Development** | Build the kiosk shell application | 3-5 days | Phase 2 |
| **4. Pi System Services** | Camera bridge, GPIO, systemd | 1-2 days | Phase 3 |
| **5. Reliability Hardening** | Watchdog, restarts, SD protection | 1-2 days | Phase 4 |
| **6. Multi-Device Deployment** | Scale to all units | 1-2 days | Phase 5 |

Total estimated: ~10-15 days of focused work, assuming Phase 1 passes.
