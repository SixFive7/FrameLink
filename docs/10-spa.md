# Software Build Guide 10 — Kiosk SPA (Shell + LiveKit Client)

This guide deploys the FrameLink web app onto the Pi and serves it locally so the kiosk browser loads it. The app is the frame's brain: by default it shows the [Immich Kiosk](9-immich-kiosk.md) slideshow built in guide 9, and it switches to a [LiveKit](7-livekit-server.md) video call when the physical button from [guide 11](11-gpio-button.md) is pressed or an incoming call arrives. The app ships as plain files with its `lit` and `livekit-client` libraries vendored in, so there is no build step and no `npm`. We serve those files with `busybox httpd` bound to `127.0.0.1:8888` — already on the base image — so the frame depends on no external web host, then point the Chromium kiosk service at `http://localhost:8888/` and order it to wait for that local server before it opens.

---

<a id="1-clone-the-framelink-app-onto-the-pi"></a>
<img src="https://img.shields.io/badge/STEP_01-Clone_the_FrameLink_app_onto_the_Pi-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Clone the FrameLink app onto the Pi"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Pi has a slideshow and a camera, but it has none of the FrameLink app files that tie them together and run the video call. They have to get onto the Pi.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install `git`, then clone the FrameLink repository into the home folder so the app and its bundled libraries land on the Pi in one step.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The app is a static single-page application: an `index.html`, a handful of ES modules (`frame-app.js` and friends), and a `vendor/` folder holding pre-built copies of the two libraries it needs — `lit` (the small web-component framework the UI is written in) and `livekit-client` (the browser SDK that joins the video room). Because those libraries are vendored as ready-to-load files, the app has **no build step and no `npm`** — the browser loads the files exactly as they sit on disk. Cloning the whole repository is therefore all the "install" the app needs.

1. `sudo apt install -y git` puts `git` on the image; a fresh Raspberry Pi OS Lite install does not always carry it.
2. `[ -d ~/FrameLink ] || git clone ...` clones the repo into `~/FrameLink` **only if that folder does not already exist**, so re-running the step on a Pi that is already cloned does nothing instead of erroring with `destination path already exists`.

The clone lands the served app at `~/FrameLink/app`, which is the document root the local web server in [step 3](#3-serve-the-app-locally) points at.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install -y git
[ -d ~/FrameLink ] || git clone https://github.com/SixFive7/FrameLink.git ~/FrameLink
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The authoritative first-run output will show apt installing git (and any dependencies the base image lacks), then git's clone progress — "Cloning into '/home/framelink/FrameLink'...", object-counting and receiving lines, ending back at the prompt.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `apt` line finishes with `git` set up and no `E:` error, and `git clone` ends with a `Receiving objects: 100%` / `Resolving deltas: 100%` pair and no `fatal:` line. If you see `fatal: destination path '/home/framelink/FrameLink' already exists`, the guard did not fire because the folder was there — that is harmless, the existing clone is reused. A `Could not resolve host: github.com` means the Pi has no internet path to GitHub.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The FrameLink app and its vendored `lit` and `livekit-client` libraries are on the Pi at `~/FrameLink/app`, ready to configure and serve. The app cannot connect to anything yet — it has no configuration.

<a id="2-create-the-app-configuration"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_app_configuration-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the app configuration"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The app does not know which slideshow to show, which video room to join, or the secret that proves this frame is allowed to join it. Those values are unique to your setup, so they cannot be baked into the shared code.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Copy the bundled example configuration to `config.json`, then fill in this unit's identity, room, LiveKit address, the local slideshow URL, and the LiveKit token.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The app reads a single file, `app/config.json`, at start-up. The repo ships `app/config.example.json` as a template you copy and edit — the real `config.json` is per-unit and is not committed. Its five fields:

1. `identity` — this frame's LiveKit identity, the name other callers see (e.g. `framelink-douwe`). It must be unique per unit so two frames in the same room do not collide.
2. `room` — the LiveKit room every family device joins (e.g. `family`). All frames and phones that should be able to call each other share one room name.
3. `livekitUrl` — the WebSocket address of the LiveKit server from [guide 7](7-livekit-server.md), in the form `ws://YOUR-LIVEKIT-HOST:7880`. This is where the app connects to place and receive calls.
4. `immichKioskUrl` — the **local** Immich Kiosk slideshow URL from [guide 9](9-immich-kiosk.md). The example already contains `http://127.0.0.1:3000/` with the full set of display query parameters the frame needs — `disable_ui`, `hide_cursor`, `disable_navigation`, `frameless`, `image_fit=cover`, `transition=fade`, `duration=30`, and `use_offline_mode=true` (which tells Kiosk to serve cached photos when your Immich server is unreachable). Leave it as shipped unless you changed Kiosk's port.
5. `token` — a long-lived LiveKit access token minted for this `identity` and `room` per [guide 7](7-livekit-server.md). **This is a secret**: it grants the bearer the right to join your video room. It is never printed in this guide and must not be committed — paste it into `config.json` and leave it only there.

The block below copies the example to `config.json` (only if `config.json` does not already exist, so a re-run never clobbers a token you already pasted), then opens the file in `nano` for you to fill in the five values. Editing the file in place keeps the secret token off the terminal and out of your shell history.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cd ~/FrameLink/app
[ -f config.json ] || cp config.example.json config.json
nano config.json
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The cp is silent; nano then opens the editor full-screen showing the five-field JSON (identity, room, livekitUrl, immichKioskUrl, token) for editing. Nothing is printed back to the prompt until you save and exit.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`nano` opens on the JSON template. Set `identity`, `room`, and `livekitUrl` to this unit's values, paste your long-lived token into `token` (between the quotes), and leave `immichKioskUrl` as shipped unless you changed Kiosk's port in [guide 9](9-immich-kiosk.md). Save with `Ctrl+O`, `Enter`, then exit with `Ctrl+X`. A trailing comma or a missing quote makes the file invalid JSON and the app falls back to its built-in defaults (so the slideshow and call will not work) — keep the punctuation exactly as in the example.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The app now has its per-unit configuration: which slideshow to embed, which LiveKit room to join, and the token that authorises it. The files still are not being served to a browser — that is the next step.

<a id="3-serve-the-app-locally"></a>
<img src="https://img.shields.io/badge/STEP_03-Serve_the_app_locally-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Serve the app locally"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The app is just files on disk. A browser cannot load an app reliably from `file://` — the modules and the LiveKit connection need a real web server — and we do not want to depend on a web host somewhere else on the internet that could go down.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Serve the app folder over HTTP from the Pi itself using `busybox httpd`, run as a systemd user service so it starts on boot and restarts if it ever stops.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`busybox` is already on the Raspberry Pi OS Lite image, and it includes a tiny static web server, `httpd` — no package to install. We point it at the app folder and bind it to localhost only, so the app is reachable by the Pi's own browser but never exposed to the network.

The service is defined exactly as in `deploy/systemd/framelink-spa.service`. Its `ExecStart` runs `busybox httpd -f -p 127.0.0.1:8888 -h /home/framelink/FrameLink/app`: `-f` keeps it in the foreground so systemd can supervise it, `-p 127.0.0.1:8888` binds it to port 8888 on loopback only, and `-h` sets the document root to the app folder cloned in [step 1](#1-clone-the-framelink-app-onto-the-pi). `Restart=always` with `RestartSec=2` relaunches it within two seconds if it ever exits.

This is a **`--user` service**, matching the Chromium kiosk service from [guide 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service): both run under the `framelink` user session, which is what lets the next step order the browser after this server with a simple `--user` dependency. `daemon-reload` makes systemd read the new unit file; `enable --now` both enables it for future boots and starts it immediately. The final `curl` asks the server for its root page and prints just the HTTP status, confirming it is actually serving.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user
tee ~/.config/systemd/user/framelink-spa.service << 'EOF'
[Unit]
Description=FrameLink SPA static server (busybox httpd)
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/busybox httpd -f -p 127.0.0.1:8888 -h /home/framelink/FrameLink/app
Restart=always
RestartSec=2

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now framelink-spa.service
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:8888/
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the eleven-line unit file; daemon-reload is silent; enable --now prints the "Created symlink ... framelink-spa.service" line and otherwise nothing; the final curl prints "HTTP 200".]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The closing `curl` prints `HTTP 200`, proving `busybox httpd` is serving the app on `127.0.0.1:8888`. `enable --now` prints a `Created symlink` line and is otherwise silent. A `Failed to connect to bus` error from `systemctl --user` means this SSH login has no user session bus — confirm the autologin session from [guide 5](5-kiosk-base.md#3-enable-console-autologin) is active, or log out and back in. If `curl` prints `HTTP 000` or `Connection refused`, the service did not start — check `systemctl --user status framelink-spa.service`.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The FrameLink app is being served on the Pi at `http://127.0.0.1:8888/`, from a service that restarts on its own and comes back after a reboot, with no dependency on any external web host. The kiosk browser is still pointed at the placeholder URL from guide 5 — the next step redirects it here.

<a id="4-point-the-kiosk-browser-at-the-app"></a>
<img src="https://img.shields.io/badge/STEP_04-Point_the_kiosk_browser_at_the_app-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Point the kiosk browser at the app"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Chromium kiosk service from guide 5 still opens a placeholder page, and even pointed at the right address it could launch before the local server is ready — on a cold boot the browser and the server start at the same time, and a browser that opens first sees a "connection refused" error page and stays stuck on it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Replace the Chromium service with its final form: point it at the local app and order it to wait until the Wayland display and the local server are both ready before it opens.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

This is the **final** `chromium-kiosk.service`, defined exactly as in `deploy/systemd/chromium-kiosk.service`. Three things change from the guide 5 version, and all three matter for an unattended cold boot:

1. The `ExecStart` URL is now `http://localhost:8888/` — the local server from [step 3](#3-serve-the-app-locally) — instead of the placeholder. All the other Chromium flags (`--kiosk`, `--ozone-platform=wayland`, the camera flags from [guide 6](6-camera.md), and so on) are unchanged.
2. `After=...framelink-spa.service` and `Wants=framelink-spa.service` tie the browser to the local server: `Wants` pulls the server into the same start-up, and `After` makes systemd *start* the browser after the server — but "after the service started" is not the same as "after the server is answering requests", which is why the readiness wait below is still needed.
3. Two `ExecStartPre` guards make Chromium **block until its world is ready**. The first, `while [ ! -S "/run/user/$(id -u)/${WAYLAND_DISPLAY}" ]; do sleep 0.1; done`, waits for the Wayland socket to exist so Chromium has a display to draw on. The second, `until curl -sf http://127.0.0.1:8888/ >/dev/null 2>&1; do sleep 0.3; done`, polls the local server every 0.3 s and only returns once it answers — so Chromium never opens before the app is actually being served. Together these defeat the cold-boot race: whichever of display or server is slower, Chromium waits for it.

`daemon-reload` reads the rewritten unit; `restart` relaunches Chromium against the new URL with the new guards.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user
tee ~/.config/systemd/user/chromium-kiosk.service << 'EOF'
[Unit]
Description=Chromium Kiosk Browser
After=graphical-session.target framelink-spa.service
Wants=framelink-spa.service
Requires=graphical-session.target

[Service]
Type=simple
Environment="WAYLAND_DISPLAY=wayland-0"
ExecStartPre=/bin/bash -c 'while [ ! -S "/run/user/$(id -u)/${WAYLAND_DISPLAY}" ]; do sleep 0.1; done'
ExecStartPre=/bin/bash -c 'until curl -sf http://127.0.0.1:8888/ >/dev/null 2>&1; do sleep 0.3; done'
ExecStart=/usr/bin/chromium \
  --ozone-platform=wayland \
  --user-data-dir=/tmp/framelink-chromium \
  --kiosk \
  --noerrdialogs \
  --disable-infobars \
  --disable-session-crashed-bubble \
  --no-first-run \
  --auto-accept-camera-and-microphone-capture \
  --enable-features=UsePipeWireCamera \
  --autoplay-policy=no-user-gesture-required \
  --disable-background-timer-throttling \
  --disable-renderer-backgrounding \
  http://localhost:8888/
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user restart chromium-kiosk.service
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the rewritten unit file; daemon-reload and restart are both silent on success, and the Pi's screen switches from the placeholder to the FrameLink app loading the slideshow.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`tee` echoes the unit file; the two `systemctl --user` commands are silent on success, and the DSI screen reloads into the FrameLink app showing the slideshow. Confirm the running browser carries the new URL with `pgrep -a chromium | grep -o 'localhost:8888'`. A `Failed to connect to bus` error means the user session bus is unavailable from this login — see the note in [step 3](#3-serve-the-app-locally). If the screen sits on a spinner, the local server is not answering — recheck [step 3](#3-serve-the-app-locally)'s `curl`.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The kiosk browser now loads the FrameLink app from the local server, and it is ordered to wait for both the display and that server before it opens — so it survives a cold boot without landing on an error page. The final step confirms the whole frame comes up correctly.

<a id="5-verify-the-frame-works"></a>
<img src="https://img.shields.io/badge/STEP_05-Verify_the_frame_works-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Verify the frame works"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Every piece is configured, but we have not yet confirmed the frame actually comes up into a working slideshow on its own — the way it will every time it is switched on.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Reboot the Pi, then check that both services are active and the local server answers — and look at the screen to confirm the photos are showing.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

A reboot is the real test, because it exercises the full cold-boot ordering: the slideshow server, the Wayland session, the readiness guards, and Chromium all coming up together. After the Pi is back, `systemctl --user is-active framelink-spa chromium-kiosk` reports the live state of both user services — both should read `active`. The `curl` re-confirms the local server is serving the app. The authoritative confirmation, though, is the screen itself: a working frame shows the Immich photos from [guide 9](9-immich-kiosk.md) filling the display.

The frame's other mode — the video call — is not exercised here. The app switches to the call grid when it is told to, either by the physical button wired in [guide 11](11-gpio-button.md) (which sends a `toggle` command over a localhost WebSocket) or by an incoming call (the app auto-answers when a remote participant joins the room). With no button yet and no caller, the frame correctly rests on the slideshow; call mode is validated once [guide 11](11-gpio-button.md) is done.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo reboot
systemctl --user is-active framelink-spa chromium-kiosk
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:8888/
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. sudo reboot drops the SSH session with the client's disconnect line; after reconnecting, is-active prints "active" twice (one per service) and curl prints "HTTP 200", while the DSI screen shows the Immich slideshow.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

After reconnecting, `is-active` prints `active` on two lines and `curl` prints `HTTP 200`, and — the real proof — the screen is cycling through your Immich photos full-screen. If `is-active` prints `inactive` or `failed` for either service, inspect it with `systemctl --user status <name>`; if the screen shows a spinner instead of photos, the slideshow URL or the Immich Kiosk container from [guide 9](9-immich-kiosk.md) is the place to look. You will not see the video grid here — that is expected until the button from [guide 11](11-gpio-button.md) is wired.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame boots on its own into the FrameLink app and rests on the Immich slideshow, served entirely from the Pi. The app is ready to switch into a video call the moment the button from [guide 11](11-gpio-button.md) is connected or a call comes in.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

After a reboot the Pi comes up on its own into the FrameLink app: `systemctl --user is-active framelink-spa chromium-kiosk` reports `active` for both services, `curl http://127.0.0.1:8888/` returns HTTP `200`, and the DSI screen shows the Immich slideshow full-screen with no manual step. Call mode is wired and ready in the app, and is exercised once the GPIO button from [guide 11](11-gpio-button.md) is connected or an incoming call arrives.
