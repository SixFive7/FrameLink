# Software Build Guide 03 — Hardware Configuration

Configure the Pi's firmware and kernel to recognise the hardware assembled in [guide 1](1-hardware-build-guide.md). A stock Raspberry Pi OS install leaves the Waveshare DSI touch display dark — the firmware's auto-detection only knows Raspberry Pi's own official panel — and the panel itself is built portrait while the frame hangs landscape. This guide loads the Waveshare display overlay, rotates the text console to landscape, and reboots to apply both changes. After it, the display is lit and the bare console is usable on the Pi itself; the graphical kiosk that will fill the screen comes later, in [guide 5](5-kiosk-base.md).

---

<a id="1-enable-the-dsi-touch-display-and-rotate-the-console-to-landscape"></a>
<img src="https://img.shields.io/badge/STEP_01-Enable_the_DSI_touch_display_and_rotate_the_console_to_landscape-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Enable the DSI touch display and rotate the console to landscape"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The frame's screen is still completely dark: a fresh Raspberry Pi OS install does not recognise the Waveshare panel on its own. On top of that, the panel is built as an upright (portrait) screen, so even once it lights up, its text would be drawn sideways.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Add one line to each of the Pi's two startup files — one naming the exact display that is attached, one telling the Pi to draw its text console rotated to landscape — then restart the Pi so both take effect.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

On a stock Raspberry Pi OS install the DSI port is idle — the stock `display_auto_detect=1` setting only recognises the *official* Raspberry Pi 7" DSI panel, not Waveshare panels — so the Waveshare overlay has to be loaded explicitly. The overlay used below is what the [Waveshare 10.1-DSI-TOUCH-A wiki](https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A) instructs for Raspberry Pi OS on a Pi 5, matched to our 800×1280 panel and to the DSI cable sitting on the heatsink-side port as connected in [guide 1](1-hardware-build-guide.md) (the LAN-side port would need a `,dsi0` suffix). The panel is natively portrait, so `fbcon=rotate:1` is added to the kernel command line to make the framebuffer console render landscape; rotation of the graphical session is handled separately once labwc is installed in [guide 5](5-kiosk-base.md).

The four lines map as follows, and the edits are idempotent — running the block more than once does not duplicate lines:

1. Store the overlay line in a shell variable so the next command stays readable on one line.
2. Append the overlay line to `/boot/firmware/config.txt`, but only if it is not already present — `grep -qxF` looks for the exact line and skips the append when it finds one, while `tee -a` performs the append and echoes the line it wrote.
3. Append `fbcon=rotate:1` to the end of the single-line kernel command in `/boot/firmware/cmdline.txt`, but only if no `fbcon=rotate:` entry is already there; this `sed` edit is silent.
4. Reboot so the firmware loads the new overlay and the kernel picks up the rotation parameter.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
OVERLAY='dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a'
grep -qxF "$OVERLAY" /boot/firmware/config.txt || echo "$OVERLAY" | sudo tee -a /boot/firmware/config.txt
grep -q 'fbcon=rotate:' /boot/firmware/cmdline.txt || sudo sed -i 's|$| fbcon=rotate:1|' /boot/firmware/cmdline.txt
sudo reboot
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a
client_loop: send disconnect: Connection reset
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line is `tee` echoing the overlay line as it appends it to `config.txt` — the confirmation that the display entry landed; the `cmdline.txt` edit prints nothing. The last line is the SSH session dropping as the Pi restarts. If you ever run the block again, the overlay line is already present, so the echo does not repeat — only the disconnect line appears. After the reboot the decisive check is the frame itself: the Waveshare display shows a **landscape** text console on the DSI panel — no GUI yet, that comes in [guide 5](5-kiosk-base.md). If the text is upside down instead of right-side-up, change `fbcon=rotate:1` to `fbcon=rotate:3` in `/boot/firmware/cmdline.txt` and reboot. If the panel stays dark, reconnect over SSH, confirm the overlay line is present with `grep waveshare /boot/firmware/config.txt`, and recheck the display's DSI ribbon and 5V power connections from [guide 1](1-hardware-build-guide.md).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Pi now recognises the frame's display and puts a usable, correctly-oriented text console on it — the first time the device shows anything on its own screen. There is nothing graphical yet; the kiosk desktop that fills this screen arrives in [guide 5](5-kiosk-base.md).

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

The DSI display is lit with a landscape text console after the reboot, and the Pi comes back up reachable over SSH.
