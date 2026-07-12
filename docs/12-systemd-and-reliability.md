# Software Build Guide 12 — systemd Services & Reliability Hardening

Every service the frame runs was installed and verified by the earlier guides — the SPA server and Chromium kiosk from [guide 10](10-spa.md), the GPIO button daemon from [guide 11](11-gpio-button.md), the camera portal from [guide 6](6-camera.md), and the Immich Kiosk slideshow container from [guide 9](9-immich-kiosk.md) — and each is already set to restart after a crash and to come back after a reboot. This guide hardens that fleet for 24/7 unattended operation by adding what restart-on-crash cannot provide: one sweep that verifies the whole fleet is healthy, a watchdog that restarts Chromium when the browser's memory bloats, a scheduled fresh browser start every morning so a frame that hangs on a wall for weeks never goes stale, a set of changes that keeps everyday writes off the SD card so the card lasts years instead of months, and automatic security updates. A final reboot proves the hardened frame still brings itself up with no one touching it.

---

<a id="1-verify-the-whole-service-fleet-is-healthy"></a>
<img src="https://img.shields.io/badge/STEP_01-Verify_the_whole_service_fleet_is_healthy-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Verify the whole service fleet is healthy"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The frame now depends on five separate programs, each installed in a different guide. Hardening only makes sense on top of a healthy system, so before changing anything we need one check that shows all of them running at once.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask systemd for the state of the four frame services in a single command, and ask Docker for the slideshow container, so one screenful shows the health of the whole fleet.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The fleet is five pieces, each owned by the guide that installed it:

1. `framelink-spa.service` — the `busybox httpd` server for the app on `127.0.0.1:8888`, from [guide 10 step 3](10-spa.md#3-serve-the-app-locally).
2. `chromium-kiosk.service` — the browser, created in [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service) and given its final form in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app).
3. `framelink-gpio.service` — the button daemon listening on `127.0.0.1:8889`, from [guide 11 step 3](11-gpio-button.md#3-run-the-daemon-as-a-service).
4. `xdg-desktop-portal.service` — the camera portal with its labwc drop-in, from [guide 6 step 2](6-camera.md#2-point-the-desktop-portal-at-the-labwc-session). Unlike the other three it is started on demand: D-Bus wakes it the first time Chromium asks for the camera, so it can legitimately be idle on a frame that has not run a call since boot.
5. The `immich-kiosk` Docker container — the slideshow, from [guide 9 step 3](9-immich-kiosk.md#3-start-the-immich-kiosk-container), with a `restart: always` policy.

All four systemd services are `--user` units running inside the `framelink` session that the console autologin from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) brings up at every boot — which means the whole fleet is alive whenever the frame is powered. The startup ordering between them already exists and is **not** redefined here: `chromium-kiosk.service` declares `After=` and `Wants=` on `framelink-spa.service` and then blocks in two `ExecStartPre` guards — one waiting for the Wayland display socket, one polling `http://127.0.0.1:8888/` with `curl` until the app server actually answers — all defined in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app). Every unit carries `Restart=always` (within 2, 5, and 3 seconds respectively) and the container restarts itself too, so plain crashes are already covered. What none of that catches is a browser that is still *running* but degraded, an SD card being slowly worn out, and an OS missing security fixes — that is what the rest of this guide adds. `is-active` prints one state word per service; `docker ps --filter name=immich-kiosk` narrows Docker's process list to the one container that matters.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user is-active framelink-spa chromium-kiosk framelink-gpio xdg-desktop-portal
docker ps --filter name=immich-kiosk
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. is-active prints one state per line — active for framelink-spa, chromium-kiosk, and framelink-gpio, with xdg-desktop-portal reading active or inactive depending on whether a call has run since boot — and docker ps lists the immich-kiosk container with an Up status.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first three lines must read `active`. The fourth line is the portal: `active` if anything has used the camera since boot, `inactive` otherwise — `inactive` here is normal, because the portal wakes on demand. The word that always means trouble is `failed`; if any service shows it, inspect it with `systemctl --user status <name>` before continuing. The Docker listing must show `immich-kiosk` with a STATUS beginning `Up`. A `permission denied` from `docker ps` means this SSH session predates your docker group membership — log out, reconnect, and re-run (see [guide 9 step 1](9-immich-kiosk.md#1-install-docker-engine)).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have confirmed the whole fleet — app server, browser, button daemon, camera portal, and slideshow container — is healthy in one sweep, and you know the one command pair that shows it at a glance. Nothing is hardened yet; that starts now.

<a id="2-create-the-chromium-memory-watchdog-script"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_Chromium_memory_watchdog_script-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the Chromium memory watchdog script"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A browser that runs for weeks without a reload slowly grows its memory use, and this Pi has limited RAM to give it. systemd already restarts Chromium when it crashes — but a bloated browser does not crash, it just makes the frame slower and slower.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a small script that reads how much memory the browser is using and restarts it if that crosses a threshold. This step creates and test-runs the script; the next step makes it run automatically.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The script does one narrow job, in four moves:

1. `pgrep -f "chromium.*kiosk" | head -1` finds the running Chromium by matching its command line (which contains `chromium` followed by the `--kiosk` flag) and keeps the first — lowest-numbered — process ID, the main browser process whose memory matters.
2. If no such process exists at all, it restarts the kiosk service. `Restart=always` on the unit normally handles a dead browser by itself, so this branch is a belt-and-braces catch for the corner case where the service is nominally up but its browser process is gone.
3. `awk '/VmRSS/{print $2}' /proc/$CHROMIUM_PID/status` reads the process's resident memory — the RAM it actually occupies right now, in kB, straight from the kernel's bookkeeping.
4. If that exceeds `1536000` kB (1.5 GB), it restarts the browser.

The restart target is `chromium-kiosk.service` — a **user** unit (from [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service), finalized in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app)) — which is why the script calls `systemctl --user restart`, not plain `systemctl`. A watchdog-triggered restart is clean: the unit's own start-up guards make the relaunched browser wait for the display and the local app server, so the frame blinks and is back on the slideshow within seconds. The `cat > ... << 'EOF'` block writes the script (safely overwriting any previous copy on a re-run), `chmod +x` makes it executable, and the last line runs it once by hand as a trial.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cat > ~/chromium-watchdog.sh << 'EOF'
#!/bin/bash
CHROMIUM_PID=$(pgrep -f "chromium.*kiosk" | head -1)
if [ -z "$CHROMIUM_PID" ]; then
    systemctl --user restart chromium-kiosk.service
    exit 0
fi
RSS_KB=$(awk '/VmRSS/{print $2}' /proc/$CHROMIUM_PID/status 2>/dev/null || echo 0)
if [ "$RSS_KB" -gt 1536000 ]; then
    systemctl --user restart chromium-kiosk.service
fi
EOF
chmod +x ~/chromium-watchdog.sh
~/chromium-watchdog.sh
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. All three commands are silent on a healthy frame: the script is written, made executable, and the trial run finds the browser under its memory threshold and exits printing nothing.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Silence is the pass: no output, and the screen keeps showing the slideshow. If the screen reloads instead, the trial run restarted the browser — meaning Chromium either was not running or was over the threshold — which is the watchdog doing its job, not a failure. A `Permission denied` when running `~/chromium-watchdog.sh` means the `chmod +x` line was skipped.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The watchdog logic exists on the Pi and has been proven runnable: it can measure the browser's memory and restart it cleanly. Nothing runs it on a schedule yet — that is the next step.

<a id="3-run-the-watchdog-every-five-minutes"></a>
<img src="https://img.shields.io/badge/STEP_03-Run_the_watchdog_every_five_minutes-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Run the watchdog every five minutes"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The watchdog script only helps if something runs it, day and night, without anyone remembering to.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create a systemd timer that runs the script every five minutes inside the same user session as the browser, and switch it on.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Two small units, working as a pair:

1. `chromium-watchdog.service` is `Type=oneshot`: each time it is triggered it runs the script from [step 2](#2-create-the-chromium-memory-watchdog-script) once and exits. It is deliberately not enabled on its own — it only exists for the timer to fire.
2. `chromium-watchdog.timer` triggers that service five minutes after boot (`OnBootSec=5min`) and then five minutes after each run (`OnUnitActiveSec=5min`) — every five minutes, forever. `WantedBy=timers.target` hooks it into the user session's timer machinery so it arms itself at every boot.

Both are **user** units, for the same reason the script calls `systemctl --user`: only the user manager that owns `chromium-kiosk.service` can restart it, and a timer running inside that same session reaches it with no extra plumbing. They live in `~/.config/systemd/user` beside the frame's other services, and because the autologin session from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) is up whenever the frame is powered, "every five minutes" truly means around the clock. `daemon-reload` makes systemd read the two new files; `enable --now` arms the **timer** (the service stays dormant between triggers); the closing `list-timers` shows the timer with the time of its next run.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user
tee ~/.config/systemd/user/chromium-watchdog.service << 'EOF'
[Unit]
Description=Chromium memory watchdog

[Service]
Type=oneshot
ExecStart=/home/framelink/chromium-watchdog.sh
EOF
tee ~/.config/systemd/user/chromium-watchdog.timer << 'EOF'
[Unit]
Description=Run the Chromium memory watchdog every five minutes

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now chromium-watchdog.timer
systemctl --user list-timers chromium-watchdog.timer --no-pager
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes each unit file as it is written, enable --now prints a "Created symlink ... chromium-watchdog.timer" line, and list-timers shows the timer with a NEXT time about five minutes away, activating chromium-watchdog.service.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`enable --now` prints a `Created symlink` line, and the `list-timers` table shows a row naming `chromium-watchdog.timer` with a NEXT time in the near future and `chromium-watchdog.service` in the ACTIVATES column. The timer may run the watchdog once immediately when first enabled; on a healthy browser that run does nothing visible. A `Failed to connect to bus` error means this SSH login has no user session bus — confirm the autologin session from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) is active, or log out and back in.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame now checks its own browser every five minutes and restarts it if it has bloated past 1.5 GB — a failure mode that used to mean a slowly degrading frame until someone pulled the plug now heals itself within minutes.

<a id="4-restart-chromium-early-every-morning"></a>
<img src="https://img.shields.io/badge/STEP_04-Restart_Chromium_early_every_morning-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Restart Chromium early every morning"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Even a browser that never trips the memory watchdog accumulates wear from simply running for weeks on end — sessions that idle for that long collect stale state that no single measurement can catch.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Schedule one clean browser restart every morning at 3 AM, when nobody is looking at the frame.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The same oneshot-plus-timer pattern as [step 3](#3-run-the-watchdog-every-five-minutes), on a calendar instead of an interval:

1. `chromium-restart.service` is a oneshot whose whole job is `systemctl --user restart chromium-kiosk.service`.
2. `chromium-restart.timer` fires it at `03:00` every day (`OnCalendar=*-*-* 03:00:00`). `Persistent=true` records the last run on disk, so if the frame happens to be powered off at 3 AM the missed restart runs once at the next power-on instead of being skipped — harmless, since it just reloads the browser shortly after boot.

Where [step 3](#3-run-the-watchdog-every-five-minutes) bounds the browser's *memory*, this bounds its *age*. A kiosk session left up for weeks accumulates staleness that no threshold measures — long-lived connections, the slideshow iframe cycling endlessly, a renderer that has been alive for a month — and the cheapest cure is a scheduled clean start. With this timer the running browser is never more than 24 hours old, and because the restart happens at 3 AM through the same guarded start-up as always (waiting for display and app server), it is a seconds-long blink that nobody sees. These are user units in `~/.config/systemd/user` like everything else in the frame's session; `enable --now` arms the timer, and `list-timers` shows the next 3 AM in its NEXT column.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
tee ~/.config/systemd/user/chromium-restart.service << 'EOF'
[Unit]
Description=Restart Chromium kiosk

[Service]
Type=oneshot
ExecStart=/usr/bin/systemctl --user restart chromium-kiosk.service
EOF
tee ~/.config/systemd/user/chromium-restart.timer << 'EOF'
[Unit]
Description=Daily Chromium restart at 3 AM

[Timer]
OnCalendar=*-*-* 03:00:00
Persistent=true

[Install]
WantedBy=timers.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now chromium-restart.timer
systemctl --user list-timers chromium-restart.timer --no-pager
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes each unit file as it is written, enable --now prints a "Created symlink ... chromium-restart.timer" line, and list-timers shows the timer with the coming 3 AM in its NEXT column, activating chromium-restart.service.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

A `Created symlink` line, then a `list-timers` row naming `chromium-restart.timer` whose NEXT column reads the upcoming 3 AM. Nothing visible happens now — the first effect is at the next 3 AM, when the screen briefly blinks back to the slideshow.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The browser now gets a clean start every morning, so no session ever grows older than a day. Together with the watchdog, both slow-decay failure modes — bloating and staleness — are handled without anyone touching the frame.

<a id="5-cut-sd-card-writes-to-make-the-card-last"></a>
<img src="https://img.shields.io/badge/STEP_05-Cut_SD--card_writes_to_make_the_card_last-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Cut SD-card writes to make the card last"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

SD cards wear out from being written to, and a frame that scribbles temporary files and log lines around the clock can wear its card out in months. A worn-out card means a frame that one day simply does not boot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Keep the busiest everyday writes in RAM instead of on the card: temporary files, the system log, and swap — none of which need to survive a reboot on a kiosk.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Three write streams, one command each:

1. **Temporary files.** A `tmpfs` is a filesystem that lives entirely in RAM and vanishes at power-off. Debian 13 (Trixie) mounts `/tmp` as a tmpfs by default on fresh images, so the first command checks with `findmnt` whether `/tmp` is already RAM-backed — and only if it is not does the guarded append add a `tmpfs` line to `/etc/fstab`, which takes effect at the next reboot. This matters more than it sounds: Chromium's working profile is `/tmp/framelink-chromium` (set in the kiosk unit from [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app)), making the browser the single busiest writer on the frame — with `/tmp` in RAM, all of that scratch traffic never touches the card.
2. **The system log.** The journal drop-in sets `Storage=volatile`, which keeps systemd's journal in RAM under `/run` instead of on the card, capped at 30 MB by `RuntimeMaxUse=30M`. The cost is that logs from before the latest boot are gone; `journalctl` still shows everything since boot, which is what the troubleshooting steps in these guides actually use. Restarting `systemd-journald` applies the change immediately.
3. **Swap.** Trixie provides the Pi's swap as zram — compressed RAM, verified back in [guide 5](5-kiosk-base.md) — so no swap file sits on the card. The `dphys-swapfile` line is a guard against the *old* SD-backed swap mechanism from earlier Raspberry Pi OS releases: if anything ever installed it, this disables and stops it, and when it is absent (the normal case) the command is silenced and made harmless by the `2>/dev/null || true`. The closing `swapon --show` is the proof: it lists every active swap device, and none of them may be a file on the card.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
findmnt -n -t tmpfs /tmp || grep -qxF 'tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0' /etc/fstab || echo 'tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0' | sudo tee -a /etc/fstab
sudo mkdir -p /etc/systemd/journald.conf.d
sudo tee /etc/systemd/journald.conf.d/volatile.conf << 'EOF'
[Journal]
Storage=volatile
RuntimeMaxUse=30M
EOF
sudo systemctl restart systemd-journald
sudo systemctl disable --now dphys-swapfile 2>/dev/null || true
swapon --show
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a Trixie image the first line prints the existing tmpfs mount entry for /tmp; tee echoes the three journald lines; the journald restart and swap-guard lines print nothing; swapon --show prints the single /dev/zram0 swap row.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line should print a mount entry for `/tmp` with `tmpfs` in it — Trixie's default — meaning nothing needed changing; if it instead echoed the `tmpfs /tmp tmpfs ...` line, the entry was appended to `/etc/fstab` and takes effect at the reboot in [step 7](#7-reboot-and-confirm-the-hardened-frame-comes-back). Either is a pass. `tee` echoes the three-line journald drop-in exactly as written. The final `swapon --show` must list only `/dev/zram0` (RAM-backed swap) or nothing at all; a file path like `/var/swap` in that listing would mean SD-backed swap is active — re-run the `dphys-swapfile` line and check `swapon --show` again after the reboot in [step 7](#7-reboot-and-confirm-the-hardened-frame-comes-back).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame's constant background writes — browser scratch files, system logs, swapped memory — now land in RAM instead of on the SD card. The card is left holding only the OS, the app, and the photo cache, which is the difference between a card that lasts months and one that lasts years.

<a id="6-turn-on-unattended-security-updates"></a>
<img src="https://img.shields.io/badge/STEP_06-Turn_on_unattended_security_updates-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Turn on unattended security updates"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Nobody is going to log in to a picture frame every week to install security fixes, and a device that never gets them slowly becomes the least protected thing in the house.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install Debian's unattended-upgrades service and switch it on, so security fixes install themselves in the background.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`unattended-upgrades` is Debian's standard automatic-update service: once a day it checks the package archives and installs pending updates on its own. Its default policy is deliberately conservative — it takes **security** updates only, so a fix for a vulnerability arrives by itself while ordinary feature updates still happen only when you run a manual `sudo apt full-upgrade`. That is the right split for an unattended kiosk: patched without supervision, but nothing changing the frame's behavior overnight.

The three commands: `apt install` puts the service on the image; `dpkg-reconfigure -plow unattended-upgrades` opens a full-screen yes/no dialog in the terminal — answering **Yes** writes `/etc/apt/apt.conf.d/20auto-upgrades`, the two-line file that actually turns the daily run on; and `cat` prints that file back as confirmation. Both lines in it are `APT::Periodic` switches and both must end in `"1";` — one enables the daily package-list refresh, the other the unattended upgrade run itself.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install -y unattended-upgrades
sudo dpkg-reconfigure -plow unattended-upgrades
cat /etc/apt/apt.conf.d/20auto-upgrades
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. apt installs unattended-upgrades and its dependencies; dpkg-reconfigure takes over the terminal with a full-screen dialog and prints nothing after Yes is chosen; cat then shows the two APT::Periodic lines of 20auto-upgrades, each set to "1".]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `apt` run must finish with no `E:` line. The `dpkg-reconfigure` dialog asks whether to automatically download and install stable updates — select **Yes** with the arrow keys and press Enter. The closing `cat` must print two `APT::Periodic` lines that both end in `"1";`; if either reads `"0";`, the dialog was answered No — re-run the `dpkg-reconfigure` line and choose Yes.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame now keeps itself patched: security fixes install automatically in the background for as long as the frame is plugged in, with feature updates still left to a deliberate manual upgrade.

<a id="7-reboot-and-confirm-the-hardened-frame-comes-back"></a>
<img src="https://img.shields.io/badge/STEP_07-Reboot_and_confirm_the_hardened_frame_comes_back-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Reboot and confirm the hardened frame comes back"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

This guide changed what happens at boot — timers that must arm themselves, a RAM-backed `/tmp`, volatile logging — and the only honest test of boot-time changes is a boot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Reboot the Pi, reconnect, and verify everything in one sweep: services up, both timers armed, temporary files and swap in RAM.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The reboot exercises the full cold-boot sequence from [guide 10 step 5](10-spa.md#5-verify-the-frame-works) — autologin, app server, ordered browser start — now with the hardening live on top. After reconnecting: `is-active` re-checks the three always-on frame services; `docker ps` re-checks the slideshow container; `list-timers` (unfiltered this time) lists every armed timer in the session, which must include `chromium-watchdog.timer` with a NEXT a few minutes out and `chromium-restart.timer` with a NEXT at the coming 3 AM — proof that both re-armed themselves from a cold start; `findmnt /tmp` prints the mount backing `/tmp`, which must be `tmpfs`; and `swapon --show` confirms swap is still RAM-backed zram. The authoritative check is again the screen itself: the frame must come back to the slideshow with nobody touching it.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo reboot
systemctl --user is-active framelink-spa chromium-kiosk framelink-gpio
docker ps --filter name=immich-kiosk
systemctl --user list-timers --no-pager
findmnt /tmp
swapon --show
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. sudo reboot drops the SSH session with the client's disconnect line; after reconnecting, is-active prints active three times, docker ps shows immich-kiosk Up, list-timers includes chromium-watchdog.timer and chromium-restart.timer with populated NEXT times, findmnt shows /tmp on tmpfs, and swapon --show shows the /dev/zram0 row.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Run the five check commands after reconnecting. Three `active` lines and an `Up` container mean the fleet survived the reboot. The `list-timers` table may include other timers the OS ships — the two that must be present are `chromium-watchdog.timer` and `chromium-restart.timer`, each with a real time in the NEXT column; a missing timer means its `enable` in [step 3](#3-run-the-watchdog-every-five-minutes) or [step 4](#4-restart-chromium-early-every-morning) did not happen. `findmnt` must show `tmpfs` in the FSTYPE column for `/tmp`, and `swapon --show` must list only `/dev/zram0` (or nothing). And the screen: the slideshow is back, untouched by human hands.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame is now hardened for unattended 24/7 duty: it recovers from crashes, bloat, and staleness by itself, spares its SD card from constant writes, patches its own security holes, and has just proven it brings all of that up from a cold boot with no help.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

After a reboot the frame comes back to the slideshow on its own, and the hardening is live on top of it: `systemctl --user list-timers` shows `chromium-watchdog.timer` firing every five minutes and `chromium-restart.timer` set for 3 AM, `findmnt /tmp` shows a RAM-backed tmpfs, `swapon --show` lists only zram, the journal is volatile, and `unattended-upgrades` is installed and enabled. Left alone for weeks, the frame now restarts a bloated or stale browser by itself, keeps everyday writes off the SD card, and receives security fixes — with no one ever logging in.
