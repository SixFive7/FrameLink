# Software Build Guide 06 — Camera Bridge (v4l2loopback)

Bridge the Pi Camera Module 3 (libcamera) to a standard V4L2 device so Chromium's `getUserMedia()` can see it. Install GStreamer with the libcamera + v4l2 plugins and the `v4l2loopback` kernel module, make the module load persistently, install a udev rule so the loopback device is readable by the `framelink` user, run a GStreamer pipeline at the camera's full-FoV native binned mode (2304×1296 @ 56 fps, YUY2), and verify Chromium is configured to enumerate the resulting `/dev/video8` as "Chromium device". The pipeline you run by hand in step 5 is a smoke test; a systemd user service that keeps it running automatically comes in [guide 11](11-systemd-and-reliability.md).

---

<a id="1-install-the-camera-bridge-packages"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_the_camera_bridge_packages-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install the camera bridge packages"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A fresh Raspberry Pi OS Lite image does not include the software needed to move frames from the Pi Camera into a form that Chromium can open as a webcam. Without these packages, none of the later steps in this guide can run.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the five packages the bridge needs, in a single `apt install` call.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Five packages, each doing one job:
1. `gstreamer1.0-tools` — the `gst-launch-1.0` CLI used in step 5 to run a pipeline by hand for testing.
2. `gstreamer1.0-plugins-base` — the core pipeline elements GStreamer relies on everywhere.
3. `gstreamer1.0-plugins-good` — the package that contains the `v4l2sink` element used to write frames into a `/dev/video*` device. Without this, step 5's pipeline fails with `no element "v4l2sink"`.
4. `gstreamer1.0-libcamera` — the `libcamerasrc` element, which bridges Raspberry Pi's libcamera stack into GStreamer.
5. `v4l2loopback-dkms` — the kernel module that creates a virtual V4L2 capture device at `/dev/video8` (in our configuration). Because it is a DKMS package, it builds itself against every installed Pi 5 kernel variant at install time.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libcamera v4l2loopback-dkms -y
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Not captured from a single fresh-flash first-run this session; the installation was split across two `apt install` calls during guide authoring. Will be recaptured and inserted from a clean first-run before the guide ships.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

In the authoritative capture: all five requested packages listed under `Installing:`, a `Setting up ...` line for each, and multiple `Building module(s)... done.` blocks from DKMS (one per installed Pi 5 kernel variant — typically four, covering `6.12.Y+rpt-rpi-2712` and `6.12.Y+rpt-rpi-v8` for two kernel versions). Any line containing `ERROR` or `Failed` during the DKMS build is fatal — step 2's `modprobe` will fail, and `dmesg | grep v4l2loopback` after the install will show the specific build error.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The GStreamer command-line tooling is installed, libcamera is wired into GStreamer via `libcamerasrc`, and the `v4l2loopback` kernel module has been built against every installed Pi 5 kernel. The module is not loaded yet.

<a id="2-load-the-v4l2loopback-kernel-module"></a>
<img src="https://img.shields.io/badge/STEP_02-Load_the_v4l2loopback_kernel_module-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Load the v4l2loopback kernel module"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The module is on disk but inert. Until something tells the kernel to load it, there is no virtual camera device for a later step to write into or for Chromium to read from.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Load the module now with the options we want, and confirm it is live with `lsmod`.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Four options on the `modprobe` command, each load-bearing:
1. `video_nr=8` pins the device to `/dev/video8`. Every later config (the GStreamer pipeline, the udev rule, the SPA that reads the device) references this fixed number, so it must be stable.
2. `card_label="Chromium device"` is the V4L2 "card" name userspace tools and Chromium's camera picker will show for this device. It is also what our udev rule in step 4 will match on.
3. `exclusive_caps=1` is specifically needed for Chromium. Without it, v4l2loopback advertises both CAPTURE and OUTPUT capabilities on the same device node, and Chromium silently refuses to enumerate such devices as cameras. With it, the device starts as OUTPUT-only (for a writer to connect to) and then flips to CAPTURE-only once a writer is attached, which is the shape Chromium accepts.
4. `max_buffers=2` counters a known Pi OS / Trixie failure mode where v4l2loopback's default buffer count causes a single-writer / single-reader pipeline to "freeze after one frame" under Chromium.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo modprobe v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1 max_buffers=2
lsmod | grep v4l2loopback
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
v4l2loopback           65536  0
videodev              344064  10 v4l2_async,v4l2_fwnode,pisp_be,rpi_hevc_dec,imx708,videobuf2_v4l2,v4l2loopback,rp1_cfe,dw9807_vcm,v4l2_mem2mem
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Line 1 confirms `v4l2loopback` is loaded; the final column (the "used by" count) is `0` — the module is live but nothing has opened the device yet, which is the correct starting state. Line 2 shows `videodev` (the shared V4L2 core) now lists `v4l2loopback` in its dependents, alongside the real camera drivers (`imx708`, `pisp_be`, `rp1_cfe`, …). If line 1 is missing, `modprobe` failed — `dmesg | tail` will show the DKMS build error or the symbol-resolution failure.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The virtual V4L2 device `/dev/video8` now exists in the kernel. It is not yet readable by the `framelink` user (that comes in step 4), and nothing is feeding frames into it yet.

<a id="3-persist-the-module-config-across-reboots"></a>
<img src="https://img.shields.io/badge/STEP_03-Persist_the_module_config_across_reboots-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Persist the module config across reboots"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The `modprobe` from step 2 is live-only. After the next power cycle the module will not auto-load, and even if it did, our custom options would be lost. Every reboot would leave the Pi without a camera bridge.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write two small config files: one that tells systemd to load the module at boot, one that tells `modprobe` which options to pass.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

1. `/etc/modules-load.d/v4l2loopback.conf` is read at boot by `systemd-modules-load.service`. Any module names listed here get `modprobe`'d early in the boot sequence.
2. `/etc/modprobe.d/v4l2loopback.conf` is read by `modprobe` every time it (re)loads the module — whether called by systemd, by a user running `modprobe v4l2loopback` with no arguments, or by the udev reload in step 4. The single `options` line here supplies the same four parameters we passed explicitly in step 2.

Combined, these two files mean that from now on, `sudo modprobe v4l2loopback` with no arguments is equivalent to the full command we ran in step 2, and the module will already be loaded on every fresh boot before any user process runs.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
echo 'v4l2loopback' | sudo tee /etc/modules-load.d/v4l2loopback.conf
sudo tee /etc/modprobe.d/v4l2loopback.conf << 'EOF'
options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1 max_buffers=2
EOF
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
v4l2loopback
options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1 max_buffers=2
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`tee` echoes each write to stdout, so the two echoed lines should match exactly the content you wrote — byte-for-byte. If you see a shell error instead of the `options ...` line, the heredoc is malformed; if the echoed `options` line has any value different from step 2's modprobe arguments, the next boot will load the module with the wrong settings.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The v4l2loopback module will auto-load on every boot with exactly the options we chose. The Pi no longer needs a manual `modprobe` after a power cycle.

<a id="4-grant-the-framelink-user-access-via-a-udev-rule"></a>
<img src="https://img.shields.io/badge/STEP_04-Grant_the_framelink_user_access_via_a_udev_rule-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Grant the framelink user access via a udev rule"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The `/dev/video8` the kernel creates is owned by `root:root` with mode `0600` — only root can open it. Chromium runs as the `framelink` user. Without a fix, `getUserMedia()` would fail silently: the camera device exists, but the process that wants to open it cannot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write a small udev rule that sets `/dev/video8`'s group to `video` (which `framelink` already belongs to) and its permissions to `0660`, then reload udev and re-create the device so the rule applies cleanly.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The upstream `v4l2loopback-dkms` package ships no udev rules of its own. On Pi OS, every real V4L2 device (`/dev/video0` through `/dev/video35`) gets `crw-rw----+ root video` because the default udev rules match the `video4linux` subsystem with certain attributes — but v4l2loopback's virtual devices are not matched by those rules. Our rule targets them specifically:

- `SUBSYSTEM=="video4linux"` scopes the rule to V4L2 devices only.
- `ATTR{name}=="Chromium device"` matches the `card_label` we set in step 2, which is exposed through sysfs at `/sys/class/video4linux/video8/name`. Matching by label (rather than `KERNEL=="video8"`) keeps the rule correct even if we ever change `video_nr`.
- `GROUP="video"` and `MODE="0660"` together mirror the permission pattern of real camera devices.

After writing the rule, `udevadm control --reload` tells the udev daemon to re-read its ruleset. The rule will not retroactively change the already-created `/dev/video8` — it only runs on device events — so we unload and reload the module to fire a fresh "add" event, which udev then processes with the new rule in force. The final `ls` confirms the result.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo tee /etc/udev/rules.d/99-v4l2loopback.rules << 'EOF'
SUBSYSTEM=="video4linux", ATTR{name}=="Chromium device", GROUP="video", MODE="0660"
EOF
sudo udevadm control --reload
sudo modprobe -r v4l2loopback
sudo modprobe v4l2loopback
ls -la /dev/video8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
SUBSYSTEM=="video4linux", ATTR{name}=="Chromium device", GROUP="video", MODE="0660"
crw-rw----+ 1 root video 81, 29 Apr 13 15:24 /dev/video8
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `ls` line must show `crw-rw----+` (not `crw-------`), group `video` (not `root`), and end with `/dev/video8`. The trailing `+` indicates systemd-logind has granted an additional ACL to the active-seat user (`framelink`, autologged-in on tty1 from [guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin)) via the `uaccess` udev tag — extra belt-and-braces on top of the `video` group permission. If you see `crw------- 1 root root` instead, the rule did not apply: confirm `udevadm control --reload` produced no error and that the module was genuinely reloaded (a `modprobe v4l2loopback` with the module already loaded is a no-op and does not re-create the device).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

`/dev/video8` now has the same permission pattern as real camera devices on the Pi. The `framelink` user — and therefore Chromium — can open it.

<a id="5-smoke-test-the-gstreamer-pipeline"></a>
<img src="https://img.shields.io/badge/STEP_05-Smoke--test_the_GStreamer_pipeline-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Smoke-test the GStreamer pipeline"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

So far `/dev/video8` is visible and has the right permissions, but nothing is writing frames to it. A reader like Chromium that opened it right now would get an empty stream. Before moving on we need to confirm the producer side — Pi Camera → libcamera → GStreamer → loopback — actually works.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Run a GStreamer pipeline that captures from the Pi Camera at its full field-of-view binned native mode and writes the frames straight into `/dev/video8`. Leave it running long enough to see the startup messages, then Ctrl-C.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The pipeline has three elements chained together with GStreamer's `!` operator:

1. `libcamerasrc` captures from the Pi Camera. We pin it to the IMX708's full-FoV 2×2-binned native mode (2304×1296 at 56 fps) and pixel format `YUY2`. Without an explicit `format=`, `libcamerasrc` auto-negotiates NV12, which Chromium cannot render from a loopback device (symptom: a grey box). `YUY2` is Chromium's preferred V4L2 capture format from a loopback.
2. `queue max-size-buffers=2 leaky=downstream` is a two-frame buffer in front of the sink. The `leaky=downstream` flag means that if the downstream sink falls behind, the queue drops the oldest frame instead of blocking upstream. Without it, a momentary Chromium stall would propagate back to `libcamerasrc` as a negotiation-deadline warning and tear the pipeline down; with it, the bridge survives brief consumer pauses by dropping stale frames.
3. `v4l2sink device=/dev/video8` writes each frame into the loopback device.

No `videoconvert` element is needed because `libcamerasrc` emits `YUY2` natively on Pi 5 when asked, so source and sink formats already match. Because the Camera Module 3 is natively landscape (16:9) and the display is rotated to landscape too (see [guide 3](3-hardware-configuration.md) and [guide 5 step 6](5-kiosk-base.md#6-create-the-labwc-autostart-rotate-the-dsi-output-then-start-the-chromium-service)), no `videoflip` rotation filter is needed either. If you later mount the camera module rotated 90° or 180° (for example, ribbon exiting the top of the case instead of the bottom), insert `videoflip method=clockwise` (or `rotate-180` / `counterclockwise`) between `queue` and `v4l2sink`. The pipeline runs in the foreground until you press Ctrl-C. A persistent systemd user service that runs this same pipeline automatically comes in [guide 11](11-systemd-and-reliability.md).

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
gst-launch-1.0 libcamerasrc ! 'video/x-raw,width=2304,height=1296,framerate=56/1,format=YUY2' ! queue max-size-buffers=2 leaky=downstream ! v4l2sink device=/dev/video8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Setting pipeline to PAUSED ...
[0:54:34.974426807] [2126]  INFO Camera camera_manager.cpp:340 libcamera v0.7.0+rpt20260205
[0:54:34.983362064] [2131]  INFO RPI pisp.cpp:720 libpisp version v1.4.0 23-03-2026 (13:29:05)
[0:54:35.098577645] [2131]  INFO IPAProxy ipa_proxy.cpp:180 Using tuning file /usr/share/libcamera/ipa/rpi/pisp/imx708.json
[0:54:35.105819039] [2131]  INFO Camera camera_manager.cpp:223 Adding camera '/base/axi/pcie@1000120000/rp1/i2c@88000/imx708@1a' for pipeline handler rpi/pisp
[0:54:35.105843003] [2131]  INFO RPI pisp.cpp:1181 Registered camera /base/axi/pcie@1000120000/rp1/i2c@88000/imx708@1a to CFE device /dev/media0 and ISP device /dev/media2 using PiSP variant BCM2712_D0
Pipeline is live and does not need PREROLL ...
Pipeline is PREROLLED ...
Setting pipeline to PLAYING ...
New clock: GstSystemClock
INFO:
../src/gstreamer/gstlibcamerasrc.cpp(614): gst_libcamera_src_negotiate (): /GstPipeline:pipeline0/GstLibcameraSrc:libcamerasrc0:
CameraConfiguration::validate() returned CameraConfiguration::Adjusted
[0:54:35.110945631] [2134]  INFO Camera camera.cpp:1215 configuring streams: (0) 2304x1296-YUYV/sYCC
[0:54:35.111064838] [2131]  INFO RPI pisp.cpp:1485 Sensor: /base/axi/pcie@1000120000/rp1/i2c@88000/imx708@1a - Selected sensor format: 2304x1296-SBGGR10_1X10/RAW - Selected CFE format: 2304x1296-PC1B/RAW
handling interrupt.
Interrupt: Stopping pipeline ...
Execution ended after 0:00:04.579065964
Setting pipeline to NULL ...
Freeing pipeline ...
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Three lines matter. (1) `Pipeline is PREROLLED` followed by `Setting pipeline to PLAYING` — element negotiation succeeded and the pipeline is running. (2) `configuring streams: (0) 2304x1296-YUYV/sYCC` — libcamera accepted exactly the caps we requested. (3) `Selected sensor format: 2304x1296-SBGGR10_1X10/RAW` — PiSP chose the IMX708's full-FoV binned mode (the third entry returned by `rpicam-hello --list-cameras`). The single `CameraConfiguration::validate() returned CameraConfiguration::Adjusted` INFO line in the middle is expected and harmless — it is `libcamerasrc` noting that it reconciled GStreamer caps to sensor-native capabilities. Error signatures to watch for instead: `WARNING: erroneous pipeline: no element "X"` (a package from step 1 is missing), `could not be negotiated` (the caps filter asks for something the camera cannot deliver), or `Internal data stream error` (normally a downstream writer stall — the `queue leaky=downstream` defends against this, but a broken `v4l2sink` or a missing `/dev/video8` can still trigger it).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The camera-to-virtual-device bridge works end-to-end: frames flow from the Camera Module 3 sensor, through PiSP, through libcamera, through GStreamer, and into `/dev/video8`. While this pipeline is running, any V4L2 capture client that opens `/dev/video8` receives live frames at 2304×1296 YUY2 at 56 fps. You are still running the pipeline by hand; the persistent user service for it is set up in [guide 11](11-systemd-and-reliability.md).

<a id="6-confirm-the-device-permissions"></a>
<img src="https://img.shields.io/badge/STEP_06-Confirm_the_device_permissions-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Confirm the device permissions"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Before we hand the bridge off to Chromium, we want a final sanity check that `/dev/video8` is in the correct steady state after the udev rule, the module reload, and a full pipeline run.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

`ls -la` the device and verify the permission bits have not drifted.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Run this in a second SSH session (if the pipeline from step 5 is still running) or after Ctrl-C'ing it. The device file persists as long as the `v4l2loopback` module is loaded; its ownership and mode are set by our udev rule at device-add time and do not change when a producer (GStreamer) connects or disconnects.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
ls -la /dev/video8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
crw-rw----+ 1 root video 81, 29 Apr 13 15:24 /dev/video8
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Same three-part check as the final line of step 4: mode `crw-rw----+`, owner `root`, group `video`. If any of these have changed after the step 5 pipeline run, something outside this guide is modifying the device — highly unexpected and worth investigating before proceeding.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

`/dev/video8` is stable in the configuration Chromium needs. The bridge is ready for a reader.

<a id="7-confirm-chromium-is-enumerating-v4l2-devices"></a>
<img src="https://img.shields.io/badge/STEP_07-Confirm_Chromium_is_enumerating_V4L2_devices-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Confirm Chromium is enumerating V4L2 devices"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

We have set up the producer side (bridge), the permission side (udev rule), and the device steady state. The last open question is whether the consumer — Chromium, configured in [guide 5](5-kiosk-base.md) — is actually looking at `/dev/video*` at all. Pi OS Trixie's Chromium build defaults to the PipeWire camera backend, which would ignore v4l2loopback devices entirely and leave `getUserMedia()` with no camera to return.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the running Chromium process's command line and confirm the flag that disables the PipeWire camera path is present.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

[Guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service) wires `--disable-features=UsePipeWireCamera` into Chromium's launch command. That flag toggles Chromium off the PipeWire camera enumeration path (which only sees PipeWire camera nodes) and back onto the V4L2 enumeration path (which sees every `/dev/video*` device the process can open — including our `/dev/video8`). `pgrep -a chromium` prints the PID and full command line of each matching process, and `head -1` picks the main Chromium browser process. The command line you read here is the authoritative truth: if the flag is not in this line, it is not in effect, no matter what any config file says.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
pgrep -a chromium | head -1
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
1655 /usr/lib/chromium/chromium --js-flags=--no-decommit-pooled-pages --force-renderer-accessibility --enable-remote-extensions --show-component-extension-options --enable-gpu-rasterization --no-default-browser-check --disable-pings --media-router=0 --disable-dev-shm-usage --enable-remote-extensions --load-extension --use-angle=gles --ozone-platform=wayland --user-data-dir=/tmp/framelink-chromium --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --no-first-run --auto-accept-camera-and-microphone-capture --disable-features=UsePipeWireCamera --autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding https://webrtc.github.io/samples/
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`--disable-features=UsePipeWireCamera` must appear somewhere in the command line. Scroll right in the output to find it — on a fresh boot it sits between `--auto-accept-camera-and-microphone-capture` and `--autoplay-policy=no-user-gesture-required`. If the flag is absent, the Chromium service is running with stale arguments — `systemctl --user daemon-reload && systemctl --user restart chromium-kiosk.service` picks up [guide 5 step 5](5-kiosk-base.md#5-create-the-chromium-systemd-user-service)'s updated unit. The PID (`1655` in the captured line) will vary on your unit; it is meaningful only as proof that `pgrep` matched a real process.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Chromium is running with V4L2 camera enumeration active. When the SPA (built in [guide 9](9-spa.md)) later calls `navigator.mediaDevices.getUserMedia()`, Chromium will enumerate `/dev/video8` and "Chromium device" will appear in the camera-selection list.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`/dev/video8` exists with `root:video 0660+` permissions, the GStreamer pipeline runs at 2304×1296 YUY2 from the IMX708's full-FoV binned native mode without errors, the v4l2loopback module auto-loads with the correct options on every boot, and the running Chromium process has `--disable-features=UsePipeWireCamera` in its command line so it enumerates V4L2 devices. When the SPA built in [guide 9](9-spa.md) calls `navigator.mediaDevices.getUserMedia()`, "Chromium device" will appear in the camera list and deliver frames from the Pi Camera.
