# Software Build Guide 06 — Camera (libcamera → PipeWire → desktop portal)

Make the Pi Camera Module 3 available to Chromium's `getUserMedia()` using Raspberry Pi OS's modern camera path: libcamera → PipeWire → the desktop portal. PipeWire and WirePlumber are already running on the base image (they are Pi OS Trixie's audio stack), so this guide adds only the libcamera PipeWire plugin and the desktop portal that Chromium requests a camera through, points that portal at the labwc session so it offers a Camera interface, and pre-authorizes camera access so the unattended kiosk never blocks on a permission dialog. The Chromium flag that selects this camera path (`--enable-features=UsePipeWireCamera`) was set when the kiosk service was created in [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service); this guide makes the camera that flag points at actually exist. There is no `v4l2loopback` module, no GStreamer bridge, and no `/dev/video8` — the Pi Camera reaches Chromium directly through libcamera.

---

<a id="1-install-the-camera-portal-packages"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_the_camera_portal_packages-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install the camera portal packages"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A fresh Raspberry Pi OS Lite image runs PipeWire for audio, but nothing teaches PipeWire about the Pi Camera, and nothing gives Chromium the "desktop portal" it asks a camera through. Without these pieces, `getUserMedia()` finds no camera and hangs.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the three packages the modern camera path needs, in a single `apt install` call.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Three packages, each doing one job:
1. `libspa-0.2-libcamera` — the PipeWire SPA plugin that enumerates libcamera cameras (the Pi Camera) and exposes them as PipeWire camera nodes. Without it, PipeWire knows about the microphone but not the camera.
2. `xdg-desktop-portal` — the "desktop portal" daemon. Chromium's PipeWire camera backend does not open a camera device directly; it asks the portal's `org.freedesktop.portal.Camera` interface for access and receives a PipeWire handle in return. This is the frontend half of the portal.
3. `xdg-desktop-portal-gtk` — a portal *backend*. The portal frontend only exposes the Camera interface when a backend is present that implements the `Access` permission service; the GTK backend is the standard, lightweight one that provides it. (PipeWire `1.4.x` and WirePlumber `0.5.x` are already installed and running from the base image, so they are not listed here.)

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install -y xdg-desktop-portal xdg-desktop-portal-gtk libspa-0.2-libcamera
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The authoritative first-run output will be inserted from a freshly flashed card. On a configured test unit this pulled xdg-desktop-portal, xdg-desktop-portal-gtk, libspa-0.2-libcamera plus a small number of dependencies (bubblewrap, libjson-glib); the exact dependency set and "Setting up ..." lines depend on what the base image already carries and must be captured clean.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

In the authoritative capture: a `Setting up xdg-desktop-portal ...`, `Setting up xdg-desktop-portal-gtk ...`, and `Setting up libspa-0.2-libcamera ...` line, each completing without error. Any line containing `E:` or `dpkg: error` is fatal — the later steps depend on all three packages being present.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

PipeWire now has the plugin it needs to see the Pi Camera, and the desktop portal that Chromium asks a camera through is installed. Nothing is wired together yet — the portal still has to be pointed at this device's session.

<a id="2-point-the-desktop-portal-at-the-labwc-session"></a>
<img src="https://img.shields.io/badge/STEP_02-Point_the_desktop_portal_at_the_labwc_session-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Point the desktop portal at the labwc session"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The portal is installed but offers no Camera interface yet. It decides which interfaces to expose by reading an environment variable, `XDG_CURRENT_DESKTOP`, to pick a configuration — and the kiosk starts the labwc desktop in a bare way that never sets that variable.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a tiny systemd drop-in that sets `XDG_CURRENT_DESKTOP=labwc` for the portal service, then reload and restart the portal so it picks up its configuration.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Raspberry Pi OS ships a portal configuration file at `/usr/share/xdg-desktop-portal/labwc-portals.conf`, but the portal only uses it when `XDG_CURRENT_DESKTOP` contains `labwc`. The kiosk launches the compositor with a bare `exec labwc` (from [guide 5](5-kiosk-base.md)), which does not export that variable, so the portal falls back to a degraded mode that exposes only a handful of trivial interfaces — Camera not among them.

`xdg-desktop-portal` runs as a per-user systemd service, so the cleanest fix is a service drop-in. A file under `~/.config/systemd/user/xdg-desktop-portal.service.d/` adds an `Environment=` line that systemd applies whenever it starts the portal — including the cold-boot case, where Chromium's first camera request D-Bus-activates the portal. `daemon-reload` makes systemd read the new drop-in; `restart` relaunches the portal with the variable set so it loads `labwc-portals.conf` and exposes the full interface set, Camera included.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user/xdg-desktop-portal.service.d
tee ~/.config/systemd/user/xdg-desktop-portal.service.d/desktop.conf << 'EOF'
[Service]
Environment=XDG_CURRENT_DESKTOP=labwc
EOF
systemctl --user daemon-reload
systemctl --user restart xdg-desktop-portal
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the two written lines ("[Service]" and "Environment=XDG_CURRENT_DESKTOP=labwc"); the two systemctl commands print nothing on success.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`tee` echoes the file body back to the terminal, so you should see the `[Service]` and `Environment=XDG_CURRENT_DESKTOP=labwc` lines exactly as written. The two `systemctl --user` commands are silent when they succeed; a `Failed to connect to bus` error means the user session bus is not reachable from this SSH login — log out and back in, or confirm the autologin session from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) is active.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The portal now knows it is running under labwc and offers the Camera interface that Chromium needs. The camera is not yet authorized for unattended use — that is the next step.

<a id="3-pre-authorize-camera-access-for-the-kiosk"></a>
<img src="https://img.shields.io/badge/STEP_03-Pre--authorize_camera_access_for_the_kiosk-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Pre-authorize camera access for the kiosk"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The first time Chromium asks the portal for the camera, the portal pops up a "Allow app to use the camera?" window and waits for someone to click "Grant". On a wall-mounted frame with no keyboard, nobody ever clicks it, so the call freezes with a black self-view forever.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Record a permanent "yes" for the camera in the portal's permission store, once, so the portal grants access silently from then on and never shows the window.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The portal keeps per-application device permissions in a small on-disk database managed by the `org.freedesktop.impl.portal.PermissionStore` D-Bus service (the file lives at `~/.local/share/flatpak/db/devices`). When an app requests the camera, the portal looks up the app's permission: `yes` grants silently, `no` denies silently, and *unset* triggers the GTK "Allow?" dialog. We write `yes` up front so the dialog never appears.

The `busctl --user call` below invokes the store's `SetPermission` method with: table `devices`, create-if-missing `true`, id `camera`, application id `""` (the empty string — the identifier the portal uses for an unsandboxed host application like the packaged Chromium), and the permission list `yes` (the trailing `1 yes` is "a one-element list whose value is yes"). The setting is written to disk, so it persists across reboots and is run once and never again. It is idempotent: writing `yes` a second time changes nothing. The second command reads the value back as a confirmation.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
busctl --user call org.freedesktop.impl.portal.PermissionStore /org/freedesktop/impl/portal/PermissionStore org.freedesktop.impl.portal.PermissionStore SetPermission sbssas devices true camera "" 1 yes
busctl --user call org.freedesktop.impl.portal.PermissionStore /org/freedesktop/impl/portal/PermissionStore org.freedesktop.impl.portal.PermissionStore Lookup ss devices camera
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. SetPermission prints nothing on success; the Lookup line reports the stored permission, e.g.  a{sas}v 1 "" 1 "yes" y 0  — an entry mapping the empty app id to the list ["yes"].]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `SetPermission` call is silent on success. The `Lookup` line must contain `"" 1 "yes"` — the empty application id mapped to a one-element list whose single value is `yes`. If `Lookup` reports `No entry for camera`, the `SetPermission` call did not land; re-run it and confirm there was no `Failed to connect to bus` error.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Camera access is permanently granted for the kiosk. When the frame enters a call, the portal will hand Chromium the camera with no dialog and no human in the loop. The remaining steps only confirm the pieces are in place.

<a id="4-confirm-the-pi-camera-is-a-pipewire-camera"></a>
<img src="https://img.shields.io/badge/STEP_04-Confirm_the_Pi_Camera_is_a_PipeWire_camera-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Confirm the Pi Camera is a PipeWire camera"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

We installed the plugin that should make PipeWire see the Pi Camera, but we have not yet checked that it actually did. If PipeWire cannot see the camera, nothing downstream will either.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask PipeWire to list its devices and confirm the IMX708 camera appears.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`wpctl status` prints WirePlumber's view of every audio and video device PipeWire currently manages. After the libcamera plugin from step 1 is loaded (WirePlumber picks it up on the next session restart or login), the Camera Module 3's sensor appears in the **Video** section: as a device labelled `imx708 [libcamera]`, and as a usable capture **Source** named `imx708`. The `[libcamera]` tag is the proof that this is the native libcamera path and not a raw V4L2 node. `grep -i imx708` narrows the long listing to just those lines.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
wpctl status | grep -i imx708
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a configured unit this shows two lines: an  imx708  device entry tagged  [libcamera]  under Video > Devices, and an  imx708  entry under Video > Sources.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

At least one line mentioning `imx708`, with the device line tagged `[libcamera]`. If `grep` returns nothing, WirePlumber has not picked up the libcamera plugin: `systemctl --user restart wireplumber` and re-check. If `rpicam-hello --list-cameras` (from [guide 3](3-hardware-configuration.md)) does not list the IMX708 either, the problem is upstream of PipeWire — recheck the camera ribbon and the `camera_auto_detect=1` line in `/boot/firmware/config.txt`.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

PipeWire sees the Pi Camera as a first-class camera source. The portal can now hand this source to any application that asks for a camera.

<a id="5-confirm-the-camera-portal-is-on-the-session-bus"></a>
<img src="https://img.shields.io/badge/STEP_05-Confirm_the_Camera_portal_is_on_the_session_bus-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Confirm the Camera portal is on the session bus"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Step 2 was supposed to make the portal offer a Camera interface. Chromium will hang on a black self-view if that interface is missing, so we confirm it is really there before relying on it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the portal on the session bus to list its interfaces and confirm `Camera` is among them.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`busctl --user introspect` lists every interface a D-Bus object publishes. The desktop portal lives at the bus name `org.freedesktop.portal.Desktop` on the object path `/org/freedesktop/portal/desktop`. When the portal is correctly configured (step 2) and a backend providing the permission service is installed (the GTK backend from step 1), it publishes `org.freedesktop.portal.Camera` here. `grep -i camera` filters the long interface list to the one line that matters. If step 2 had failed, this command would print nothing — which is exactly the failure that makes `getUserMedia()` hang.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop | grep -i camera
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a configured unit this prints a single line:  org.freedesktop.portal.Camera              interface -                 -            -]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Exactly one line containing `org.freedesktop.portal.Camera` and the word `interface`. If `grep` returns nothing, the portal did not load its Camera interface: confirm the drop-in from step 2 is in place, that `xdg-desktop-portal-gtk` from step 1 installed, then `systemctl --user restart xdg-desktop-portal` and re-check.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Camera interface Chromium asks through is live on the session bus. The portal, the permission, and the camera source are all in place; only Chromium's own configuration remains to confirm.

<a id="6-confirm-chromium-uses-the-pipewire-camera-path"></a>
<img src="https://img.shields.io/badge/STEP_06-Confirm_Chromium_uses_the_PipeWire_camera_path-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Confirm Chromium uses the PipeWire camera path"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything on the system side is ready, but Chromium only uses the portal camera path when it is launched with the right flag. We confirm the running browser actually has it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the running Chromium process's command line and confirm the flag that selects the PipeWire camera path is present.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

[Guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service) launches Chromium with `--enable-features=UsePipeWireCamera`. That flag puts Chromium on the PipeWire camera path: instead of scanning `/dev/video*` directly (the legacy V4L2 path, which hangs while probing the Pi's many internal camera-pipeline nodes), Chromium requests a camera through the portal interface confirmed in step 5. `pgrep -a chromium` prints the full command line of each Chromium process; piping to `grep -o` isolates the flag. The command line is the authoritative truth — if the flag is not here, it is not in effect, whatever a config file says.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
pgrep -a chromium | grep -o 'enable-features=[^ ]*'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a configured unit this prints  enable-features=UsePipeWireCamera  (once per Chromium process that carries the flag).]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

At least one line reading `enable-features=UsePipeWireCamera`. If `grep` returns nothing, Chromium is running with stale arguments — `systemctl --user daemon-reload && systemctl --user restart chromium-kiosk.service` picks up [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service)'s unit. If it prints `enable-features=` followed by other names but not `UsePipeWireCamera`, the kiosk unit was not updated to the value this build uses — recheck guide 5.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Chromium is on the PipeWire camera path, the portal offers the Camera interface, access is pre-authorized, and PipeWire sees the Pi Camera. The full chain from sensor to browser is in place. When the SPA built in [guide 10](10-spa.md) enters a call and calls `navigator.mediaDevices.getUserMedia()`, Chromium receives the IMX708 through the portal and publishes it with no dialog and no delay.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`wpctl status` lists the IMX708 as a `[libcamera]` camera source, `busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop` shows `org.freedesktop.portal.Camera`, the portal permission store records the camera as `yes` for the empty application id, and the running Chromium carries `--enable-features=UsePipeWireCamera`. The camera path persists across reboots with no manual step. When the SPA from [guide 10](10-spa.md) calls `getUserMedia()`, the Pi Camera is delivered to Chromium through the portal and published into the call.
