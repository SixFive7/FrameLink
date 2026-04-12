# Software Build Guide 04 — Audio Configuration

Configure the ReSpeaker XVF3800 USB mic array and its attached speaker so that audio capture and playback work reliably end-to-end: pin the USB device to a stable ALSA card index so users can add or remove other USB devices without breaking routing, enable the on-board speaker amp, and tune the acoustic echo cancellation (AEC) delay and mixer levels to match the enclosure the Pi is built into.

---

## Steps

1. **Pin the XVF3800 to a stable ALSA card index.** On a fresh install `snd-usb-audio` hands out card indices in enumeration order, which means the ReSpeaker's card number can shift the moment any other USB audio device (a headset, another mic, a USB-C dock that happens to expose an audio endpoint) joins the bus. Everything downstream — `aplay -D hw:0,0`, `alsactl store`, the XVF3800 control tool's HID lookup — assumes the array is card 0, so we pin it explicitly via the `snd-usb-audio` module's `index=` / `vid=` / `pid=` options.

    The options line is appended to `/etc/modprobe.d/alsa-base.conf` (a file Raspberry Pi OS does not create by default). The `grep -qxF` guard keeps the append idempotent; `2>/dev/null` swallows grep's "No such file" on the first run when the file has yet to exist. The reboot is required because `snd-usb-audio` only honours the options when the module loads, and it loads very early in boot. The vid/pid `2886:001a` matches the retail Seeed ReSpeaker XVF3800 as shipped today; a future hardware revision under a different PID would need this line updated.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    grep -qxF 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' /etc/modprobe.d/alsa-base.conf 2>/dev/null || echo 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' | sudo tee -a /etc/modprobe.d/alsa-base.conf
    sudo reboot
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
    options snd-usb-audio index=0 vid=0x2886 pid=0x001a
    Connection to framelink-douwe.local closed by remote host.
    ```

2. **Install and verify the XVF3800 host control tool.** The XVF3800 exposes two things over the same USB cable: the audio interface that ALSA sees as card 0, and a separate USB HID control interface that speaks an XMOS command/response protocol for configuring the DSP — AEC parameters, mic/reference gains, GPIO, LED ring, and device management commands like `VERSION` / `SAVE_CONFIGURATION`. Without a host-side tool that speaks this protocol we cannot enable the speaker amplifier (step 3), tune AEC for the enclosure, or read back DSP state for diagnostics. ALSA's mixer does not reach into the DSP-side parameters.

    Seeed distribute a pre-built aarch64 `xvf_host` binary and its supporting `.so` files alongside the firmware releases in [respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY). We use that binary directly: no compiler toolchain, no `pip` install, no XMOS SDK. The install is a shallow `git clone` plus a `chmod +x`, both trivially idempotent. The binary loads its three `.so` files relative to its own directory, so every invocation has to `cd` into that directory (or we wrap it in a subshell, which is what the smoke-test below does so that the surrounding shell's `$PWD` is left untouched). `sudo` is required because the HID device node is root-owned and Seeed do not ship a udev rule for it.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    sudo apt-get update
    sudo apt-get install -y git
    [ -d ~/xvf3800/.git ] || git clone --depth 1 https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY.git ~/xvf3800
    chmod +x ~/xvf3800/host_control/rpi_64bit/xvf_host
    (cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host VERSION)
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
    Hit:1 http://deb.debian.org/debian trixie InRelease
    Hit:2 http://deb.debian.org/debian trixie-updates InRelease
    Hit:3 http://deb.debian.org/debian-security trixie-security InRelease
    Hit:4 http://archive.raspberrypi.com/debian trixie InRelease
    Reading package lists...
    Reading package lists...
    Building dependency tree...
    Reading state information...
    The following package was automatically installed and is no longer required:
      retry
    Use 'sudo apt autoremove' to remove it.
    The following additional packages will be installed:
      git-man liberror-perl
    Suggested packages:
      git-doc git-email git-gui gitk gitweb git-cvs git-mediawiki git-svn
    The following NEW packages will be installed:
      git git-man liberror-perl
    0 upgraded, 3 newly installed, 0 to remove and 0 not upgraded.
    Need to get 10.9 MB of archives.
    After this operation, 53.1 MB of additional disk space will be used.
    ...
    Setting up liberror-perl (0.17030-1) ...
    Setting up git-man (1:2.47.3-0+deb13u1) ...
    Setting up git (1:2.47.3-0+deb13u1) ...
    Processing triggers for man-db (2.13.1-1) ...
    Cloning into '/home/framelink/xvf3800'...
    Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3
    VERSION 2 0 6
    ```

    The last two lines are the smoke test. `VID: 10374` is `0x2886` in decimal and `PID: 26` is `0x001A` — confirmation that `xvf_host` found the right USB device and opened HID interface 3 (the control interface). `VERSION 2 0 6` is the Seeed firmware version reported by the device. FrameLink does not reflash firmware as a build step; the retail firmware is used as-shipped and newer releases (currently up to v2.0.7) only adjust LED and DAC-volume behaviour that does not affect this kiosk use case. If the smoke test instead produces `device_init() -- No device found` or similar, unplug and re-seat the XVF3800's USB cable and re-run — the HID interface occasionally needs a fresh enumeration after the reboot from step 1.

3. **Enable the speaker amplifier and confirm the playback path works.** The XVF3800 exposes five addressable GPO pins; the one that controls the speaker amplifier is `X0D31`, and it is active-low (low = amp enabled). Per Seeed's [host_control/README.md](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/blob/master/host_control/README.md#gpio-control), `GPO_READ_VALUES` returns five values in the fixed order `X0D11, X0D30, X0D31, X0D33, X0D39`, and `GPO_WRITE_VALUE` addresses the same five pins by their XMOS port number.

    Firmware v2.0.6 (the retail shipping version) **already boots with `X0D31` low**, so the amp is effectively enabled out of the box — the `GPO_WRITE_VALUE 31 0` below is a belt-and-braces idempotent no-op against any future firmware that might ship with a different default. A class-D amp with no signal produces an audible hiss; this is the amp's noise floor, it starts at boot, and it is normal.

    The smoke test plays one of the stock `alsa-utils` voice samples (`Front_Left.wav` — a short "Front Left" spoken word). Because the Adafruit 3351 mono speaker is a low-sensitivity driver and the on-board amp only delivers a few watts, ALSA's `PCM` playback volume has to be turned up to the top of its range (`60/60` = 0 dB) for the sample to be clearly audible. This is the loudness ceiling of the current hardware combination; see [the project TODO](../TODO.md) for the note on adding an external amp if a noisier deployment environment ever needs it. The mono speaker only reproduces the left channel — the right channel on the TRS jack is either unused (TS plug) or summed into the single driver.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    (cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_READ_VALUES)
    (cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_WRITE_VALUE 31 0)
    amixer -c 0 sset PCM 60
    aplay -D plughw:0,0 /usr/share/sounds/alsa/Front_Left.wav
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
    Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3
    GPO_READ_VALUES 0 0 0 1 0
    Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3
    Simple mixer control 'PCM',0
      Capabilities: pvolume pswitch
      Playback channels: Front Left - Front Right
      Limits: Playback 0 - 60
      Mono:
      Front Left: Playback 60 [100%] [0.00dB] [on]
      Front Right: Playback 60 [100%] [0.00dB] [on]
    Playing WAVE '/usr/share/sounds/alsa/Front_Left.wav' : Signed 16 bit Little Endian, Rate 48000 Hz, Mono
    ```

    The `0 0 0 1 0` readback means `X0D11=0, X0D30=0, X0D31=0, X0D33=1, X0D39=0` — the third value is `X0D31`, already low, confirming the amp is enabled. `X0D33=1` is the LED-ring power rail (active-high, so `1` means the ring is powered — you should see the LED ring cycling its default rainbow → DoA pattern). `GPO_WRITE_VALUE 31 0` produces only the `device_init()` banner because the command itself is a write with no return payload. During the `aplay` you should hear the words "Front Left" spoken clearly through the speaker, over the amp's steady hiss.

    If you hear no voice at all — only hiss — check the 3.5 mm plug is fully seated, and verify `X0D31` is still `0` by re-running `GPO_READ_VALUES`. If the voice is present but very faint even at `PCM 60`, the speaker-plus-amp combination is at its ceiling for this hardware; see the TODO note referenced above.

4. **Persist the ALSA mixer state across reboots.** The `alsa-utils` package that ships with Raspberry Pi OS Lite Trixie includes `alsa-restore.service`, a static systemd unit that runs `alsactl restore` early in boot, reading the saved mixer values from `/var/lib/alsa/asound.state` and applying them to every sound card the system sees. Nothing needs to be enabled, installed, or written — it is already pulled in by the sound subsystem and runs automatically. All that is required is to capture the current in-memory mixer state to disk so that the service has something to restore on the next boot.

    `sudo alsactl store` rewrites `/var/lib/alsa/asound.state` every time it runs; there is no "if-changed" guard needed because the file is itself the desired state. Running it twice is indistinguishable from running it once.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    sudo alsactl store
    sudo reboot
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
    Connection to framelink-douwe.local closed by remote host.
    ```

    `alsactl store` writes silently on success; the only visible output from the block is the SSH client reporting the disconnect caused by the reboot. After the Pi comes back up, reconnect and confirm the mixer is still at `60/60` and the restore service ran during boot:

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    amixer -c 0 sget PCM | grep 'Front Left'
    systemctl status alsa-restore.service --no-pager | head -8
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
      Front Left: Playback 60 [100%] [0.00dB] [on]
    ● alsa-restore.service - Save/Restore Sound Card State
         Loaded: loaded (/usr/lib/systemd/system/alsa-restore.service; static)
         Active: active (exited) since Sun 2026-04-12 20:10:51 CEST; 11s ago
     Invocation: b4083997199f4b8ebe8b3abd46088708
           Docs: man:alsactl(1)
       Main PID: 722 (code=exited, status=0/SUCCESS)
            CPU: 14ms
    Apr 12 20:10:51 framelink-douwe systemd[1]: Starting alsa-restore.service - Save/Restore Sound Card State...
    ```

    The `Playback 60 [100%] [0.00dB]` line is PCM coming back up at the stored level. The `alsa-restore.service` block shows the unit ran once at boot and exited cleanly. (The service also emits a couple of benign `failed to import hw:1 use case configuration` lines for the HDMI cards, which ship without UCM profiles — not visible in the `head -8` slice above and safe to ignore.)

5. **Validate mic capture with a round-trip recording.** The microphone side has had no test yet — step 3 exercised the playback path only. The simplest honest validation is to record a few seconds with `arecord`, then play the captured file back with `aplay` and confirm the voice is intelligible. Success here means the USB capture endpoint, the XVF3800's mic array and AEC processing, and ALSA's card-0 routing all line up correctly.

    The XVF3800 emits two capture channels: the left channel carries the AEC-processed, beamformed, auto-selected voice output (what you actually want for a voice call), and the right channel carries the ASR-ready output (intended for downstream speech recognition). Recording at 48 kHz / 16-bit / stereo matches the device's native format — no resampling, no surprises. A 3-second stereo capture produces a 576,044-byte WAV: `3 s × 48000 Hz × 2 channels × 2 bytes + 44-byte header`.

    Speak at normal conversational volume from roughly 30 cm away from the array during the `arecord` window. The recording starts the moment `arecord` prints its `Recording WAVE` line.

    ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

    ```bash
    arecord -D plughw:0,0 -c 2 -f S16_LE -r 48000 -d 3 /tmp/mic_test.wav
    ls -l /tmp/mic_test.wav
    aplay -D plughw:0,0 /tmp/mic_test.wav
    rm /tmp/mic_test.wav
    ```

    ![OUTPUT](https://img.shields.io/badge/🍓-OUTPUT-success?style=flat-square)

    ```text
    Recording WAVE '/tmp/mic_test.wav' : Signed 16 bit Little Endian, Rate 48000 Hz, Stereo
    -rw-r--r-- 1 framelink framelink 576044 Apr 12 20:14 /tmp/mic_test.wav
    Playing WAVE '/tmp/mic_test.wav' : Signed 16 bit Little Endian, Rate 48000 Hz, Stereo
    ```

    The playback should reproduce your own voice clearly through the speaker. Because the mono speaker only reproduces the left channel (see step 3), you are hearing the AEC-processed beamformed output rather than the ASR-ready right channel — which is the channel you would actually feed into a video call anyway.

    If the file is exactly 576,044 bytes but plays back as silence, the USB mic endpoint opened but no samples were captured — check that the ReSpeaker's hardware mute button has not been pressed (it toggles `X0D30`; confirm via `GPO_READ_VALUES` that the second value is `0`, not `1`). If `arecord` itself errors with "Device or resource busy", something else already opened card 0's capture endpoint — typical culprit is a stale `arecord` from a previous aborted run, found via `sudo fuser -v /dev/snd/*` and killed.

**Checkpoint:** after this guide, `aplay -l` and `arecord -l` both show the XVF3800 as card 0 on every boot; a short playback through the speaker is audibly clear; and a short capture from the mic is audibly clear.
