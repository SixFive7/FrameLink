# Software Build Guide 05 — Kiosk Base (labwc + Chromium)

Install the Wayland compositor (`labwc`) and Chromium, enable console autologin, run labwc on tty1 after login, and hand Chromium to a systemd user service so it starts deterministically (waiting for the Wayland socket, not a fixed sleep) and restarts automatically if it crashes. After this guide the Pi boots directly into a fullscreen Chromium kiosk on the DSI display in landscape orientation.

---

## Steps

1. ### Confirm ZRAM swap is active

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   A 2 GB Raspberry Pi 5 running Chromium + WebRTC + a compositor is tight on memory. Without extra swap headroom Chromium can get OOM-killed under load, taking the kiosk down mid-call.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Confirm that the ~2 GB of ZRAM swap that Pi OS Trixie enables by default is actually live.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   Pi OS Trixie enables ~2 GB of ZRAM swap out of the box via the `rpi-swap` package — we do not configure anything, just verify. ZRAM compresses cold memory pages in RAM (rather than writing to the SD card) and gives the 2 GB Pi 5 meaningfully more effective headroom under Chromium's WebRTC load, without the write amplification of disk-based swap on a flash card.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   swapon --show
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   NAME       TYPE      SIZE USED PRIO
   /dev/zram0 partition   2G   0B  100
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   A `/dev/zram0` line with type `partition`, size `2G`, and priority `100`. If `swapon --show` prints nothing at all, ZRAM is not active — check `systemctl status rpi-swap` and confirm the `rpi-swap` package is installed with `dpkg -l rpi-swap`.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   ZRAM swap is active. The Pi effectively has ~2 GB of compressed swap on top of its physical RAM, with no SD-card wear.

2. ### Install the kiosk packages

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   Pi OS Lite does not ship a Wayland compositor, a browser, or the audio plumbing that WebRTC needs to reach an ALSA device. All of these are required for the kiosk.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Install the five packages the kiosk stack needs in a single `apt install` call.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   Five packages and what each does:
   1. `labwc` — the Wayland compositor. Chosen over Wayfire / weston / sway for its small footprint, stable wlroots-based output-rotation path (via `wlr-randr`), and `rc.xml` touch-mapping support.
   2. `chromium` — the browser. On Trixie the package is `chromium` (not the older `chromium-browser`); the binary is `/usr/bin/chromium`.
   3. `pipewire-alsa` — shim that lets Chromium's WebRTC audio reach real ALSA devices on Pi OS Lite (where there is no PulseAudio).
   4. `wireplumber` — session manager that PipeWire relies on under Debian and derivatives.
   5. `wlr-randr` — one-shot CLI used in step 6 to rotate the DSI output. labwc's `rc.xml` does not accept output transforms — those come from `wlr-randr` (one-shot) or `kanshi` (daemon); the one-shot form matches a kiosk's static-configuration intent.

   The install pulls in about 215 dependencies totalling ~256 MB.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   sudo apt install labwc chromium pipewire-alsa wireplumber wlr-randr -y
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   Installing:
     chromium  labwc  pipewire-alsa  wireplumber  wlr-randr

   Installing dependencies:
     adwaita-icon-theme            libpango-1.0-0
     at-spi2-common                libpangocairo-1.0-0
     ...
     xserver-common                zutty

   Summary:
     Upgrading: 0, Installing: 215, Removing: 0, Not Upgrading: 0
     Download size: 256 MB
     Space needed: 750 MB / 116 GB available

   Get:1 http://deb.debian.org/debian trixie/main arm64 at-spi2-common all 2.56.2-1+deb13u1 [171 kB]
   ...
   Fetched 256 MB in 6s (41.8 MB/s)
   ...
   Setting up labwc (0.9.2-1+rpt4) ...
   Setting up chromium (1:146.0.7680.164-1~deb13u1+rpt1) ...
   update-alternatives: using /usr/bin/chromium to provide /usr/bin/x-www-browser (x-www-browser) in auto mode
   update-alternatives: using /usr/bin/chromium to provide /usr/bin/gnome-www-browser (gnome-www-browser) in auto mode
   Setting up rpi-chromium-mods (20260211) ...
   Setting up wlr-randr (0.4.1-1) ...
   Processing triggers for libc-bin (2.41-12+rpt1+deb13u2) ...
   Processing triggers for libgdk-pixbuf-2.0-0:arm64 (2.42.12+dfsg-4+deb13u1) ...
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   All five requested packages in the `Installing:` list, around 215 dependencies, ~256 MB download, and `Setting up` lines for each of the five at the end — particularly `Setting up labwc ...` and `Setting up chromium ...`. The `update-alternatives: using /usr/bin/chromium to provide /usr/bin/x-www-browser` line confirms Chromium is wired in as the system default browser. `...` in the middle is the dependency-list / `Get:` truncation marker — tens of lines elided for brevity.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   The full kiosk software stack is on disk. Nothing is configured or running yet — every piece still needs wiring up in the following steps.

3. ### Enable console autologin

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   A fresh Pi OS install waits for a username and password at the console. A kiosk needs to reach its GUI without anyone typing credentials at every power cycle.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Run `raspi-config` in non-interactive mode to write the autologin drop-in silently, then `cat` the generated file to verify.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   `raspi-config nonint do_boot_behaviour B2` writes the same configuration the interactive menu would — a `getty@tty1.service` drop-in that replaces the agetty invocation with `agetty --autologin framelink` — but without prompts and idempotently (re-running does nothing if the drop-in already matches). After reboot the Pi auto-logs in as `framelink` on tty1 with no password prompt.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   sudo raspi-config nonint do_boot_behaviour B2
   cat /etc/systemd/system/getty@tty1.service.d/autologin.conf
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   [Service]
   ExecStart=
   ExecStart=-/sbin/agetty --autologin framelink --noclear %I $TERM
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   A `[Service]` section with exactly these three lines. The first `ExecStart=` on its own clears any ExecStart inherited from the parent unit (systemd requires this to override `ExecStart`); the second supplies the autologin version. If the username in the second line is not `framelink`, raspi-config picked up a different username — re-check the username you set in [guide 2 step 10](2-sd-flash-first-boot.md#10-set-the-username-to-framelink).

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   tty1 will autologin as `framelink` at the next boot. No password prompt on the console.

4. ### Start labwc on tty1 after autologin

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   Installing labwc does not arrange for it to start — on Pi OS Lite there is no display manager. Without a shell-login hook, autologin lands framelink at a bare bash prompt and labwc never launches.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Create a `~/.bash_profile` that runs `exec labwc` when bash is the autologin shell on tty1, and does nothing in any other context.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   Two conditions scope the `exec` tightly:
   - `"$(tty)" = "/dev/tty1"` — only fires on the autologin console, never on SSH sessions or other TTYs.
   - `-z "$WAYLAND_DISPLAY"` — idempotent re-entry guard. If something ever re-sources `~/.bash_profile` while a Wayland session is already live, this skips the exec.

   `exec` replaces the login bash with labwc so no orphan shell lingers. Sourcing `~/.profile` first preserves the normal bash login behaviour that bash otherwise skips when `~/.bash_profile` exists.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   cat > ~/.bash_profile << 'EOF'
   [ -f ~/.profile ] && . ~/.profile

   if [ -z "$WAYLAND_DISPLAY" ] && [ "$(tty)" = "/dev/tty1" ]; then
       exec labwc
   fi
   EOF
   cat ~/.bash_profile
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   [ -f ~/.profile ] && . ~/.profile

   if [ -z "$WAYLAND_DISPLAY" ] && [ "$(tty)" = "/dev/tty1" ]; then
       exec labwc
   fi
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   The echoed `~/.bash_profile` matches the heredoc byte-for-byte: the profile-source line, a blank line, the conditional with both the tty and `WAYLAND_DISPLAY` checks, and `exec labwc` inside the `if`. If the conditional is missing either check, labwc will fire on SSH sessions too — which breaks remote admin.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   When framelink autologs in on tty1 at the next boot, bash will source `~/.profile` and then `exec labwc`. Nothing is running yet — takes effect on the reboot in step 8.

5. ### Create the Chromium systemd user service

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   Launching Chromium directly from labwc's autostart script is fragile: Chromium races against the Wayland socket and D-Bus activation and exits silently if it loses. There is also no automatic recovery if the browser crashes mid-call, and no structured place to read its logs.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Wrap Chromium in a systemd `--user` unit that waits for the Wayland socket to exist, restarts on any exit, and logs through the systemd journal.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   The unit structure:
   - `Type=simple` with `ExecStart` running Chromium in the foreground.
   - `Environment="WAYLAND_DISPLAY=wayland-0"` — labwc creates the Wayland socket at `wayland-0` in `/run/user/$(id -u)/`; Chromium's `--ozone-platform=wayland` looks for this env var.
   - `ExecStartPre=/bin/bash -c 'while [ ! -S ... ]; do sleep 0.1; done'` — state-driven wait loop that spins until the Wayland socket file exists, so Chromium never starts before labwc is ready. Much more reliable than a fixed `sleep`.
   - `Restart=always` + `RestartSec=5` — Chromium crash → systemd restarts it five seconds later. The kiosk recovers without operator intervention.
   - `WantedBy=default.target` — user-service default target, enabled by `systemctl --user enable`.

   The Chromium command-line flags, each load-bearing:
   - `--ozone-platform=wayland` — required; Chromium's default X11 backend fails silently under labwc.
   - `--user-data-dir=/tmp/framelink-chromium` — puts the profile on tmpfs, so a power cut never leaves stale `SingletonLock` or dirty-exit state behind. No `sed`-the-Preferences-file hack needed.
   - `--kiosk` + `--noerrdialogs` + `--disable-infobars` + `--disable-session-crashed-bubble` + `--no-first-run` — quiet kiosk UI: fullscreen, no error dialogs, no "Restore pages?" prompts, no first-run wizard.
   - `--auto-accept-camera-and-microphone-capture` — pre-grants `getUserMedia` permission for the LiveKit call flow in later guides. **Do not add `--use-fake-ui-for-media-stream`** — it conflicts with this flag on this Chromium build and causes a silent startup crash.
   - `--disable-features=UsePipeWireCamera` — Pi OS Trixie's Chromium defaults to the PipeWire camera backend, which enumerates PipeWire camera nodes and ignores `/dev/video*`. [Guide 6 (camera bridge)](6-camera-bridge.md) publishes the Pi Camera as a v4l2loopback device at `/dev/video8`; disabling this feature here lets Chromium's `getUserMedia` see that device when the kiosk SPA calls it.
   - `--autoplay-policy=no-user-gesture-required` — lets the SPA's audio/video autoplay without a user tap.
   - `--disable-background-timer-throttling` + `--disable-renderer-backgrounding` — keeps the kiosk tab responsive when its window is not in the foreground (relevant under labwc's tiling behaviour).

   The placeholder URL `https://webrtc.github.io/samples/` is a quick end-to-end WebRTC smoke page; it will be replaced by `http://localhost:8888` (the kiosk SPA) in [guide 9](9-spa.md).

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   mkdir -p ~/.config/systemd/user
   cat > ~/.config/systemd/user/chromium-kiosk.service << 'EOF'
   [Unit]
   Description=Chromium Kiosk Browser
   After=graphical-session.target
   Requires=graphical-session.target

   [Service]
   Type=simple
   Environment="WAYLAND_DISPLAY=wayland-0"
   ExecStartPre=/bin/bash -c 'while [ ! -S "/run/user/$(id -u)/${WAYLAND_DISPLAY}" ]; do sleep 0.1; done'
   ExecStart=/usr/bin/chromium \
     --ozone-platform=wayland \
     --user-data-dir=/tmp/framelink-chromium \
     --kiosk \
     --noerrdialogs \
     --disable-infobars \
     --disable-session-crashed-bubble \
     --no-first-run \
     --auto-accept-camera-and-microphone-capture \
     --disable-features=UsePipeWireCamera \
     --autoplay-policy=no-user-gesture-required \
     --disable-background-timer-throttling \
     --disable-renderer-backgrounding \
     https://webrtc.github.io/samples/
   Restart=always
   RestartSec=5

   [Install]
   WantedBy=default.target
   EOF
   systemctl --user daemon-reload
   systemctl --user enable chromium-kiosk.service
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   Created symlink '/home/framelink/.config/systemd/user/default.target.wants/chromium-kiosk.service' → '/home/framelink/.config/systemd/user/chromium-kiosk.service'.
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   The `Created symlink` line confirms `systemctl --user enable` wrote the target-wants symlink. Both the source and the target of the `→` reference `chromium-kiosk.service` under `~/.config/systemd/user/`. If you see `Failed to enable unit` or any error instead, the unit file has a syntax problem — re-check the heredoc content.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   The kiosk service is enabled and will start automatically when `default.target` is reached in the framelink user session. It is not running yet — the autostart in step 6 fires it once labwc is up.

6. ### Create the labwc autostart

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   labwc itself does not rotate outputs (no native output-transform support in `rc.xml`), and nothing has yet told labwc to start the Chromium service.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Create labwc's autostart file with two lines: one rotates the DSI output to landscape via `wlr-randr`, one starts the Chromium user service in the background. Mark the file executable.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   The two lines:
   - `wlr-randr --output DSI-2 --transform 270` — applies a 270° transform to the `DSI-2` output. The Waveshare 10.1-DSI-TOUCH-A is natively portrait 800×1280; rotating 270° gives a 1280×800 landscape surface for Chromium. labwc's `rc.xml` does not accept output transforms — they have to come from `wlr-randr` (one-shot) or `kanshi` (daemon); the one-shot form matches the kiosk's static configuration.
   - `systemctl --user start chromium-kiosk.service &` — starts the kiosk service, backgrounded with `&` so the autostart script returns promptly. The service's own `ExecStartPre` wait loop handles any residual timing against the Wayland socket.

   If the display ends up upside-down in landscape on the first boot, change `270` to `90` in the autostart file (same step, opposite 180° flip). If your hardware uses a different DSI connector, confirm its name with `WAYLAND_DISPLAY=wayland-0 wlr-randr` over SSH first — on this build it is `DSI-2`. labwc requires the autostart file to be executable; the `chmod +x` is non-optional.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   mkdir -p ~/.config/labwc
   cat > ~/.config/labwc/autostart << 'EOF'
   wlr-randr --output DSI-2 --transform 270
   systemctl --user start chromium-kiosk.service &
   EOF
   chmod +x ~/.config/labwc/autostart
   ls -la ~/.config/labwc/autostart
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   -rwxrwxr-x 1 framelink framelink 91 Apr 13 02:24 /home/framelink/.config/labwc/autostart
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   The `ls` line must show permissions starting `-rwx` (the owner's executable bit is set — `chmod +x` did fire), size 91 bytes (matches the two-line autostart content), owner `framelink framelink`, and path ending `/home/framelink/.config/labwc/autostart`. If the permissions start `-rw-` instead, `chmod` did not run and labwc will silently ignore the autostart file on the next start.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   When labwc starts on the next boot it will rotate the DSI output to landscape and start the Chromium service in one motion. The full autostart chain is wired but not yet fired.

7. ### Map touch input to the rotated DSI output

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   With the output rotated to landscape but no touch-input mapping, touch events stay in the panel's native portrait coordinate system — taps land on the wrong pixels after the image rotates.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Create `~/.config/labwc/rc.xml` with a `<touch mapToOutput="DSI-2"/>` element that binds touch input to the rotated output.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   With `mapToOutput="DSI-2"`, wlroots automatically re-maps touch coordinates to match whatever transform the output has applied. No `LIBINPUT_CALIBRATION_MATRIX` udev rule is required and no Waveshare-specific device-ID matching is needed — it works purely off the output identity.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   cat > ~/.config/labwc/rc.xml << 'EOF'
   <?xml version="1.0" encoding="UTF-8"?>
   <labwc_config>
     <touch mapToOutput="DSI-2"/>
   </labwc_config>
   EOF
   cat ~/.config/labwc/rc.xml
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   <?xml version="1.0" encoding="UTF-8"?>
   <labwc_config>
     <touch mapToOutput="DSI-2"/>
   </labwc_config>
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   The echoed XML matches the heredoc byte-for-byte: XML declaration, opening `<labwc_config>`, the `<touch mapToOutput="DSI-2"/>` element, closing tag. If any piece is misspelled (especially the `DSI-2` identifier) touch mapping silently defaults to the identity transform and taps will be misaligned after boot.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   Touch input is configured to follow the rotated DSI-2 output. Takes effect on the next labwc start, alongside the rest of the autostart chain.

8. ### Reboot to fire the whole autostart chain

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   Every piece configured above — the autologin drop-in, `~/.bash_profile`, the Chromium user service, the labwc autostart, the touch-mapping `rc.xml` — is still dormant. Nothing fires until the framelink user session starts fresh on tty1.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Reboot the Pi. Verification that it came up correctly is step 9.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   On reboot the full chain fires in order: systemd reaches `getty@tty1` → the autologin drop-in runs `agetty --autologin framelink` → framelink's bash starts → `~/.bash_profile` sources `~/.profile` and execs labwc → labwc starts and runs `~/.config/labwc/autostart` → `wlr-randr` rotates the DSI output → `systemctl --user start chromium-kiosk.service` fires → the service's `ExecStartPre` waits for the Wayland socket → Chromium launches fullscreen. Your SSH session drops at the start of all that.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   sudo reboot
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   Connection to framelink-douwe.local closed by remote host.
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   The workstation-side "Connection ... closed by remote host." message confirms the Pi actually went down rather than rejecting the `reboot` call. The exact wording depends on your SSH client (OpenSSH on Windows 11 gives the line shown here). The Pi will be unreachable for roughly 30 seconds during the boot.

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   The Pi is rebooting.

9. ### Verify the kiosk came up

   ![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

   We need to confirm that the full autostart chain actually fired — autologin, labwc, output rotation, Chromium service — end-to-end in the right order, not just that individual files exist.

   ![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

   Reconnect over SSH once the Pi is back and run three quick checks: is the kiosk service active, are Chromium processes actually running, and is the DSI output rotated.

   ![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

   Each of the three commands probes a different link in the chain:
   - `systemctl --user is-active chromium-kiosk.service` — the service is running (proves the labwc autostart's `systemctl --user start` ran, which proves labwc started, which proves `~/.bash_profile` exec'd, which proves autologin worked).
   - `ps -e -o comm | grep -c "^chromium$"` — counts Chromium processes. Chromium spawns about a dozen child processes (main browser process, GPU process, renderer, utility, broker, …). A healthy kiosk shows somewhere around 12; zero or one means Chromium crashed during startup.
   - `WAYLAND_DISPLAY=wayland-0 wlr-randr | grep Transform` — the `DSI-2` output's transform is 270° (the labwc autostart's `wlr-randr` call took effect).

   If any of the three fails, `journalctl --user -u chromium-kiosk.service -n 50 --no-pager` is the first place to look — the systemd journal captures Chromium's stdout, startup errors, and any `ExecStartPre` timing issues.

   ![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

   ```bash
   systemctl --user is-active chromium-kiosk.service
   ps -e -o comm | grep -c "^chromium$"
   WAYLAND_DISPLAY=wayland-0 wlr-randr | grep Transform
   ```

   ![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

   ```text
   active
   12
   Transform: 270
   ```

   ![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

   Three lines: `active` (service is running), a Chromium process count somewhere around 12 (your exact number may differ depending on how many renderer processes are up, but it should not be `0` or `1`), and `Transform: 270`. If `active` is instead `inactive` or `failed`, `journalctl --user -u chromium-kiosk.service -n 50 --no-pager` will show why. If the process count is `0`, the service exited — usually a mis-configured `ExecStart`. If `Transform:` is missing or reads `Transform: normal`, `wlr-randr` did not run — check that the labwc autostart file is executable (`ls -la ~/.config/labwc/autostart` should start `-rwx`).

   ![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

   The Pi boots directly into a fullscreen Chromium kiosk on the DSI panel in landscape orientation. Touch input works. No desktop environment, no login UI, no manual intervention. Chromium is supervised by systemd and will restart automatically if it crashes.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

Chromium is fullscreen on the DSI panel in landscape orientation, touch input is correctly mapped to the rotated output, no desktop or login screen is ever visible, `systemctl --user is-active chromium-kiosk.service` reports `active`, and the kiosk auto-restarts Chromium on crash via `Restart=always` in the systemd unit.
