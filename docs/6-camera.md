# Software Build Guide 06 - Camera (libcamera → PipeWire → desktop portal)

Make the Pi Camera Module 3 available to Chromium's `getUserMedia()` using Raspberry Pi OS's modern camera path: libcamera → PipeWire → the desktop portal. PipeWire and WirePlumber are already running on the base image (they are Pi OS Trixie's audio stack), so this guide adds the desktop portal that Chromium requests a camera through, points it at the labwc session, pre-authorizes camera access so the unattended kiosk never blocks on a permission dialog, and then builds the camera itself: a small always-on service captures the Pi Camera at its full field of view and publishes it into PipeWire as a single camera named `FrameLinkCam`, while WirePlumber's own camera-finding is switched off so that node is the only camera Chromium can ever see. The Chromium flag that selects this camera path (`--enable-features=UsePipeWireCamera`) was set when the kiosk service was created in [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service); this guide makes the camera that flag points at actually exist. This is the feed the app from [guide 10](10-spa.md) publishes into every call as H.264 1080p30.

---

<a id="1-install-the-camera-packages"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_the_camera_packages-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 - Install the camera packages"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A fresh Raspberry Pi OS Lite image runs PipeWire for audio, but nothing feeds the Pi Camera into it, and nothing gives Chromium the "desktop portal" it asks a camera through. Without these pieces, `getUserMedia()` finds no camera and hangs.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the two desktop-portal packages and the four GStreamer packages the dedicated camera service is built from, in a single `apt install` call.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Six packages, each doing one job:
1. `xdg-desktop-portal`: the "desktop portal" daemon. Chromium's PipeWire camera backend does not open a camera device directly; it asks the portal's `org.freedesktop.portal.Camera` interface for access and receives a PipeWire handle in return. This is the frontend half of the portal.
2. `xdg-desktop-portal-gtk`: a portal *backend*. The portal frontend only registers the Camera interface when a backend is present that implements the `Access` permission service; the GTK backend is the standard, lightweight one that provides it.
3. `gstreamer1.0-tools`: provides `gst-launch-1.0`, the runner the camera service in step 5 uses to assemble and run its capture pipeline.
4. `gstreamer1.0-plugins-base`: GStreamer's base element and library set (video format handling, caps negotiation) that the capture and publish elements below depend on.
5. `gstreamer1.0-libcamera`: provides `libcamerasrc`, the element that captures frames from the Pi Camera through libcamera.
6. `gstreamer1.0-pipewire`: provides `pipewiresink`, the element that publishes those frames into PipeWire.

Just as important is what is *not* installed: `libspa-0.2-libcamera`, the stock PipeWire camera plugin that an earlier revision of this build used here. Measured on this hardware, the camera node that plugin creates is hard-limited to roughly 30 fps, advertises no framerates at all to applications, rejects every resolution outside its own fixed menu, and Chromium fails to acquire it at anything above 1280x720, a dead end for a 1080p call. Steps 4 and 5 replace it with a dedicated camera node built from the four GStreamer packages above. (PipeWire `1.4.x` and WirePlumber `0.5.x` are already installed and running from the base image, so they are not listed here.)

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install -y xdg-desktop-portal xdg-desktop-portal-gtk gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-libcamera gstreamer1.0-pipewire
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. apt prints its dependency resolution and progress log, ending with a Setting up ... line for each of the two portal packages, the four GStreamer packages, and their pulled-in dependencies.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

A `Setting up ...` line for each of the six named packages, each completing without error. Any line containing `E:` or `dpkg: error` is fatal, because every later step depends on all six packages being present.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The portal Chromium asks a camera through is installed, and every building block of the camera service (the pipeline runner, the capture element, and the PipeWire publisher) is on the system. Nothing is wired together yet: the portal still has to be pointed at this device's session, and the camera node itself does not exist until step 5.

<a id="2-point-the-desktop-portal-at-the-labwc-session"></a>
<img src="https://img.shields.io/badge/STEP_02-Point_the_desktop_portal_at_the_labwc_session-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 - Point the desktop portal at the labwc session"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The portal is installed but offers no Camera interface yet. It decides which interfaces to expose by reading an environment variable, `XDG_CURRENT_DESKTOP`, to pick a configuration, and the kiosk starts the labwc desktop in a bare way that never sets that variable.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a tiny systemd drop-in that sets `XDG_CURRENT_DESKTOP=labwc` for the portal service, then reload and restart the portal so it picks up its configuration.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Raspberry Pi OS ships a portal configuration file at `/usr/share/xdg-desktop-portal/labwc-portals.conf`, but the portal only uses it when `XDG_CURRENT_DESKTOP` contains `labwc`. The kiosk launches the compositor with a bare `exec labwc` (from [guide 5](5-kiosk-base.md)), which does not export that variable, so the portal falls back to a degraded mode that exposes only a handful of trivial interfaces, Camera not among them.

`xdg-desktop-portal` runs as a per-user systemd service, so the cleanest fix is a service drop-in. A file under `~/.config/systemd/user/xdg-desktop-portal.service.d/` adds an `Environment=` line that systemd applies whenever it starts the portal, including the cold-boot case, where Chromium's first camera request D-Bus-activates the portal. `daemon-reload` makes systemd read the new drop-in; `restart` relaunches the portal with the variable set so it loads `labwc-portals.conf` and exposes the full interface set, Camera included.

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

`tee` echoes the file body back to the terminal, so you should see the `[Service]` and `Environment=XDG_CURRENT_DESKTOP=labwc` lines exactly as written. The two `systemctl --user` commands are silent when they succeed; a `Failed to connect to bus` error means the user session bus is not reachable from this SSH login, so log out and back in, or confirm the autologin session from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin) is active.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The portal now knows it is running under labwc and offers the Camera interface that Chromium needs. The camera is not yet authorized for unattended use; that is the next step.

<a id="3-pre-authorize-camera-access-for-the-kiosk"></a>
<img src="https://img.shields.io/badge/STEP_03-Pre--authorize_camera_access_for_the_kiosk-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 - Pre-authorize camera access for the kiosk"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The first time Chromium asks the portal for the camera, the portal pops up a "Allow app to use the camera?" window and waits for someone to click "Grant". On a wall-mounted frame with no keyboard, nobody ever clicks it, so the call freezes with a black self-view forever.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Record a permanent "yes" for the camera in the portal's permission store, once, so the portal grants access silently from then on and never shows the window.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The portal keeps per-application device permissions in a small on-disk database managed by the `org.freedesktop.impl.portal.PermissionStore` D-Bus service (the file lives at `~/.local/share/flatpak/db/devices`). When an app requests the camera, the portal looks up the app's permission: `yes` grants silently, `no` denies silently, and *unset* triggers the GTK "Allow?" dialog. We write `yes` up front so the dialog never appears.

The `busctl --user call` below invokes the store's `SetPermission` method with: table `devices`, create-if-missing `true`, id `camera`, application id `""` (the empty string, the identifier the portal uses for an unsandboxed host application like the packaged Chromium), and the permission list `yes` (the trailing `1 yes` is "a one-element list whose value is yes"). The setting is written to disk, so it persists across reboots and is run once and never again. It is idempotent: writing `yes` a second time changes nothing. The second command reads the value back as a confirmation.

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

The `SetPermission` call is silent on success. The `Lookup` line must contain `"" 1 "yes"`, the empty application id mapped to a one-element list whose single value is `yes`. If `Lookup` reports `No entry for camera`, the `SetPermission` call did not land; re-run it and confirm there was no `Failed to connect to bus` error.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Camera access is permanently granted for the kiosk. When the frame enters a call, the portal will hand Chromium the camera with no dialog and no human in the loop. The camera itself does not exist yet; building it is the next two steps.

<a id="4-route-the-camera-through-a-dedicated-pipewire-node"></a>
<img src="https://img.shields.io/badge/STEP_04-Route_the_camera_through_a_dedicated_PipeWire_node-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 - Route the camera through a dedicated PipeWire node"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

WirePlumber discovers cameras on its own, and on this Pi that works against us: the camera subsystem's raw low-level video devices surface as cameras that freeze Chromium the moment it probes them, and the limited stock camera plugin from step 1 would come straight back if a future install ever pulled it in. Chromium must only ever see one camera: the dedicated node the next step creates.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a small WirePlumber configuration file that switches off both of its built-in camera finders, then restart WirePlumber so it takes effect. Audio is not touched.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

WirePlumber reads configuration fragments from `~/.config/wireplumber/wireplumber.conf.d/` on top of its stock configuration, and the `99-` prefix sorts this fragment last so it wins. The fragment disables the two monitors of the default `main` profile that create camera nodes:
1. `monitor.libcamera`: the monitor that loads the stock `libspa-0.2-libcamera` plugin and exposes its camera node. That node is the measured dead end described in step 1 (hard ~30 fps cap, no advertised framerates, fixed size menu, unacquirable above 720p). Step 1 already leaves the plugin uninstalled; disabling the monitor guarantees the stock node stays gone even if a future package pulls the plugin back in as a dependency.
2. `monitor.v4l2`: the monitor that creates nodes for raw `/dev/video*` devices. On a Pi those are the CFE and ISP pipeline stages of the camera subsystem, not usable cameras, but surfaced into PipeWire they enumerate as cameras, and Chromium hangs while probing them. They must never appear.

The ALSA monitor, the one that provides the frame's audio devices, is not mentioned in the file, so sound is unaffected. The comment header names the service this file pairs with (`deploy/systemd/framelink-camera.service`, the repository's master copy of the camera service, from the repo you will clone in [guide 10](10-spa.md)); the next step writes that same service by hand. `tee` overwrites the file in place, and `systemctl --user restart wireplumber` makes WirePlumber reload its configuration and drop the two monitors immediately.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/wireplumber/wireplumber.conf.d
tee ~/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf << 'EOF'
# FrameLink camera routing.
# Disable WirePlumber's stock camera monitors so the only camera Chromium can see is the
# framelink-camera PipeWire node (deploy/systemd/framelink-camera.service):
#  - monitor.libcamera: the stock libspa-libcamera node is hard-limited to ~30 fps, rejects
#    non-menu sizes, and Chromium fails to acquire it above 720p (measured).
#  - monitor.v4l2: without it the raw CFE/ISP V4L2 nodes would surface as bogus cameras.
# Audio is untouched (that is the ALSA monitor).
# Install to: ~/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf
wireplumber.profiles = {
  main = {
    monitor.libcamera = disabled
    monitor.v4l2 = disabled
  }
}
EOF
systemctl --user restart wireplumber
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the fourteen lines of the configuration file back to the terminal; the wireplumber restart prints nothing on success.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`tee` echoes the file body (the eight comment lines and the `wireplumber.profiles` block) exactly as written above, and the restart is silent on success. After this step, `wpctl status` shows nothing under the Video section's `Sources:` at all: the stock camera paths are off and the dedicated node does not exist yet. That empty state is correct here and is filled by the next step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

WirePlumber no longer surfaces any camera on its own, so right now the frame deliberately has no camera at all. The next step creates the single camera Chromium will see from now on.

<a id="5-run-the-camera-node-service"></a>
<img src="https://img.shields.io/badge/STEP_05-Run_the_camera_node_service-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 - Run the camera node service"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

After the previous step the system has no camera at all. Something has to read the Pi Camera at the right size, speed, and field of view, and offer it to PipeWire as a camera, automatically, from every boot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create a small always-on service that captures the Pi Camera at 1920x1080, 30 frames per second (the setting that keeps the sensor's full field of view) and publishes it into PipeWire as a camera named `FrameLinkCam`. Then check that PipeWire lists it.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The service's `ExecStart` runs a four-element GStreamer pipeline under `gst-launch-1.0`:
1. `libcamerasrc` captures from the Pi Camera through libcamera, the same stack `rpicam-hello` uses.
2. `video/x-raw,format=NV12,width=1920,height=1080,framerate=30/1` is a demand the camera subsystem satisfies in hardware. libcamera selects the sensor mode from the requested size: asking for 1080 lines forces the IMX708's full-field-of-view 2304x1296 mode, which the Pi's ISP then scales to 1920x1080 on the fly. Any request of 900 lines or fewer would instead select a cropped sensor mode that behaves like a ~1.5x zoom, and scaling in software instead of on the ISP was measured at a ~51 fps single-thread ceiling on this CPU. This one line is why the camera shows the whole room at a steady 30 fps with no CPU cost.
3. `queue max-size-buffers=4 leaky=downstream` is a small elastic buffer: if the consuming side stalls for a moment, it drops the oldest queued frames instead of back-pressuring the camera, so the feed stays live and the pipeline never wedges.
4. `pipewiresink mode=provide` publishes the stream as a new standalone PipeWire node rather than connecting to an existing one; `sync=false` forwards frames as the sensor delivers them instead of pacing them against a playback clock; and the `stream-properties` stamp the node with `media.class=Video/Source` and `media.role=Camera` (what makes PipeWire and the portal treat it as a camera) plus the `framelink-cam` / `FrameLinkCam` name Chromium will display.

Around that pipeline, the unit does the keeping-alive: `After=pipewire.service` orders it after PipeWire inside the user session, `Restart=always` with `RestartSec=3` resurrects the pipeline three seconds after any crash, and `WantedBy=default.target` starts it with the autologin session on every boot. The commands: `mkdir -p` ensures the user-unit directory exists, `tee` writes the unit, `daemon-reload` makes systemd read it, and `enable --now` starts the service immediately and on every future boot. The final command prints just the Video section of PipeWire's device list (the `sed` expression slices from the `Video` header to the next blank line) to confirm the node registered.

One more thing about this service's lifetime, so it never surprises you later: once the button daemon from [guide 11](11-gpio-button.md) is installed, it restarts this service after **every** call. That is intentional. The `pipewiresink` element in PipeWire `1.4.x` (this OS's version) can be left permanently broken by an abrupt consumer disconnect, and the service keeps reporting `active` while the camera is dead, so each call is given a freshly started node instead. Upstream fixed the bug in PipeWire `1.6.0`; the per-call restart can be retired when the OS ships that.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/.config/systemd/user
tee ~/.config/systemd/user/framelink-camera.service << 'EOF'
[Unit]
Description=FrameLink camera node (Pi Camera -> PipeWire, full-FoV 1080p30)
After=pipewire.service

[Service]
Type=simple
ExecStart=/usr/bin/gst-launch-1.0 libcamerasrc ! video/x-raw,format=NV12,width=1920,height=1080,framerate=30/1 ! queue max-size-buffers=4 leaky=downstream ! pipewiresink sync=false mode=provide stream-properties=props,media.class=Video/Source,media.role=Camera,node.name=framelink-cam,node.description=FrameLinkCam
Restart=always
RestartSec=3

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now framelink-camera.service
wpctl status | sed -n '/^Video/,/^$/p'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. tee echoes the unit file body, enable --now prints a Created symlink line, and the wpctl slice shows the Video section with FrameLinkCam as the only entry under Sources.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

In the Video section: exactly one entry under `Sources:`, `FrameLinkCam`, and nothing camera-like anywhere else (no `imx708` device, no V4L2 entries). If `FrameLinkCam` is missing, read the service's log with `journalctl --user -u framelink-camera.service -n 20`: a pipeline that cannot find a camera means a problem upstream of PipeWire, so recheck the camera ribbon and the `camera_auto_detect=1` line in `/boot/firmware/config.txt`. If an `imx708` device or extra sources appear alongside it, the configuration from [step 4](#4-route-the-camera-through-a-dedicated-pipewire-node) did not load, so confirm the file path and restart WirePlumber again.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame now has exactly one camera: `FrameLinkCam`, full field of view, 1080p at 30 fps, published into PipeWire from boot and revived automatically if it ever dies. What remains is confirming that Chromium's route to it, the portal, is really live.

<a id="6-confirm-the-camera-portal-is-on-the-session-bus"></a>
<img src="https://img.shields.io/badge/STEP_06-Confirm_the_Camera_portal_is_on_the_session_bus-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 - Confirm the Camera portal is on the session bus"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Step 2 was supposed to make the portal offer a Camera interface. Chromium will hang on a black self-view if that interface is missing, so we confirm it is really there before relying on it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the portal on the session bus to list its interfaces and confirm `Camera` is among them.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`busctl --user introspect` lists every interface a D-Bus object publishes. The desktop portal lives at the bus name `org.freedesktop.portal.Desktop` on the object path `/org/freedesktop/portal/desktop`. When the portal is correctly configured (step 2) and a backend providing the permission service is installed (the GTK backend from step 1), it publishes `org.freedesktop.portal.Camera` here. `grep -i camera` filters the long interface list to the one line that matters. If step 2 had failed, this command would print nothing, which is exactly the failure that makes `getUserMedia()` hang.

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

<a id="7-confirm-chromium-uses-the-pipewire-camera-path"></a>
<img src="https://img.shields.io/badge/STEP_07-Confirm_Chromium_uses_the_PipeWire_camera_path-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 - Confirm Chromium uses the PipeWire camera path"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything on the system side is ready, but Chromium only uses the portal camera path when it is launched with the right flag. We confirm the running browser actually has it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the running Chromium process's command line and confirm the flag that selects the PipeWire camera path is present.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

[Guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service) launches Chromium with `--enable-features=UsePipeWireCamera`. That flag puts Chromium on the PipeWire camera path: instead of scanning `/dev/video*` directly (the legacy V4L2 path, which hangs while probing the Pi's many internal camera-pipeline nodes), Chromium requests a camera through the portal interface confirmed in step 6. `pgrep -a chromium` prints the full command line of each Chromium process; piping to `grep -o` isolates the flag. The command line is the authoritative truth: if the flag is not here, it is not in effect, whatever a config file says.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
pgrep -a chromium | grep -o 'enable-features=[^ ]*'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. On a configured unit this prints  enable-features=UsePipeWireCamera  (once per Chromium process that carries the flag).]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

At least one line reading `enable-features=UsePipeWireCamera`. If `grep` returns nothing, Chromium is running with stale arguments, and `systemctl --user daemon-reload && systemctl --user restart chromium-kiosk.service` picks up [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service)'s unit. If it prints `enable-features=` followed by other names but not `UsePipeWireCamera`, the kiosk unit was not updated to the value this build uses, so recheck guide 5.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Chromium is on the PipeWire camera path, the portal offers the Camera interface, access is pre-authorized, and `FrameLinkCam` is the one camera PipeWire offers. The full chain from sensor to browser is in place. When the SPA built in [guide 10](10-spa.md) enters a call and calls `navigator.mediaDevices.getUserMedia()`, Chromium receives the full-field-of-view `FrameLinkCam` feed through the portal, with no dialog and no delay, and the app publishes it into the call as H.264 1080p30.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`wpctl status` lists `FrameLinkCam` as the only entry under the Video section's Sources, `busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop` shows `org.freedesktop.portal.Camera`, the portal permission store records the camera as `yes` for the empty application id, and the running Chromium carries `--enable-features=UsePipeWireCamera`. The camera service, the WirePlumber routing, and the portal all persist across reboots with no manual step. When the SPA from [guide 10](10-spa.md) calls `getUserMedia()`, the frame's one camera, the full field of view of the Pi Camera, is delivered to Chromium through the portal and published into the call as H.264 1080p30.
