# Software Build Guide 12 — systemd Services & Reliability Hardening

Every service the frame runs was installed and verified by the earlier guides (the SPA server and Chromium kiosk from [guide 10](10-spa.md), the GPIO button daemon from [guide 11](11-gpio-button.md), the camera node and camera portal from [guide 6](6-camera.md), and the Immich Kiosk slideshow container from [guide 9](9-immich-kiosk.md)), and each is already set to restart after a crash and to come back after a reboot. This guide hardens that fleet for 24/7 unattended operation by adding what restart-on-crash cannot provide: one sweep that verifies the whole fleet is healthy, a watchdog that restarts Chromium when the browser's memory bloats, a scheduled fresh browser start every morning so a frame that hangs on a wall for weeks never goes stale, a set of changes that keeps everyday writes off the SD card so the card lasts years instead of months while keeping enough log history on it to diagnose a bad boot after the fact, automatic security updates, a CPU pinned at full speed so the first seconds of a video call never wait for the chip to ramp up, and a boot-time repair that heals the one way a power cut can break Docker and take the slideshow with it. A final reboot proves the hardened frame still brings itself up with no one touching it.

---

<a id="1-verify-the-whole-service-fleet-is-healthy"></a>
<img src="https://img.shields.io/badge/STEP_01-Verify_the_whole_service_fleet_is_healthy-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Verify the whole service fleet is healthy"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The frame now depends on six separate programs installed across the earlier guides. Hardening only makes sense on top of a healthy system, so before changing anything we need one check that shows all of them running at once.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask systemd for the state of the five frame services in a single command, and ask Docker for the slideshow container, so one screenful shows the health of the whole fleet.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The fleet is six pieces, each owned by the guide that installed it:

1. `framelink-spa.service`: the `busybox httpd` server for the app on `127.0.0.1:8888`, from [guide 10 step 3](10-spa.md#3-serve-the-app-locally).
2. `chromium-kiosk.service`: the browser, created in [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service) and given its final form in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app).
3. `framelink-gpio.service`: the button daemon listening on `127.0.0.1:8889`, from [guide 11 step 3](11-gpio-button.md#3-run-the-daemon-as-a-service).
4. `framelink-camera.service`: the dedicated PipeWire camera node from [guide 6](6-camera.md), which publishes the Pi Camera into PipeWire as a full-field-of-view 1920x1080 at 30 fps video source.
5. `xdg-desktop-portal.service`: the camera portal with its labwc drop-in, from [guide 6 step 2](6-camera.md#2-point-the-desktop-portal-at-the-labwc-session). Unlike the other four it is started on demand: D-Bus wakes it the first time Chromium asks for the camera, so it can legitimately be idle on a frame that has not run a call since boot.
6. The `immich-kiosk` Docker container: the slideshow, from [guide 9 step 3](9-immich-kiosk.md#3-start-the-immich-kiosk-container), with a `restart: always` policy.

All five systemd services are `--user` units running inside the `framelink` session that the console autologin from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) brings up at every boot, which means the whole fleet is alive whenever the frame is powered. The startup ordering between them already exists and is **not** redefined here: `chromium-kiosk.service` declares `After=` and `Wants=` on `framelink-spa.service` and `framelink-camera.service` and then blocks in two `ExecStartPre` guards (one waiting for the Wayland display socket, one polling `http://127.0.0.1:8888/` with `curl` until the app server actually answers), all defined in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app). Every unit carries `Restart=always` (within 2, 5, 3, and 3 seconds respectively) and the container restarts itself too, so plain crashes are already covered. What none of that catches is a browser that is still *running* but degraded, an SD card being slowly worn out, and an OS missing security fixes; that is what the rest of this guide adds. `is-active` prints one state word per service; `docker ps --filter name=immich-kiosk` narrows Docker's process list to the one container that matters.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user is-active framelink-spa chromium-kiosk framelink-gpio framelink-camera xdg-desktop-portal
docker ps --filter name=immich-kiosk
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. is-active prints one state per line — active for framelink-spa, chromium-kiosk, framelink-gpio, and framelink-camera, with xdg-desktop-portal reading active or inactive depending on whether a call has run since boot — and docker ps lists the immich-kiosk container with an Up status.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first four lines must read `active`. The fifth line is the portal: `active` if anything has used the camera since boot, `inactive` otherwise. `inactive` here is normal, because the portal wakes on demand. The word that always means trouble is `failed`; if any service shows it, inspect it with `systemctl --user status <name>` before continuing. The Docker listing must show `immich-kiosk` with a STATUS beginning `Up`. A `permission denied` from `docker ps` means this SSH session predates your docker group membership, so log out, reconnect, and re-run (see [guide 9 step 1](9-immich-kiosk.md#1-install-docker-engine)).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have confirmed the whole fleet (app server, browser, button daemon, camera node, camera portal, and slideshow container) is healthy in one sweep, and you know the one command pair that shows it at a glance. Nothing is hardened yet; that starts now.

<a id="2-create-the-chromium-memory-watchdog-script"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_Chromium_memory_watchdog_script-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the Chromium memory watchdog script"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A browser that runs for weeks without a reload slowly grows its memory use, and this Pi has limited RAM to give it. systemd already restarts Chromium when it crashes, but a bloated browser does not crash; it just makes the frame slower and slower.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a small script that reads how much memory the browser is using and restarts it if that crosses a threshold. This step creates and test-runs the script; the next step makes it run automatically.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The script does one narrow job, in four moves:

1. `pgrep -f "chromium.*kiosk" | head -1` checks a Chromium matching the kiosk command line exists at all.
2. If no such process exists, it restarts the kiosk service. `Restart=always` on the unit normally handles a dead browser by itself, so this branch is a belt-and-braces catch for the corner case where the service is nominally up but its browser process is gone.
3. `ps -o rss= -C chromium | awk '{s+=$1} END {print s+0}'` sums the resident memory of **every** Chromium process (main, GPU, network, and all renderers), and `awk '/MemAvailable/...' /proc/meminfo` reads how much memory the whole system still has to give. Measuring the whole tree is the load-bearing choice: Chromium keeps each web page in a separate *renderer* process, and a leaking page bloats that renderer while the main process barely moves. This exact blindness was measured on hardware: a renderer grew past 1.4 GB and died to the kernel's OOM killer while the main process sat at an innocent-looking 130 MB. A watchdog that reads only the main process never fires; one that sums the tree catches every process the browser owns.
4. It restarts the browser when the tree exceeds `1843200` kB (1.8 GB) **or** `MemAvailable` drops under `358400` kB (350 MB). Both numbers come from measuring this exact hardware. The tree threshold has to sit surprisingly high because big is not the same as sick: after hours of slideshow, the iframe's image cache legitimately carries the healthy tree to ~1.7 GB (that cache is released the instant the iframe unloads), while a full six-way call runs a lean ~1.3 GB, so anything under 1.8 GB would restart perfectly healthy frames. The available-memory floor is the sharper instrument: in the incident that shaped this guide, system-wide stalls began once free memory fell into the low hundreds of megabytes, whatever was consuming it, and since the browser is always this machine's biggest tenant, restarting it is the right response to pressure from any source.

The restart target is `chromium-kiosk.service`, a **user** unit (from [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service), finalized in [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app)), which is why the script calls `systemctl --user restart`, not plain `systemctl`. A watchdog-triggered restart is clean: the unit's own start-up guards make the relaunched browser wait for the display and the local app server, so the frame blinks and is back on the slideshow within seconds. The `cat > ... << 'EOF'` block writes the script (safely overwriting any previous copy on a re-run), `chmod +x` makes it executable, and the last line runs it once by hand as a trial.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cat > ~/chromium-watchdog.sh << 'EOF'
#!/bin/bash
CHROMIUM_PID=$(pgrep -f "chromium.*kiosk" | head -1)
if [ -z "$CHROMIUM_PID" ]; then
    systemctl --user restart chromium-kiosk.service
    exit 0
fi
TREE_RSS_KB=$(ps -o rss= -C chromium | awk '{s+=$1} END {print s+0}')
AVAIL_KB=$(awk '/MemAvailable/{print $2}' /proc/meminfo)
if [ "$TREE_RSS_KB" -gt 1843200 ] || [ "$AVAIL_KB" -lt 358400 ]; then
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

Silence is the pass: no output, and the screen keeps showing the slideshow. If the screen reloads instead, the trial run restarted the browser, meaning Chromium either was not running or was over the threshold, which is the watchdog doing its job, not a failure. A `Permission denied` when running `~/chromium-watchdog.sh` means the `chmod +x` line was skipped.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The watchdog logic exists on the Pi and has been proven runnable: it can measure the browser's memory and restart it cleanly. Nothing runs it on a schedule yet; that is the next step.

<a id="3-run-the-watchdog-every-five-minutes"></a>
<img src="https://img.shields.io/badge/STEP_03-Run_the_watchdog_every_five_minutes-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Run the watchdog every five minutes"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The watchdog script only helps if something runs it, day and night, without anyone remembering to.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create a systemd timer that runs the script every five minutes inside the same user session as the browser, and switch it on.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Two small units, working as a pair:

1. `chromium-watchdog.service` is `Type=oneshot`: each time it is triggered it runs the script from [step 2](#2-create-the-chromium-memory-watchdog-script) once and exits. It is deliberately not enabled on its own; it only exists for the timer to fire.
2. `chromium-watchdog.timer` triggers that service five minutes after boot (`OnBootSec=5min`) and then five minutes after each run (`OnUnitActiveSec=5min`), so every five minutes, forever. `WantedBy=timers.target` hooks it into the user session's timer machinery so it arms itself at every boot.

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

`enable --now` prints a `Created symlink` line, and the `list-timers` table shows a row naming `chromium-watchdog.timer` with a NEXT time in the near future and `chromium-watchdog.service` in the ACTIVATES column. The timer may run the watchdog once immediately when first enabled; on a healthy browser that run does nothing visible. A `Failed to connect to bus` error means this SSH login has no user session bus, so confirm the autologin session from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) is active, or log out and back in.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame now checks itself every five minutes and restarts the browser when memory turns unhealthy: a browser tree past 1.8 GB, or the whole system squeezed under 350 MB free. A failure mode that used to mean a slowly degrading frame until someone pulled the plug now heals itself within minutes.

<a id="4-restart-chromium-early-every-morning"></a>
<img src="https://img.shields.io/badge/STEP_04-Restart_Chromium_early_every_morning-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Restart Chromium early every morning"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Even a browser that never trips the memory watchdog accumulates wear from simply running for weeks on end. Sessions that idle for that long collect stale state that no single measurement can catch.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Schedule one clean browser restart every morning at 3 AM, when nobody is looking at the frame.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The same oneshot-plus-timer pattern as [step 3](#3-run-the-watchdog-every-five-minutes), on a calendar instead of an interval:

1. `chromium-restart.service` is a oneshot whose whole job is `systemctl --user restart chromium-kiosk.service`.
2. `chromium-restart.timer` fires it at `03:00` every day (`OnCalendar=*-*-* 03:00:00`). `Persistent=true` records the last run on disk, so if the frame happens to be powered off at 3 AM the missed restart runs once at the next power-on instead of being skipped, which is harmless, since it just reloads the browser shortly after boot.

Where [step 3](#3-run-the-watchdog-every-five-minutes) bounds the browser's *memory*, this bounds its *age*. A kiosk session left up for weeks accumulates staleness that no threshold measures: long-lived connections, the slideshow iframe cycling endlessly, a renderer that has been alive for a month. The cheapest cure is a scheduled clean start. With this timer the running browser is never more than 24 hours old, and because the restart happens at 3 AM through the same guarded start-up as always (waiting for display and app server), it is a seconds-long blink that nobody sees. These are user units in `~/.config/systemd/user` like everything else in the frame's session; `enable --now` arms the timer, and `list-timers` shows the next 3 AM in its NEXT column.

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

A `Created symlink` line, then a `list-timers` row naming `chromium-restart.timer` whose NEXT column reads the upcoming 3 AM. Nothing visible happens now; the first effect is at the next 3 AM, when the screen briefly blinks back to the slideshow.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The browser now gets a clean start every morning, so no session ever grows older than a day. Together with the watchdog, both slow-decay failure modes, bloating and staleness, are handled without anyone touching the frame.

<a id="5-cut-sd-card-writes-to-make-the-card-last"></a>
<img src="https://img.shields.io/badge/STEP_05-Cut_SD--card_writes_to_make_the_card_last-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Cut SD-card writes to make the card last"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

SD cards wear out from being written to, and a frame that scribbles temporary files and log lines around the clock can wear its card out in months. A worn-out card means a frame that one day simply does not boot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Keep the busiest everyday writes in RAM instead of on the card: temporary files, the system log, and swap, none of which need to survive a reboot on a kiosk.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Three write streams, one command each:

1. **Temporary files.** A `tmpfs` is a filesystem that lives entirely in RAM and vanishes at power-off. Debian 13 (Trixie) mounts `/tmp` as a tmpfs by default on fresh images, so the first command checks with `findmnt` whether `/tmp` is already RAM-backed, and only if it is not does the guarded append add a `tmpfs` line to `/etc/fstab`, which takes effect at the next reboot. This matters more than it sounds: Chromium's working profile is `/tmp/framelink-chromium` (set in the kiosk unit from [guide 10 step 4](10-spa.md#4-point-the-kiosk-browser-at-the-app)), making the browser the single busiest writer on the frame. With `/tmp` in RAM, all of that scratch traffic never touches the card.
2. **The system log.** The journal drop-in sets `Storage=persistent` with a hard `SystemMaxUse=64M` cap, the one write stream this step deliberately **keeps on the card**, small and bounded. The tempting alternative, a RAM-only volatile journal, has a cost this project has already paid once: every log line vanishes at power-off, so a boot that misbehaves and then gets power-cycled leaves no evidence at all. During hardware validation a fleet-killing failure chain (a leak that ended in watchdog resets) went undiagnosable for days for exactly this reason, and became solvable the moment the journal persisted. 64 MB holds one to two weeks of this frame's logs, journald rotates within the cap automatically, and the write volume it adds is a rounding error next to what moving `/tmp` and swap off the card just saved. Restarting `systemd-journald` applies the change; the journal moves to `/var/log/journal` at the flush that follows.
3. **Swap.** Trixie provides the Pi's swap as zram (compressed RAM, verified back in [guide 5](5-kiosk-base.md)), so no swap file sits on the card. The `dphys-swapfile` line is a guard against the *old* SD-backed swap mechanism from earlier Raspberry Pi OS releases: if anything ever installed it, this disables and stops it, and when it is absent (the normal case) the command is silenced and made harmless by the `2>/dev/null || true`. The closing `swapon --show` is the proof: it lists every active swap device, and none of them may be a file on the card.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
findmnt -n -t tmpfs /tmp || grep -qxF 'tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0' /etc/fstab || echo 'tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0' | sudo tee -a /etc/fstab
sudo mkdir -p /etc/systemd/journald.conf.d /var/log/journal
sudo tee /etc/systemd/journald.conf.d/persistent.conf << 'EOF'
[Journal]
Storage=persistent
SystemMaxUse=64M
EOF
sudo systemctl restart systemd-journald
sudo journalctl --flush
sudo systemctl disable --now dphys-swapfile 2>/dev/null || true
swapon --show
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a Trixie image the first line prints the existing tmpfs mount entry for /tmp; tee echoes the three journald lines; the journald restart, journal flush, and swap-guard lines print nothing; swapon --show prints the single /dev/zram0 swap row.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line should print a mount entry for `/tmp` with `tmpfs` in it, which is Trixie's default, meaning nothing needed changing; if it instead echoed the `tmpfs /tmp tmpfs ...` line, the entry was appended to `/etc/fstab` and takes effect at the reboot in [step 9](#9-reboot-and-confirm-the-hardened-frame-comes-back). Either is a pass. `tee` echoes the three-line journald drop-in exactly as written, and `journalctl --flush` silently moves the journal onto the card, so `ls /var/log/journal` would now show a machine-id directory. The final `swapon --show` must list only `/dev/zram0` (RAM-backed swap) or nothing at all; a file path like `/var/swap` in that listing would mean SD-backed swap is active, so re-run the `dphys-swapfile` line and check `swapon --show` again after the reboot in [step 9](#9-reboot-and-confirm-the-hardened-frame-comes-back).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame's heaviest background writes, browser scratch files and swapped memory, now land in RAM instead of on the SD card, while the system log stays on the card, capped at 64 MB, so a frame that misbehaved yesterday can still tell you why today. That combination is the difference between a card that lasts months and one that lasts years, on a frame that never loses its memory of what went wrong.

<a id="6-turn-on-unattended-security-updates"></a>
<img src="https://img.shields.io/badge/STEP_06-Turn_on_unattended_security_updates-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Turn on unattended security updates"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Nobody is going to log in to a picture frame every week to install security fixes, and a device that never gets them slowly becomes the least protected thing in the house.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install Debian's unattended-upgrades service and switch it on, so security fixes install themselves in the background.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`unattended-upgrades` is Debian's standard automatic-update service: once a day it checks the package archives and installs pending updates on its own. Its default policy is deliberately conservative: it takes **security** updates only, so a fix for a vulnerability arrives by itself while ordinary feature updates still happen only when you run a manual `sudo apt full-upgrade`. That is the right split for an unattended kiosk: patched without supervision, but nothing changing the frame's behavior overnight.

The three commands: `apt install` puts the service on the image; `dpkg-reconfigure -plow unattended-upgrades` opens a full-screen yes/no dialog in the terminal, where answering **Yes** writes `/etc/apt/apt.conf.d/20auto-upgrades`, the two-line file that actually turns the daily run on; and `cat` prints that file back as confirmation. Both lines in it are `APT::Periodic` switches and both must end in `"1";`: one enables the daily package-list refresh, the other the unattended upgrade run itself.

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

The `apt` run must finish with no `E:` line. The `dpkg-reconfigure` dialog asks whether to automatically download and install stable updates; select **Yes** with the arrow keys and press Enter. The closing `cat` must print two `APT::Periodic` lines that both end in `"1";`; if either reads `"0";`, the dialog was answered No, so re-run the `dpkg-reconfigure` line and choose Yes.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame now keeps itself patched: security fixes install automatically in the background for as long as the frame is plugged in, with feature updates still left to a deliberate manual upgrade.

<a id="7-pin-the-cpu-governor-to-performance"></a>
<img src="https://img.shields.io/badge/STEP_07-Pin_the_CPU_governor_to_performance-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Pin the CPU governor to performance"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Out of the box the Pi runs its processor at a low speed and only speeds it up once heavy work has already arrived. The heaviest work this frame ever does, turning live camera video into a stream, lands in the first seconds of a call, so exactly those seconds run on a chip that is still waking up.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tell the Pi to hold its processor at full speed all the time. The frame is powered from the wall, so there is no battery to protect. The only effect anyone sees is that a call starts sharp instead of catching up.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The kernel picks CPU clock speeds through a *governor*. The default, `ondemand`, keeps the clock low and ramps it up only after load arrives, and on this frame the load that matters arrives all at once, when the video encoder forms its first keyframes at call start. The `performance` governor holds the maximum clock at all times instead: the right trade for a mains-powered kiosk whose hardest realtime job, encoding live video, is latency-sensitive, because no battery is paying for the held clock and the first frames of every call are encoded at full speed.

The pin is done by a tiny **system** service (note `sudo` and `/etc/systemd/system/`; this one is not a `--user` unit) that writes `performance` into every cpufreq policy at each boot. A oneshot unit is the reliable way on this OS: the `cpufreq.default_governor=performance` kernel parameter does not stick on the Pi OS Trixie kernel (verified on hardware: the parameter lands in `/proc/cmdline` and the governor still comes up `ondemand`). The unit file matches `deploy/systemd/cpu-performance.service` in the repository:

1. The `tee` heredoc writes the unit; `WantedBy=multi-user.target` makes it run at every boot, and `Type=oneshot` means it writes the governor once and exits.
2. `systemctl enable --now` arms it for future boots **and** runs it immediately.
3. The closing `cat` reads the live governor back from the kernel.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo tee /etc/systemd/system/cpu-performance.service << 'EOF'
[Unit]
Description=Pin CPU governor to performance
After=multi-user.target

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'echo performance | tee /sys/devices/system/cpu/cpufreq/policy*/scaling_governor'

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now cpu-performance.service
cat /sys/devices/system/cpu/cpufreq/policy0/scaling_governor
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the unit file; enable --now prints the "Created symlink ... cpu-performance.service" line; the closing cat prints performance.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The closing `cat` must print `performance`; the pin is live immediately, not just after a reboot. If it still prints `ondemand`, the service failed: `systemctl status cpu-performance.service` shows why. The reboot in [step 9](#9-reboot-and-confirm-the-hardened-frame-comes-back) then proves the pin re-applies itself from a cold start.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The CPU now runs at full speed all the time, on this boot and every future one, so the first seconds of every call are encoded on a chip that is already awake.

<a id="8-let-docker-repair-itself-after-power-loss"></a>
<img src="https://img.shields.io/badge/STEP_08-Let_Docker_repair_itself_after_power_loss-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 08 — Let Docker repair itself after power loss"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A frame in a family home will have its plug pulled: power cuts, cleaning, a curious grandchild. One specific way that can break Docker leaves the slideshow dead on every boot afterwards, and nothing on the frame would ever fix it by itself.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Teach Docker to keep containers running through its own restarts, and install a small boot-time repair service that recognises the one known corruption, heals it, and makes sure the slideshow container is running.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The failure this step closes was found the hard way, on hardware, by power-cycling a frame more than a hundred times. An abrupt power cut can catch Docker mid-write and leave its network store (`/var/lib/docker/network/files/local-kv.db`) holding a duplicate entry for the default `docker0` bridge. From then on `dockerd` refuses to start at every boot (`journalctl -u docker` shows `networks have same bridge name`), systemd gives up after three rapid attempts, and the Immich Kiosk container from [guide 9](9-immich-kiosk.md) never runs again. The damage does not stop at a missing slideshow: the app's iframe then points at a dead port, and that state drives the browser-renderer memory leak the watchdog in [step 2](#2-create-the-chromium-memory-watchdog-script) exists to contain (measured at ~50 MB/min on this exact hardware, ending in an out-of-memory kill or a hardware-watchdog reset). One corrupt file, a cascade that takes down the whole frame: worth a dedicated repair.

The commands install three pieces from the repository cloned in [guide 10 step 1](10-spa.md#1-clone-the-framelink-app-onto-the-pi):

1. `deploy/docker/daemon.json` turns on Docker's `live-restore`, which keeps running containers alive across restarts of the Docker daemon itself, so daemon-level hiccups (including the repair below) never blank the slideshow.
2. `deploy/docker/docker-selfheal.sh` runs once per boot, twenty seconds after Docker has had its own chance to start. If `docker.service` sits in `failed` state **and** the journal shows the known corruption signature, it retires the corrupt network store to a timestamped `.corrupt` file beside itself (kept for diagnosis; Docker simply recreates its networks), clears the failure, and starts Docker again. Any *other* Docker failure is deliberately left alone for a human to inspect. As a final belt-and-braces move it issues `docker start immich-kiosk`, a no-op whenever the container's own `restart: always` policy already did the job.
3. `deploy/systemd/docker-selfheal.service` is the system-level oneshot that runs the script at every boot, ordered `After=docker.service` so it always judges Docker's real, settled state.

The `grep`-guarded write of `daemon.json` only creates the file when `live-restore` is not already configured, the two `install` commands copy script and unit into place with the right permissions and are safely re-runnable, and `systemctl restart docker` applies `live-restore` immediately, with the slideshow container surviving that restart as the setting's first live demonstration. The closing `docker info` line prints the setting back as proof.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
grep -q '"live-restore"' /etc/docker/daemon.json 2>/dev/null || sudo install -m 0644 ~/FrameLink/deploy/docker/daemon.json /etc/docker/daemon.json
sudo install -m 0755 ~/FrameLink/deploy/docker/docker-selfheal.sh /usr/local/sbin/docker-selfheal.sh
sudo install -m 0644 ~/FrameLink/deploy/systemd/docker-selfheal.service /etc/systemd/system/docker-selfheal.service
sudo systemctl daemon-reload
sudo systemctl enable docker-selfheal.service
sudo systemctl restart docker
docker ps --filter name=immich-kiosk
docker info --format 'live-restore: {{.LiveRestoreEnabled}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The install and daemon-reload lines are silent, enable prints a "Created symlink ... docker-selfheal.service" line, the docker restart is silent, docker ps still lists immich-kiosk with an Up status whose age predates the restart — live-restore keeping it alive — and the last line prints live-restore: true.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Two things prove the step: the final line must print `live-restore: true`, and the `docker ps` between the restart and that line must still show `immich-kiosk` as `Up` with an uptime *older* than the restart you just performed, because the container sailed through the daemon restart untouched. An `Up 2 seconds` there means live-restore did not apply (a typo in `daemon.json` is the usual cause: `sudo dockerd --validate` checks it). The repair service itself stays silent until the day it is needed; after any future boot, `journalctl -b -u docker-selfheal` shows either a quiet no-op or the full repair story.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The one known way a power cut could permanently kill the slideshow now heals itself at the next boot, Docker restarts no longer interrupt the photos, and any future repair leaves both a journal trail and the corrupt file preserved for diagnosis. The frame survives the treatment a real living room will give it.

<a id="9-reboot-and-confirm-the-hardened-frame-comes-back"></a>
<img src="https://img.shields.io/badge/STEP_09-Reboot_and_confirm_the_hardened_frame_comes_back-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 09 — Reboot and confirm the hardened frame comes back"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

This guide changed what happens at boot: timers that must arm themselves, a RAM-backed `/tmp`, persistent capped logging, a Docker repair pass. The only honest test of boot-time changes is a boot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Reboot the Pi, reconnect, and verify everything in one sweep: services up, both timers armed, temporary files and swap in RAM.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The reboot exercises the full cold-boot sequence from [guide 10 step 5](10-spa.md#5-verify-the-frame-works) (autologin, app server, ordered browser start), now with the hardening live on top. After reconnecting: `is-active` re-checks the four always-on frame services; `docker ps` re-checks the slideshow container; `list-timers` (unfiltered this time) lists every armed timer in the session, which must include `chromium-watchdog.timer` with a NEXT a few minutes out and `chromium-restart.timer` with a NEXT at the coming 3 AM, proof that both re-armed themselves from a cold start; `findmnt /tmp` prints the mount backing `/tmp`, which must be `tmpfs`; `swapon --show` confirms swap is still RAM-backed zram; and `journalctl -b -u docker-selfheal` shows the repair pass from [step 8](#8-let-docker-repair-itself-after-power-loss) ran and found nothing to fix. The authoritative check is again the screen itself: the frame must come back to the slideshow with nobody touching it.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo reboot
systemctl --user is-active framelink-spa chromium-kiosk framelink-gpio framelink-camera
docker ps --filter name=immich-kiosk
systemctl --user list-timers --no-pager
findmnt /tmp
swapon --show
journalctl -b -u docker-selfheal --no-pager
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. sudo reboot drops the SSH session with the client's disconnect line; after reconnecting, is-active prints active four times, docker ps shows immich-kiosk Up, list-timers includes chromium-watchdog.timer and chromium-restart.timer with populated NEXT times, findmnt shows /tmp on tmpfs, swapon --show shows the /dev/zram0 row, and the docker-selfheal journal shows the service ran with its closing "docker-selfheal: done" line and no repair lines.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Run the six check commands after reconnecting. Four `active` lines and an `Up` container mean the fleet survived the reboot. The `list-timers` table may include other timers the OS ships; the two that must be present are `chromium-watchdog.timer` and `chromium-restart.timer`, each with a real time in the NEXT column; a missing timer means its `enable` in [step 3](#3-run-the-watchdog-every-five-minutes) or [step 4](#4-restart-chromium-early-every-morning) did not happen. `findmnt` must show `tmpfs` in the FSTYPE column for `/tmp`, and `swapon --show` must list only `/dev/zram0` (or nothing). The `docker-selfheal` journal must end in `docker-selfheal: done` with no repair lines between, which is the healthy-boot no-op. The governor pinned in [step 7](#7-pin-the-cpu-governor-to-performance) is live from this boot onward: `cat /sys/devices/system/cpu/cpufreq/policy0/scaling_governor` now prints `performance`. And the screen: the slideshow is back, untouched by human hands.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame is now hardened for unattended 24/7 duty: it recovers from crashes, bloat, and staleness by itself, spares its SD card from constant writes, patches its own security holes, and has just proven it brings all of that up from a cold boot with no help.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

After a reboot the frame comes back to the slideshow on its own, and the hardening is live on top of it: `systemctl --user list-timers` shows `chromium-watchdog.timer` firing every five minutes and `chromium-restart.timer` set for 3 AM, `findmnt /tmp` shows a RAM-backed tmpfs, `swapon --show` lists only zram, `cat /sys/devices/system/cpu/cpufreq/policy0/scaling_governor` prints `performance`, the journal persists on the card capped at 64 MB, `docker info` reports live-restore enabled with the `docker-selfheal` repair pass armed at every boot, and `unattended-upgrades` is installed and enabled. Left alone for weeks, the frame now restarts a bloated or stale browser by itself, heals the one known power-cut wound Docker can take, keeps everyday writes off the SD card while keeping enough log history to explain any bad day, and receives security fixes, with no one ever logging in.
