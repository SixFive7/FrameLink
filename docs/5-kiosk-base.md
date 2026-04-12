# Software Build Guide 05 — Kiosk Base (labwc + Chromium)

Install the Wayland compositor (`labwc`) and Chromium, enable console autologin, and wire up a labwc autostart entry that launches Chromium fullscreen with the kiosk flag set. After this guide the Pi boots directly into a browser on the DSI display.


---

## Steps

1. **Verify ZRAM swap is active.** Trixie enables ~2 GB zram swap by default via the `rpi-swap` package — we don't configure anything, just confirm it's working. ZRAM compresses cold memory pages in RAM, giving the 2 GB Pi 5 more effective headroom under Chromium's WebRTC load without writing to (and wearing out) the SD card.

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   swapon --show
   ```

   ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square) (a line for `/dev/zram0` should appear)

2. **Install labwc, Chromium, and the PipeWire ALSA shim.** On Trixie the package is `chromium` (not `chromium-browser`), and the binary is `/usr/bin/chromium`. `pipewire-alsa` + `wireplumber` are required on Lite so Chromium WebRTC audio can reach ALSA devices (the XVF3800 speaker/mic).

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   sudo apt install labwc chromium pipewire-alsa wireplumber -y
   ```

3. **Enable console autologin** (required for labwc to start at boot):

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   sudo raspi-config nonint do_boot_behaviour B2
   ```

4. **Create the labwc autostart file** that launches Chromium in kiosk mode. The placeholder URL below points at a public WebRTC sample — it will be replaced with `http://localhost:8888` (the SPA) in a later guide. The `cat >` form is idempotent: running the step again overwrites the file with the same content.

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   mkdir -p ~/.config/labwc
   cat > ~/.config/labwc/autostart << 'EOF'
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
     https://webrtc.github.io/samples/
   EOF
   ```

5. **Rotate the labwc output to landscape.** [Guide 3](3-hardware-configuration.md) rotated the bare framebuffer console via `fbcon=rotate:1`, but that setting only applies to the TTY. Once labwc takes over, it owns its own output transform and has to be told separately. Write an `rc.xml` with a `transform="90"` for the DSI output — this matches the 90° CW rotation used on the console, so the Chromium kiosk and the touch input stay correctly aligned. The `cat >` form is idempotent. Confirm your DSI connector name first with `ls /sys/class/drm/ | grep DSI` — on this hardware it is `DSI-2`. If you see a different name, substitute it below.

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   mkdir -p ~/.config/labwc
   cat > ~/.config/labwc/rc.xml << 'EOF'
   <?xml version="1.0" encoding="UTF-8"?>
   <openbox_config xmlns="http://openbox.org/3.4/rc">
     <outputs>
       <output name="DSI-2" transform="90"/>
     </outputs>
   </openbox_config>
   EOF
   ```

   If the display ends up rotated the wrong way in labwc, change `transform="90"` to `transform="270"` (the Wayland equivalent of `fbcon=rotate:3`).

6. **Reboot** and verify the kiosk comes up:

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   sudo reboot
   ```

**Checkpoint:** Chromium loads fullscreen on the DSI display. Touch works. No desktop environment is visible.
