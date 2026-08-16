# Software Build Guide 13 — Multi-Device Deployment

Scale from the first validated unit to the full household fleet. Rather than repeating guides 02 through 12 by hand on every remaining unit, this guide captures the finished first unit's SD card as a single golden image, flashes that image into each new unit, and changes only what must be unique per device — the hostname, the app's calling identity and token, and the Raspberry Pi Connect registration. Each clone is then verified on its own, the whole fleet is soak-tested in one call, and each frame is delivered to its household and proven in place. This guide is written ahead of the first real rollout, so every EXPECTED OUTPUT below is a pending capture to be filled in verbatim when the fleet is built.

---

<a id="1-plan-the-rollout"></a>
<img src="https://img.shields.io/badge/STEP_01-Plan_the_rollout-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Plan the rollout"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

One frame is built, validated, and running. The rest of the family is still waiting for theirs — and building every remaining unit by working through eleven guides again, by hand, would take days per frame and invite small differences between units.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Treat the finished first unit as the master copy: clone its SD card into every new unit, then change only the handful of things that must be unique per frame. Before any cloning starts, this step is the map of what is being cloned and a final health check of the master.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Everything the first unit knows — the display and audio configuration, the kiosk, the camera path, the slideshow, the app, the button daemon, the reliability hardening — was built up in sequence by guides 02 through 12. The golden image captured in the next step freezes all of that work into one file, which is why the master must be in a known-good state first: any fault cloned now becomes a fault in every household later. The table below is the full per-unit build sequence the golden image compresses.

| Guide                                                              | What                                            | Estimated effort | Depends on                 |
| ---                                                                | ---                                             | ---              | ---                        |
| [02 SD flash & first boot](2-sd-flash-first-boot.md)               | Get Pi online with Trixie Lite + base packages  | 0.5 day          | Hardware guide complete    |
| [03 Hardware configuration](3-hardware-configuration.md)           | DSI display + kernel parameters                 | 0.5 day          | 02                         |
| [04 Audio configuration](4-audio-configuration.md)                 | XVF3800 pinning, amp enable, mixer persistence  | 0.5-1 day        | 03                         |
| [05 Kiosk base](5-kiosk-base.md)                                   | labwc + Chromium fullscreen                     | 0.5 day          | 04                         |
| [06 Camera](6-camera.md)                                           | Dedicated PipeWire camera node (H.264 1080p30 FoV) | 0.5 day       | 05                         |
| [07 LiveKit server](7-livekit-server.md)                           | LiveKit in Docker + token minting               | 1 day            | 06                         |
| [08 WebRTC call-load validation](8-webrtc-validation.md)           | Soak a real call to prove 2 GB holds (go/no-go) | 2-3 days         | 06                         |
| [09 Immich Kiosk](9-immich-kiosk.md)                               | Docker slideshow (offline-capable cache)        | 0.5 day          | 08                         |
| [10 SPA](10-spa.md)                                                | Build the kiosk shell + LiveKit client          | 3-5 days         | 09                         |
| [11 GPIO button daemon](11-gpio-button.md)                         | Python gpiozero daemon                          | 0.5 day          | 10                         |
| [12 systemd & reliability](12-systemd-and-reliability.md)          | Services, watchdog, SD protection, restart      | 1-2 days         | 11                         |
| [13 Multi-device deploy](13-multi-device-deploy.md)                | Scale to all units                              | 1-2 days         | 12                         |

Total estimated: ~10-15 days of focused work, assuming the call-load validation gate (guide 08) passes. That estimate is what the golden image saves on every unit after the first — a clone goes from blank SD card to verified frame in well under a day.

The two commands are the master's health check, run on the first unit. The first asks systemd for the live state of the four FrameLink user services — the app server and kiosk browser from [guide 10](10-spa.md), the button daemon from [guide 11](11-gpio-button.md), and the camera node from [guide 6](6-camera.md). The second lists the running Docker containers, which must include the `immich-kiosk` slideshow from [guide 9](9-immich-kiosk.md).

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user is-active framelink-spa.service chromium-kiosk.service framelink-gpio.service framelink-camera.service
docker ps --format '{{.Names}}: {{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. is-active prints "active" on four lines (one per service) and docker ps lists the immich-kiosk container with an "Up" status.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Four lines reading `active`, and an `immich-kiosk: Up ...` line. Any `inactive` or `failed` means the master is not ready to be cloned — go back to the guide that owns that piece ([guide 10](10-spa.md) for the app server and browser, [guide 11](11-gpio-button.md) for the button daemon, [guide 6](6-camera.md) for the camera node, [guide 9](9-immich-kiosk.md) for the slideshow container) and fix it before capturing anything.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have confirmed the first unit is in the exact state worth copying. Nothing has been cloned yet — the next step turns this unit's SD card into the golden image.

<a id="2-capture-the-golden-image"></a>
<img src="https://img.shields.io/badge/STEP_02-Capture_the_golden_image-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Capture the golden image"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The complete, validated build exists in only one place: the first unit's SD card. Every new frame needs an identical copy, and there is no copy yet.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tidy the master, shut it down cleanly, take out its SD card, and copy the entire card into one image file on your computer — the golden image every other unit will be flashed from.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Two things happen on the Pi before the card is pulled, mapped to the command lines:

1. History and log cleanup (lines 1-4). The image is about to be copied into several households, so the build session's command history and accumulated system logs do not belong in it. `history -c` clears the current shell's history, removing `~/.bash_history` clears the saved history, and the two `journalctl` commands rotate the system journal and delete everything older than one second. (The volatile journal from [guide 12](12-systemd-and-reliability.md) already keeps day-to-day logs in RAM; the sweep also clears anything journald wrote to the card before that hardening was applied.)
2. A clean shutdown (line 5). Pulling the card from a running Pi risks corrupting its file system; `shutdown -h now` halts the system and leaves the card consistent.

Then the workstation half, with the Pi powered off: remove the microSD card from the Pi, put it in your computer's card reader, and copy the **whole card, byte for byte,** into a single file named `framelink-golden.img` using a raw disk-imaging tool of the `dd` family. The exact workstation command and its transcript will be recorded here when the first real rollout is executed. Two facts about the image hold regardless of tool: it is as large as the card itself, so have that much free disk space, and it can only be written back to cards of the **same size or larger** — buying identical cards for the whole fleet avoids the problem entirely.

Two kinds of master state deliberately stay in the image. The app configuration (`config.json`, carrying the master's calling identity and token) and, if enabled, the Raspberry Pi Connect registration are cloned along with everything else — steps 05 and 06 replace them on every clone, so stripping them here would only add work. Lower-level cloned identifiers — the SSH host keys and the machine id every Debian system carries — are also shared by all clones for now; whether those need per-clone regeneration will be pinned down when this rollout is first executed, and the fleet test in step 08 is where a problem caused by them (such as two units being handed the same network address) would surface.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
history -c
rm -f ~/.bash_history
sudo journalctl --rotate
sudo journalctl --vacuum-time=1s
sudo shutdown -h now
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The journal commands print short vacuum confirmations, the other commands print nothing, and the shutdown drops the SSH session as the master powers off.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The two `journalctl` lines report what they rotated and vacuumed; the other commands are silent — no error text means the cleanup landed. After the shutdown drops the connection, wait until the Pi's green activity LED has stopped flashing before pulling the card.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The master is clean and powered off, and its card is ready to be copied into `framelink-golden.img` on your computer. That one file is now the installer for every remaining unit.

<a id="3-flash-and-boot-a-clone"></a>
<img src="https://img.shields.io/badge/STEP_03-Flash_and_boot_a_clone-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Flash and boot a clone"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The golden image is a file on your computer, and the next frame's SD card is blank. Nothing connects them yet.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write the golden image to the new card with Raspberry Pi Imager, boot exactly one new unit from it, and confirm you can reach it — the clone wakes up believing it is the first unit.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Flashing works like [guide 2](2-sd-flash-first-boot.md), with two differences. First, instead of picking an operating system from the list, choose **Choose OS → Use custom** at the bottom and select `framelink-golden.img` — the Imager writes your image instead of a stock one. Second, **skip the OS customisation**: when the Imager offers to apply settings, answer **No**. Customisation was how guide 2 seeded a blank OS with a hostname and user; the golden image already contains all of that, and the clone's own identity is applied over SSH in the next three steps.

Boot **one clone at a time**. Every fresh clone comes up with the master's hostname (`framelink-douwe` in the running example), and two machines announcing the same name on one network collide — you could never be sure which one you are configuring. Keep the master powered off, and any other not-yet-renamed clone unpowered, until the clone in front of you has been renamed in step 04. Insert the flashed card, power the unit, give it a minute to boot, then connect exactly as you would to the master — `ssh framelink@framelink-douwe.local`, with the master's password. `hostnamectl` prints the machine's identity; seeing the master's hostname here is the expected proof that you are inside a healthy clone, since the master itself is off.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
hostnamectl
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. hostnamectl on the freshly booted clone reports the master's hostname (framelink-douwe in the running example) along with the Raspberry Pi OS machine details.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `Static hostname:` line shows the **master's** name — correct at this point, because the clone is a perfect copy and the master is powered off. If the hostname does not resolve, give the unit another minute and check the card is seated properly. If repeated connects seem to land on different machines, another unit carrying the same name is still powered on somewhere — switch it off and reconnect.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

A second unit is running the complete validated build. It still answers to the first unit's name and calls itself the first unit inside the app — the next three steps give it its own identity.

<a id="4-give-the-clone-its-own-hostname"></a>
<img src="https://img.shields.io/badge/STEP_04-Give_the_clone_its_own_hostname-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Give the clone its own hostname"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The clone answers to the first unit's name. Two frames cannot share a name on the same network, and a fleet where every unit is called `framelink-douwe` is impossible to tell apart when configuring or troubleshooting.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Rename the clone over SSH using the same naming pattern you chose in guide 2, then reboot so the new name takes effect on the network.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

[Guide 2](2-sd-flash-first-boot.md) set the first unit's hostname at flash time and recommended the pattern `framelink-<recipient-name>`; the fleet keeps that pattern, and this guide uses `framelink-anna` as the running example for the second unit — substitute the actual recipient's name for each unit you configure. `raspi-config nonint do_hostname` is the non-interactive form of the standard Raspberry Pi configuration tool: it rewrites `/etc/hostname` and the matching `/etc/hosts` entry together, achieving over SSH exactly what the flash-time setting did for the master. The reboot makes the running system adopt the name and re-announce it on the network, after which the unit is reachable at `framelink-anna.local` — and the master's name is free again, so from this point the master and any already-renamed units may be powered back on.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo raspi-config nonint do_hostname framelink-anna
sudo reboot
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. do_hostname prints nothing on success; the reboot then drops the SSH session with the client's disconnect line.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The rename is silent — no output means it worked. After the reboot, reconnect with `ssh framelink@framelink-anna.local`; the first connection to the new name asks the usual host-key question, and `yes` is the answer. If the new name does not resolve after a minute, power-cycle the unit and try again.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The unit now has its own name on the network. Inside the app it still claims to be the first unit — its calling identity and token are replaced next.

<a id="5-give-the-clone-its-own-app-identity"></a>
<img src="https://img.shields.io/badge/STEP_05-Give_the_clone_its_own_app_identity-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Give the clone its own app identity"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The cloned app configuration still contains the first unit's calling identity and its token. Calling identities must be unique — if two frames join the family room under one name, the calling system treats them as the same device and calls misbehave.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Open the app's configuration file, set this unit's own identity, and paste in a token minted for that identity. Everything else in the file stays exactly as it is.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The app reads `~/FrameLink/app/config.json` at start-up — the file created for the master in [guide 10 step 2](10-spa.md#2-create-the-app-configuration). Of its five fields, exactly two are per-device:

1. `identity` — this frame's name in the calling system, the name other callers see. Set it to the unit's hostname from step 04 (`framelink-anna` in the running example); using one name per unit everywhere is what keeps a multi-frame fleet debuggable.
2. `token` — the credential that admits this identity into the room. A token is minted **for** a specific identity, so the master's token cannot be reused: mint a new long-lived token for this unit's identity and the same room on the LiveKit server from [guide 7](7-livekit-server.md), and paste it here. The token is a secret — it goes into this file and nowhere else.

The other three fields are deliberately identical on every unit and must not be changed: `room` (the whole family shares one room — that is what makes every frame reachable by every other frame), `livekitUrl` (one shared server), and `immichKioskUrl` (it points at the unit's own local slideshow, so the same address is correct everywhere). Nothing else on the unit is per-device either — the button pin from [guide 11 step 2](11-gpio-button.md#2-wire-the-button-and-set-its-pin) is the same on every identically wired unit.

Editing with `nano` keeps the token off the terminal and out of the shell history. The final command restarts the kiosk browser so the app reloads and reads the new configuration.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cd ~/FrameLink/app
nano config.json
systemctl --user restart chromium-kiosk.service
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. nano opens the editor full-screen on the five-field JSON (identity, room, livekitUrl, immichKioskUrl, token); after saving, the service restart prints nothing while the frame's screen reloads into the slideshow.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

In `nano`, change only the `identity` and `token` values (each between its quotes), save with `Ctrl+O`, `Enter`, then exit with `Ctrl+X`. Keep the punctuation exactly as it stands — a stray comma or a missing quote makes the file invalid JSON and the app silently falls back to built-in defaults. After the restart the screen comes back to the slideshow; a frame stuck on a spinner usually means the JSON did not parse.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The unit now joins family calls as itself, with its own admission token. One inherited identity remains: if the master was registered with Raspberry Pi Connect, this clone is still carrying that registration.

<a id="6-re-register-raspberry-pi-connect"></a>
<img src="https://img.shields.io/badge/STEP_06-Re--register_Raspberry_Pi_Connect-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Re-register Raspberry Pi Connect"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

If you enabled Raspberry Pi Connect in guide 2, the clone copied the master's registration — the remote-access service now sees two machines claiming to be one device, and you would lose the ability to tell them apart once units leave your house.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Sign the clone out of the inherited registration and sign it back in as its own device under its new name.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

[Guide 2](2-sd-flash-first-boot.md) optionally enrolled the master in Raspberry Pi Connect — the hosted service that gives you a remote shell on a unit once it lives in another household, with no port-forwarding or VPN. That enrolment is per-device state on the card, so the golden image carried it into the clone. `rpi-connect signout` discards the inherited registration on this unit; `rpi-connect signin` starts a fresh enrolment and prints a verification link to open in a browser on your computer, where you approve the device against your Raspberry Pi ID. Because the hostname was already changed in step 04, the unit enrols under its own name and appears alongside the master at [connect.raspberrypi.com/devices](https://connect.raspberrypi.com/devices). The exact prompt text of both commands — and whether the master's entry in the device list needs any manual tidying after a clone signs out — will be pinned down when this rollout is first executed and captured.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
rpi-connect signout
rpi-connect signin
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. signout prints a short confirmation; signin prints a verification URL to open in a browser on the workstation to link this unit to your Raspberry Pi ID.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

A verification link from `signin` — open it on your computer, sign in with your Raspberry Pi ID, and approve. Afterwards the device list at [connect.raspberrypi.com/devices](https://connect.raspberrypi.com/devices) must show this unit under its own hostname **and** still show the master as a separate device. If the shell answers `command not found`, Connect was never enabled on the master in [guide 2](2-sd-flash-first-boot.md) — there is no inherited registration and this step does not apply to your fleet.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The clone is now fully itself: its own hostname, its own calling identity and token, and its own remote-access registration. What is not yet proven is that it behaves like the master from a cold start — that is next.

<a id="7-verify-the-clone-end-to-end"></a>
<img src="https://img.shields.io/badge/STEP_07-Verify_the_clone_end--to--end-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Verify the clone end-to-end"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The clone has been renamed and re-identified while running, but nobody has proven it comes up correctly on its own from power-on — which is the only way it will ever start once it hangs on a wall.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Reboot the unit, run the same health checks the master passed in step 01, and look at the screen.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The reboot exercises the full cold-boot chain the master was validated for: the slideshow container, the app server, the readiness-ordered kiosk browser, and the button daemon all coming up unattended. After reconnecting — to the unit's **new** hostname — the checks mirror step 01, with the app server's `curl` check from [guide 10](10-spa.md) added: three `active` services, an answering app server, and the `immich-kiosk` container `Up`. The authoritative confirmation is the DSI screen resting on the photo slideshow.

Steps 03 through 07 are the whole per-unit loop: flash, boot alone, rename, re-identify, verify. Repeat them for each remaining unit before moving on — step 08 needs every unit finished and on the network at the same time.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo reboot
systemctl --user is-active framelink-spa.service chromium-kiosk.service framelink-gpio.service framelink-camera.service
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:8888/
docker ps --format '{{.Names}}: {{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The reboot drops the SSH session; after reconnecting to the unit's new hostname, is-active prints "active" four times, curl prints "HTTP 200", and docker ps lists immich-kiosk as Up, while the screen shows the slideshow.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Reconnect to the new hostname after the reboot, then look for three `active` lines, `HTTP 200`, an `immich-kiosk: Up ...` line — and photos cycling on the screen. Anything `failed`, or a spinner on the screen, points back at the owning guide exactly as in step 01. Because this is a clone, a fault here that the master does not have was introduced in steps 03 through 06 — recheck those before suspecting the golden image.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

This unit is a verified, self-starting frame with its own identity. Run steps 03 through 07 again for each remaining unit; when all of them pass this step, the fleet is ready to be tested together.

<a id="8-soak-test-the-fleet-together"></a>
<img src="https://img.shields.io/badge/STEP_08-Soak--test_the_fleet_together-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 08 — Soak-test the fleet together"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Every unit works on its own, but the frames exist to call each other — and a call carrying the whole fleet at once is a heavier load than anything a single-unit test has proven.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

With every unit powered on the same network, pull the whole fleet into one call, check every screen, then leave the fleet running overnight and re-check each unit in the morning.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Starting the call needs no reaching behind frames: the app on every unit is already connected to the shared room and auto-answers the moment a remote participant appears ([guide 10](10-spa.md)), so toggling **one** unit into the call brings every other frame in automatically. Line 1, run on any one unit, is the simulated button press from [guide 11 step 4](11-gpio-button.md#4-test-the-toggle-without-the-button) — that unit joins the call, and every other frame switches from its slideshow to the call grid on its own.

While the call is up, walk the room: every frame shows every participant in the grid, every frame's camera and microphone are live (wave and speak at each one), and a press of any unit's physical button toggles **that unit** between call and slideshow ([guide 11 step 5](11-gpio-button.md#5-verify-the-physical-button)). The identities from step 05 show up here — each tile in the grid is labelled with the unit it belongs to.

Then the soak: leave the whole fleet powered and running overnight. The exact soak recipe — how long the fleet stays in-call versus resting on the slideshow — will be pinned down when this rollout is executed; the intent is that many hours of unattended running surface what a ten-minute test cannot, such as memory creep, an overnight crash, or the scheduled early-morning kiosk restart from [guide 12](12-systemd-and-reliability.md) misfiring. Lines 2 through 4 are the morning-after check, run on **each** unit: `uptime` proves the unit never rebooted (its `up` figure spans the whole night), `is-active` proves the services are still running, and `free -h` shows the memory headroom left after a night of operation.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user kill -s SIGUSR1 framelink-gpio.service
uptime
systemctl --user is-active framelink-spa.service chromium-kiosk.service framelink-gpio.service framelink-camera.service
free -h
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The toggle signal prints nothing while every screen switches to the call grid; the morning-after commands print each unit's uptime spanning the night, "active" four times, and a memory summary with headroom remaining.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

When line 1 runs, every frame in the room switches to the call grid within a few seconds — a frame that stays on its slideshow is not connected to the room, so recheck its identity and token from step 05. The morning after, on every unit: an `up` time longer than the soak, three `active` lines, and available memory comfortably above zero in the `free` output. A unit whose uptime is shorter than the night rebooted or crashed during the soak — investigate it before trusting it in a household.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The whole fleet has held a call together and survived a night of unattended running. The units are ready to leave the build network — the last two steps prepare each one for its destination household and prove it there.

<a id="9-pre-configure-the-household-wifi"></a>
<img src="https://img.shields.io/badge/STEP_09-Pre--configure_the_household_WiFi-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 09 — Pre-configure the household WiFi"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A unit that will use WiFi in its destination household currently knows only the network it was built on, and a frame on a wall has no keyboard to type a WiFi password with. (A unit going onto a wired network connection in its household can skip this step.)

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Store the destination household's WiFi name and password on the unit now, over SSH, before it ships. The moment it powers up in that home, it connects by itself.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Raspberry Pi OS manages networking with NetworkManager, and its command-line tool `nmcli` can store a WiFi profile for a network that is not in range yet: the profile sits dormant and connects automatically the first time the unit hears that network — which will be the moment it boots in the destination household. The first command is guarded by the repo's usual check-before-add pattern because `nmcli connection add` is not idempotent on its own (a re-run would create a duplicate profile). After the guard, its parts: `type wifi` and `ifname wlan0` bind the profile to the WiFi radio, `con-name household-wifi` names the profile, `ssid` is the destination network's name, and the two `wifi-sec` arguments store the WPA password. Replace `HOUSEHOLD-SSID` and `HOUSEHOLD-WIFI-PASSWORD` with the destination household's actual network name and password before running — collect them from the household ahead of delivery day. The stored password lands root-readable on the unit's card under `/etc/NetworkManager/system-connections/`, the same place guide 2's flash-time WiFi went. The second command lists all stored profiles as confirmation.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
nmcli -g NAME connection show | grep -qx household-wifi || sudo nmcli connection add type wifi ifname wlan0 con-name household-wifi ssid "HOUSEHOLD-SSID" wifi-sec.key-mgmt wpa-psk wifi-sec.psk "HOUSEHOLD-WIFI-PASSWORD"
nmcli -g NAME connection show
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The add prints a "Connection 'household-wifi' (...) successfully added" line with the new profile's identifier; the listing then shows household-wifi alongside the build network's connection.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

A `successfully added` line, and `household-wifi` present in the listing. Nothing connects right now — the destination network is out of range, which is expected; the profile activates on its own when the unit first hears that network. A typo in the network name or password only surfaces at the destination, so copy both carefully from the household.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The unit can join its destination household's network by itself the first time it powers up there. It has not left your house yet — the final step is the delivery and the proof in place.

<a id="10-deploy-and-verify-in-the-household"></a>
<img src="https://img.shields.io/badge/STEP_10-Deploy_and_verify_in_the_household-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 10 — Deploy and verify in the household"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Every unit has passed every test — in your house, on your network. The only unproven thing is the one that matters: the frame working in its real household, on that household's internet, for the family member it was built for.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Deliver the unit, plug it in, and watch it come up into the slideshow on its own. Then verify it remotely and make the first real call between households.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Placement is deliberately boring: position the frame (assembled per [guide 1](1-hardware-build-guide.md)), connect the wired network cable if the household uses one, and plug in the power supply. From power-on the unit needs no help — it joins the network (the step 09 profile, or the cable), starts its services, and rests on the photo slideshow. [Guide 9](9-immich-kiosk.md)'s offline cache means photos appear even if the home's internet path to your Immich server is briefly unavailable.

Your workstation is no longer on the unit's network, so `.local` SSH does not work here — the health check runs through the Raspberry Pi Connect remote shell (registered per unit in step 06), opened from [connect.raspberrypi.com/devices](https://connect.raspberrypi.com/devices). The commands are the same trio as step 07 without the reboot. If you skipped Connect, the visual check and the call below are the verification.

The last check is the real one: a call between households. Press the physical button on this unit (or on any already-deployed unit) — the frame joins the family room, and every other deployed frame auto-answers, exactly as in step 08 but now across the internet instead of one network. This is also the moment the LiveKit server's reachability is truly proven: a frame in another household can only join calls if the `livekitUrl` in its configuration is an address reachable from the wider internet, which is part of the server deployment pinned in [guide 7](7-livekit-server.md) — not something that can be fixed from the frame. Repeat steps 09 and 10 for each remaining unit and household.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user is-active framelink-spa.service chromium-kiosk.service framelink-gpio.service framelink-camera.service
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:8888/
docker ps --format '{{.Names}}: {{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. Run through the Raspberry Pi Connect remote shell, is-active prints "active" four times, curl prints "HTTP 200", and docker ps lists immich-kiosk as Up, while the frame in the household shows the slideshow.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The same pass as step 07 — three `active` lines, `HTTP 200`, `immich-kiosk: Up ...` — plus the household-side confirmation that photos are on the screen. Then the call: after a button press, the frames in both households show the call grid with live video and audio in both directions. A unit that shows its slideshow but never joins calls from its household is almost always a `livekitUrl` that is only reachable from your own network — revisit [guide 7](7-livekit-server.md).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame is live in its household: it boots into the slideshow by itself, it is remotely reachable for maintenance, and it holds real calls across the internet. When every unit has passed this step in its own household, the rollout is complete.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

Every unit boots unattended into the photo slideshow in its final household, joins family calls reliably — every frame visible in the grid, audio working in both directions, the button toggling between slideshow and call — and survives 24-hour unattended operation in place. The FrameLink fleet is deployed.
