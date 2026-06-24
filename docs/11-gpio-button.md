# Software Build Guide 11 — GPIO Button Daemon

The frame rests on the [Immich Kiosk](9-immich-kiosk.md) slideshow and switches to a [LiveKit](7-livekit-server.md) video call when someone presses the physical button on the case. This guide connects that button to the [FrameLink app](10-spa.md). The app already listens for a `toggle` command on a localhost WebSocket; what is missing is something that watches the button and sends that command when it is pressed. That something is a small Python daemon — `framelink-gpio.py` — which already arrived on the Pi with the `git clone` in [guide 10](10-spa.md), so this guide does not write any code. It installs the libraries the daemon needs, confirms the daemon is set to the pin the button is actually wired to, runs the daemon as a service that starts on boot, and then verifies the toggle both with and without the physical button.

---

<a id="1-install-the-gpio-and-websocket-packages"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_the_GPIO_and_WebSocket_packages-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install the GPIO and WebSocket packages"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The button daemon is on the Pi, but the libraries it relies on to read a GPIO pin and to talk over a WebSocket are not part of a fresh Raspberry Pi OS Lite image. Without them the daemon cannot start.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the three libraries the daemon needs from Raspberry Pi OS's own package archive with `apt`, including the low-level GPIO backend that the Pi 5 requires.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The daemon imports three things, and each maps to one package:

1. `python3-gpiozero` — gpiozero, the friendly Python library the daemon uses to represent the button (`Button(BUTTON_PIN, pull_up=True, ...)`) and to call back when it is pressed.
2. `python3-lgpio` — the lgpio backend. This is **required on the Pi 5 and Trixie**: gpiozero does not talk to the GPIO hardware itself, it hands off to a "pin factory", and on the Pi 5 the working factory is `LGPIOFactory`, which needs this package. If it is missing, gpiozero does **not** error — it silently falls back to a *mock* pin factory that pretends to work, so the daemon starts cleanly but the button never fires. Installing it is what makes the press real.
3. `python3-websockets` — the `websockets` library that runs the daemon's WebSocket server on `127.0.0.1:8889`, which the app connects to as a client.

These are installed with `apt`, not `pip`, on purpose. Debian 13 (Trixie) marks the system Python as "externally managed" and enforces [PEP 668](https://peps.python.org/pep-0668/), which blocks `pip install` into the system environment to stop it fighting with `apt`. The apt-packaged versions are built for this exact OS and are the supported way to add these libraries. Re-running the command is safe: `apt` reports the packages are already the newest version and changes nothing.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install -y python3-gpiozero python3-lgpio python3-websockets
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The authoritative first-run output shows apt resolving and installing python3-gpiozero, python3-lgpio, python3-websockets and any dependencies, ending back at the prompt with no E: error.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The command ends with all three packages installed and no `E:` line. On a re-run, `apt` prints that the packages are already the newest version instead of installing them — that is expected and harmless. The one package you must not skip is `python3-lgpio`: without it the daemon will appear to run but the button does nothing, because gpiozero quietly uses a mock pin instead of the real hardware.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Pi has the GPIO and WebSocket libraries the button daemon needs, including the lgpio backend the Pi 5 depends on. The daemon still is not running, and the button is not wired yet — those come next.

<a id="2-wire-the-button-and-set-its-pin"></a>
<img src="https://img.shields.io/badge/STEP_02-Wire_the_button_and_set_its_pin-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Wire the button and set its pin"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A push button is just two wires until it is connected to the Pi's pins, and the daemon has to be told which pin to watch. If the wire and the daemon disagree about the pin, pressing the button does nothing.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Connect the button between a GPIO pin and a ground pin on the Pi's header, then confirm the daemon is set to that same pin — editing one line if your wiring uses a different pin.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The momentary push button has two terminals. One wire goes to a **GPIO pin** and the other to a **ground pin**. The documented default is BCM **GPIO17**, which is **physical pin 11** on the 40-pin header, with the other side on a ground pin such as **physical pin 9** (the two sit one row apart, which keeps the wiring short). "Momentary" means the button only connects the two pins while it is actually held down, then springs back open — exactly what a toggle press wants.

There is no separate resistor to fit, because the daemon turns on the Pi's *internal* pull-up: `Button(BUTTON_PIN, pull_up=True, ...)`. With the pull-up enabled, the GPIO pin sits at a steady "high" (3.3 V) while the button is open; pressing the button connects that pin straight to ground, pulling it "low", and gpiozero reports a press on that high→low change. A `bounce_time` of 0.05 s tells gpiozero to ignore the tiny electrical chatter a mechanical button makes as its contacts settle, so one physical press counts as exactly one press. Because the internal pull-up does the work, the button needs nothing but the two wires.

The pin the daemon watches is the constant `BUTTON_PIN` at the top of `~/FrameLink/deploy/gpio/framelink-gpio.py`, shipped as `17`. The [hardware build guide](1-hardware-build-guide.md) does not commit you to one specific pin, so GPIO17 is the documented default rather than a fixed requirement. The command below prints that line so you can see what the daemon is currently set to. If you wired the button to GPIO17 / pin 11, it already matches and there is nothing to change. If you used a different GPIO pin, edit that one number to the BCM number of your pin (for example, open the file with `nano ~/FrameLink/deploy/gpio/framelink-gpio.py` and change `BUTTON_PIN = 17`) so the daemon watches the pin your button is actually on.

The physical wiring itself is a hardware action — the command here only reads the file; it does not and cannot connect the wires for you.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
grep -n BUTTON_PIN ~/FrameLink/deploy/gpio/framelink-gpio.py
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The authoritative capture shows the BUTTON_PIN line from the shipped daemon, set to 17, with its line number prefix from grep -n.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`grep` prints the `BUTTON_PIN = 17` line (with its line number). If that `17` is the BCM pin you wired the button to, you are done with this step. If your button is on a different pin, change the number to match before moving on. A `No such file or directory` here means the clone from [guide 10](10-spa.md) did not land at `~/FrameLink` — go back and confirm that step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The button is wired between a GPIO pin and ground using the Pi's internal pull-up, and the daemon is confirmed to be watching that same pin. Nothing is reading the button yet — the next step starts the daemon that does.

<a id="3-run-the-daemon-as-a-service"></a>
<img src="https://img.shields.io/badge/STEP_03-Run_the_daemon_as_a_service-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Run the daemon as a service"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The daemon exists on disk and its libraries are installed, but nothing is running it. It needs to be started, to come back after a reboot, and to restart on its own if it ever stops — the same way the rest of the frame's services do.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the daemon as a systemd user service that starts on boot and restarts itself, then confirm it is running and listening for the app.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The service is defined exactly as in `deploy/systemd/framelink-gpio.service`. Its `ExecStart` runs `/usr/bin/python3 /home/framelink/FrameLink/deploy/gpio/framelink-gpio.py` — the daemon cloned in [guide 10](10-spa.md). `Restart=always` with `RestartSec=3` relaunches it within three seconds if it ever exits, and `After=graphical-session.target` lets it start alongside the rest of the frame's session.

This is a **`--user` service**, matching the SPA server and the Chromium browser from [guide 10](10-spa.md): all three run under the `framelink` user session. Running the daemon as the user (rather than as root via a system service) keeps it in the same session as the app it talks to, and on the Pi 5 the `framelink` user already has the group membership to reach the GPIO hardware. `daemon-reload` makes systemd read the new unit file; `enable --now` both enables it for future boots and starts it immediately.

When it starts, the daemon opens a WebSocket **server** on `127.0.0.1:8889` and waits. The app (`app/control.js`) is the **client**: it connects to that address and reacts to a `{"cmd":"toggle"}` message by switching between the slideshow and the call. The closing `ss -tlnp` lists the ports that are open and listening; filtering for `8889` confirms the daemon is actually accepting connections on the port the app expects.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user
tee ~/.config/systemd/user/framelink-gpio.service << 'EOF'
[Unit]
Description=FrameLink GPIO button daemon
After=graphical-session.target

[Service]
Type=simple
ExecStart=/usr/bin/python3 /home/framelink/FrameLink/deploy/gpio/framelink-gpio.py
Restart=always
RestartSec=3

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now framelink-gpio.service
ss -tlnp | grep 8889
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The authoritative capture shows tee echoing the unit file, enable --now printing the "Created symlink ... framelink-gpio.service" line, and ss listing a LISTEN socket on 127.0.0.1:8889 owned by the python3 daemon.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The final `ss` line shows a `LISTEN` socket on `127.0.0.1:8889` with `python3` named as its process — that is the daemon up and accepting connections. `enable --now` prints a `Created symlink` line and is otherwise silent. If `ss` prints nothing, the daemon did not start or did not bind the port — check `systemctl --user status framelink-gpio.service` and `journalctl --user -u framelink-gpio.service`; a traceback mentioning a mock pin factory points back to a missing `python3-lgpio` from [step 1](#1-install-the-gpio-and-websocket-packages). A `Failed to connect to bus` error from `systemctl --user` means this SSH login has no user session bus — confirm the autologin session is active, or log out and back in.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The button daemon is running as a user service, listening on `127.0.0.1:8889`, and set to restart on its own and come back after a reboot. The app can now reach it. The next step proves the toggle works even before you touch the physical button.

<a id="4-test-the-toggle-without-the-button"></a>
<img src="https://img.shields.io/badge/STEP_04-Test_the_toggle_without_the_button-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Test the toggle without the button"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

We want to know the daemon-to-app path actually switches the screen between the slideshow and the call — but reaching behind the frame to press the button for every test is awkward, and a wiring fault could hide a working daemon.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tell the daemon to act as if the button was pressed by sending it a signal, and watch the screen toggle. Run it once to switch to the call, again to switch back.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The daemon listens for the `SIGUSR1` signal — a standard "user-defined" signal — and its handler runs the **exact same** `broadcast("toggle")` that a real button press triggers (`button.when_pressed` and the signal handler both call the one `broadcast` function inside the daemon). So sending `SIGUSR1` exercises the entire path that matters here: the daemon builds the `{"cmd":"toggle"}` message and pushes it over the WebSocket to the app, and the app flips between slideshow and call. The only piece it does *not* exercise is the physical wire from the button to the GPIO pin — that is verified in [step 5](#5-verify-the-physical-button).

`systemctl --user kill -s SIGUSR1 framelink-gpio.service` delivers that signal to the running daemon. Each time you run it the screen toggles: the first run switches the frame from the slideshow into the LiveKit call (the camera publishes and the call grid appears), and a second run switches it back to the slideshow. Run it as many times as you like — it is a clean, repeatable way to confirm the daemon and the app are wired together correctly.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl --user kill -s SIGUSR1 framelink-gpio.service
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The command itself prints nothing; the authoritative confirmation is the DSI screen switching from the slideshow into the LiveKit call on the first signal, and back to the slideshow on the second.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The command is silent — the proof is on the screen. The first run switches the frame from the photo slideshow into the video call (you see the call grid and the frame's camera goes live); running it again switches it back to the slideshow. If the screen does not change, the app is not connected to the daemon: re-check that the daemon is listening on `127.0.0.1:8889` in [step 3](#3-run-the-daemon-as-a-service), and that the SPA and Chromium services from [guide 10](10-spa.md) are running.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have confirmed the daemon-to-app path works end to end: a simulated press toggles the frame between the slideshow and the call, and back. All that is left is to prove the physical button drives that same toggle.

<a id="5-verify-the-physical-button"></a>
<img src="https://img.shields.io/badge/STEP_05-Verify_the_physical_button-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Verify the physical button"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The simulated press proved everything except the one thing only the hardware can prove: that the actual button, wired to the actual pin, makes the frame toggle when someone pushes it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Press the physical button on the frame and watch the screen toggle between the slideshow and the call, exactly as the simulated press did.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

This is the real end-to-end test. Pressing the button connects the GPIO pin to ground, gpiozero reports the press, the daemon runs the same `broadcast("toggle")` the signal test used, and the app switches modes. Because [step 4](#4-test-the-toggle-without-the-button) already confirmed the daemon-to-app half, a press that toggles the screen now also confirms the half the signal could not reach — the button, the two wires, the internal pull-up, and the GPIO pin the daemon is watching.

There is no SSH command for this step — it is a physical action at the frame. Push the button once and the slideshow switches to the call; push it again and the call returns to the slideshow. If the daemon is set to a different pin than the one you wired, this is where it shows up: the signal test in [step 4](#4-test-the-toggle-without-the-button) would still have worked, but the physical press does nothing — go back to [step 2](#2-wire-the-button-and-set-its-pin) and make `BUTTON_PIN` match your wiring.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
journalctl --user -u framelink-gpio.service -n 20 --no-pager
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture, confirmed at the wired-button capture session. The authoritative capture shows the daemon's recent journal lines with no errors after a physical press toggles the screen between slideshow and call.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The real check is the screen: a press toggles it from slideshow to call, and the next press toggles it back. The `journalctl` lines should show the daemon running cleanly with no Python traceback. If the press does nothing while the [step 4](#4-test-the-toggle-without-the-button) signal worked, the pin is the suspect — confirm the wire is on the pin the daemon watches and that `BUTTON_PIN` in [step 2](#2-wire-the-button-and-set-its-pin) matches it. If neither the press nor the signal toggles the screen, the daemon or the app connection is the place to look, not the wiring.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The physical button now toggles the frame between the photo slideshow and the video call. The frame is functionally complete: it rests on photos, and one push of the button starts a call.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`systemctl --user is-active framelink-gpio.service` reports `active`, `ss -tlnp | grep 8889` shows the daemon listening on `127.0.0.1:8889`, sending `SIGUSR1` with `systemctl --user kill -s SIGUSR1 framelink-gpio.service` toggles the DSI screen between the slideshow and the LiveKit call, and pressing the physical button on the frame does the same. The button daemon is set to restart on its own and to come back after a reboot, alongside the SPA and Chromium services from [guide 10](10-spa.md).
