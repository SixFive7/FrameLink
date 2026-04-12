# Software Build Guide 10 — systemd Services & Reliability Hardening

Turn the camera bridge, SPA static server, GPIO daemon, and Chromium kiosk into systemd services with the correct dependency ordering, so the Pi comes up into a working kiosk state from cold boot. Then harden for 24/7 unattended operation: memory watchdog, `/dev/video8` watchdog, nightly Chromium restart, overlayfs, `/tmp` on tmpfs, volatile journald, and unattended security updates.


---

## Part A — systemd services

### 1. Camera bridge service

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

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
sudo systemctl enable --now camera-bridge
```

### 2. SPA server service

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

### 3. Chromium kiosk service

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
ExecStart=/usr/bin/labwc -s "chromium --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --no-first-run --auto-accept-camera-and-microphone-capture --autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding --use-fake-ui-for-media-stream http://localhost:8888"
Restart=always
RestartSec=5
User=framelink

[Install]
WantedBy=graphical.target
```

### 4. Service start order

```
camera-bridge.service   (starts first, creates /dev/video8)
spa-server.service      (starts, serves SPA on :8888)
gpio-daemon.service     (starts, listens for button presses — see guide 09)
chromium-kiosk.service  (starts last, loads SPA in fullscreen)
```

**Checkpoint:** Reboot the Pi. All services start automatically. Chromium opens fullscreen showing the Immich Kiosk slideshow. Pressing the GPIO button toggles to video call mode. Camera and audio work in the call.

---

## Part B — reliability hardening

### 5. Memory + camera-device watchdog

A short script that monitors Chromium memory and restarts it if it exceeds a threshold, and reloads `v4l2loopback` if `/dev/video8` disappears:

```bash
# /home/framelink/watchdog.sh
#!/bin/bash
CHROMIUM_PID=$(pgrep -f "chromium.*kiosk" | head -1)
if [ -z "$CHROMIUM_PID" ]; then
    systemctl restart chromium-kiosk
    exit 0
fi
RSS_KB=$(awk '/VmRSS/{print $2}' /proc/$CHROMIUM_PID/status 2>/dev/null || echo 0)
if [ "$RSS_KB" -gt 1536000 ]; then  # 1.5 GB
    systemctl restart chromium-kiosk
fi

# v4l2loopback DKMS can silently fail to rebuild after a kernel upgrade on Trixie.
# If /dev/video8 is missing, try to reload the module and restart the camera bridge.
if [ ! -e /dev/video8 ]; then
    modprobe v4l2loopback && systemctl restart camera-bridge || true
fi
```

Run every 5 minutes via a systemd timer.

### 6. Scheduled Chromium restart

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

### 7. SD card longevity

Enable overlayfs (read-only root with RAM overlay). The Pi-5-specific boot issue from Bookworm is resolved on Trixie (kernel 6.12). A remaining Trixie bug makes *additional* partitions read-only, but a fresh single-partition FrameLink install is not affected. Still: test before enabling in production.

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
sudo raspi-config nonint do_overlayfs 0
```

If overlayfs is broken, manually reduce writes:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
# 1. tmpfs for /tmp
echo "tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0" | sudo tee -a /etc/fstab

# 2. Ensure no SD-backed swap (Trixie's rpi-swap provides zram; dphys-swapfile is not active by default)
sudo systemctl disable --now dphys-swapfile 2>/dev/null || true

# 3. Reduce journal writes
sudo mkdir -p /etc/systemd/journald.conf.d/
echo -e "[Journal]\nStorage=volatile\nRuntimeMaxUse=30M" | sudo tee /etc/systemd/journald.conf.d/volatile.conf
```

### 8. Unattended upgrades (optional)

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
sudo apt install unattended-upgrades -y
sudo dpkg-reconfigure -plow unattended-upgrades
```

Only enable security updates, not feature updates.

**Checkpoint:** Pi survives a 48-hour soak test with periodic simulated calls. No OOM kills, no SD card errors, Chromium restarts cleanly at 3 AM, watchdog catches stuck states.
