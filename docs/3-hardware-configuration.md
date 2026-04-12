# Software Build Guide 03 — Hardware Configuration

Configure the Pi's kernel and firmware to recognise the attached hardware. After this guide the DSI touch display is lit in landscape orientation and the bare console is usable on the Pi itself.

---

## Steps

1. **Enable the 10.1" DSI touch display and rotate the console to landscape.** On a stock Raspberry Pi OS install the DSI port is idle — `display_auto_detect=1` only recognises the *official* Raspberry Pi 7" DSI panel, not Waveshare panels. You have to load the Waveshare overlay explicitly. The overlay used below is what [Waveshare's 10.1-DSI-TOUCH-A wiki](https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A) instructs for Raspberry Pi OS on a Pi 5, matched to our 800×1280 panel and the DSI cable on the heatsink-side port (the LAN-side port would need a `,dsi0` suffix). The panel is natively portrait, so we also add `fbcon=rotate:1` to the kernel command line so the framebuffer console renders landscape. (Wayland rotation is handled separately once labwc is installed — see [guide 4](4-kiosk-base.md).)

    The command block does three things in sequence, all idempotent (running it more than once does not duplicate lines):

    1. Append the Waveshare DSI panel overlay line to `/boot/firmware/config.txt`, but only if it is not already present.
    2. Append `fbcon=rotate:1` to the single-line kernel command in `/boot/firmware/cmdline.txt`, but only if no `fbcon=rotate:` entry is already there.
    3. Reboot to apply both changes.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    OVERLAY='dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a'
    grep -qxF "$OVERLAY" /boot/firmware/config.txt || echo "$OVERLAY" | sudo tee -a /boot/firmware/config.txt
    grep -q 'fbcon=rotate:' /boot/firmware/cmdline.txt || sudo sed -i 's|$| fbcon=rotate:1|' /boot/firmware/cmdline.txt
    sudo reboot
    ```

    _(OUTPUT to be captured on the next fresh-card run.)_

    After the reboot the Waveshare display should show a **landscape** text console on the DSI panel — no GUI yet, that comes in [guide 4](4-kiosk-base.md). If the text is upside down instead of right-side-up, change `fbcon=rotate:1` to `fbcon=rotate:3` in `/boot/firmware/cmdline.txt` and reboot.

**Checkpoint:** the DSI display is lit with a landscape text console after the reboot, and the Pi comes back up reachable over SSH.
