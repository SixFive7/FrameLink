# Software Build Guide 04 — Audio Configuration

Configure the ReSpeaker XVF3800 USB mic array and its attached speaker so that audio capture and playback work reliably end-to-end: pin the USB device to a stable ALSA card index so adding or removing other USB devices can never break audio routing, install Seeed's host-side tool that talks to the array's on-board DSP, enable the speaker amplifier, raise the playback mixer to the level this speaker needs and persist it across reboots, and prove both directions with a spoken-word playback test and a round-trip microphone recording.

---

<a id="1-pin-the-xvf3800-to-a-stable-alsa-card-index"></a>
<img src="https://img.shields.io/badge/STEP_01-Pin_the_XVF3800_to_a_stable_ALSA_card_index-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Pin the XVF3800 to a stable ALSA card index"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Every boot, Linux numbers the sound cards in whatever order it happens to find them. If any other USB device with an audio function ever joins the bus — a headset, another mic, a USB-C dock that happens to expose an audio endpoint — the ReSpeaker's card number can shift, and everything in this guide and beyond assumes it is card 0.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tell the USB sound driver, once and permanently, that the device with the ReSpeaker's unique USB identity is always card 0, then reboot so the rule applies from the earliest moment of boot.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

On a fresh install `snd-usb-audio` hands out card indices in enumeration order. Everything downstream — `aplay -D hw:0,0`, `alsactl store`, the XVF3800 control tool's HID lookup — assumes the array is card 0, so we pin it explicitly via the `snd-usb-audio` module's `index=` / `vid=` / `pid=` options. The options line is appended to `/etc/modprobe.d/alsa-base.conf`, a file Raspberry Pi OS does not create by default; the `grep -qxF` guard keeps the append idempotent, and `2>/dev/null` swallows grep's "No such file" complaint on the first run when the file has yet to exist. The reboot is required because `snd-usb-audio` only honours the options when the module loads, and it loads very early in boot. The vid/pid pair `2886:001a` matches the retail Seeed ReSpeaker XVF3800 as shipped today; a future hardware revision under a different PID would need this line updated.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
grep -qxF 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' /etc/modprobe.d/alsa-base.conf 2>/dev/null || echo 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' | sudo tee -a /etc/modprobe.d/alsa-base.conf
sudo reboot
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
options snd-usb-audio index=0 vid=0x2886 pid=0x001a
Connection to framelink-douwe.local closed by remote host.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line is the options line echoed back as `tee` writes it into `/etc/modprobe.d/alsa-base.conf`; the second is your SSH session ending as the Pi reboots. If an error appears instead of the options line, the append did not land — re-run the first command before rebooting. Wait for the Pi to come back up, then reconnect over SSH before the next step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The ReSpeaker is now card 0 on this boot and every future one, no matter what else is plugged into the USB ports. No sound has been made yet — the amplifier and volume come after the control tool is installed.

<a id="2-install-and-verify-the-xvf3800-host-control-tool"></a>
<img src="https://img.shields.io/badge/STEP_02-Install_and_verify_the_XVF3800_host_control_tool-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Install and verify the XVF3800 host control tool"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The ReSpeaker contains its own sound processor whose settings — the speaker amplifier, echo cancellation, the LED ring — cannot be reached through the Pi's normal volume controls. The Pi has no program yet that can talk to it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Download Seeed's ready-made control tool from their official repository, make it executable, and ask the device for its firmware version as proof the two can talk.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 exposes two things over the same USB cable: the audio interface that ALSA sees as card 0, and a separate USB HID control interface that speaks an XMOS command/response protocol for configuring the DSP — AEC parameters, mic/reference gains, GPIO, the LED ring, and device-management commands like `VERSION` / `SAVE_CONFIGURATION`. ALSA's mixer does not reach into these DSP-side parameters, so without a host-side tool that speaks the protocol we could not enable the speaker amplifier in the next step or read back DSP state for diagnostics. Seeed distribute a pre-built aarch64 `xvf_host` binary and its supporting `.so` files alongside the firmware releases in [respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY); we use that binary directly — no compiler toolchain, no `pip` install, no XMOS SDK. Line by line:
1. `sudo apt-get update` refreshes the package lists so the next install resolves against current versions.
2. `sudo apt-get install -y git` installs git, which the clone needs.
3. The guarded `git clone --depth 1` fetches only the newest revision of the repository into `~/xvf3800`; the `[ -d ~/xvf3800/.git ]` test skips the clone when it is already there.
4. `chmod +x` marks the binary executable — trivially idempotent.
5. The smoke test runs `xvf_host VERSION` from inside the binary's own directory, because the binary loads its three `.so` files relative to that directory; wrapping the `cd` in a subshell leaves the surrounding shell's working directory untouched. `sudo` is required because the HID device node is root-owned and Seeed do not ship a udev rule for it.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt-get update
sudo apt-get install -y git
[ -d ~/xvf3800/.git ] || git clone --depth 1 https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY.git ~/xvf3800
chmod +x ~/xvf3800/host_control/rpi_64bit/xvf_host
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host VERSION)
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

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

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `...` marks trimmed apt download-and-unpack lines. The last two lines are the smoke test: `Found device VID: 10374 PID: 26 interface: 3` confirms `xvf_host` found the right USB device — `10374` is `0x2886` in decimal and `26` is `0x001A` — and opened HID interface 3, the control interface. `VERSION 2 0 6` is the Seeed firmware version reported by the device; FrameLink uses this retail firmware as-shipped and never reflashes it — newer releases (currently up to v2.0.7) only adjust LED and DAC-volume behaviour that does not affect this build. If the smoke test instead prints `device_init() -- No device found`, unplug and re-seat the ReSpeaker's USB cable and re-run the last command — the HID interface occasionally needs a fresh enumeration after the reboot from [step 1](#1-pin-the-xvf3800-to-a-stable-alsa-card-index).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Pi can now talk to the ReSpeaker's on-board sound processor, and the device answered with its firmware version. The speaker is still silent — switching on its amplifier and setting the volume is next.

<a id="3-enable-the-speaker-amplifier-and-test-playback"></a>
<img src="https://img.shields.io/badge/STEP_03-Enable_the_speaker_amplifier_and_test_playback-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Enable the speaker amplifier and test playback"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Nothing has come out of the speaker yet: its amplifier is controlled by a switch inside the ReSpeaker that has not been checked, and the playback volume has not been set to the top of its range, which this small speaker needs to be clearly audible.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the ReSpeaker's pin states, explicitly switch the amplifier pin on, raise the playback volume to its maximum, and play a short spoken test sound through the speaker.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 exposes five addressable GPO pins; the one that controls the speaker amplifier is `X0D31`, and it is active-low (low = amp enabled). Per Seeed's [host_control/README.md](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/blob/master/host_control/README.md#gpio-control), `GPO_READ_VALUES` returns five values in the fixed order `X0D11, X0D30, X0D31, X0D33, X0D39`, and `GPO_WRITE_VALUE` addresses the same five pins by their XMOS port number. Firmware v2.0.6 (the retail shipping version) already boots with `X0D31` low, so the amp is effectively enabled out of the box — the `GPO_WRITE_VALUE 31 0` below is a belt-and-braces idempotent no-op against any future firmware that might ship with a different default. A class-D amp with no signal produces an audible hiss; this is the amp's noise floor, it starts at boot, and it is normal.

The test plays one of the stock `alsa-utils` voice samples (`Front_Left.wav` — the words "Front Left" spoken). Because the Adafruit 3351 mono speaker is a low-sensitivity driver and the on-board amp only delivers a few watts, ALSA's `PCM` playback volume has to sit at the top of its range (`60/60` = 0 dB) for the sample to be clearly audible — `amixer -c 0 sset PCM 60` sets exactly that, and it is the loudness ceiling of the current hardware combination. The mono speaker only reproduces the left channel; the right channel on the TRS jack is either unused (TS plug) or summed into the single driver.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_READ_VALUES)
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_WRITE_VALUE 31 0)
amixer -c 0 sset PCM 60
aplay -D plughw:0,0 /usr/share/sounds/alsa/Front_Left.wav
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

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

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `GPO_READ_VALUES 0 0 0 1 0` readback means `X0D11=0, X0D30=0, X0D31=0, X0D33=1, X0D39=0` — the third value is `X0D31`, already low, confirming the amp is enabled. `X0D33=1` is the LED-ring power rail (active-high, so `1` means the ring is powered — you should see the LED ring cycling its default rainbow → direction-of-arrival pattern). The `GPO_WRITE_VALUE 31 0` command produces only the `device_init()` banner because it is a write with no return payload. During the `aplay` you should hear the words "Front Left" spoken clearly through the speaker, over the amp's steady hiss. If you hear no voice at all — only hiss — check the 3.5 mm plug is fully seated and re-run `GPO_READ_VALUES` to verify the third value is still `0`. If the voice is present but very faint even at `PCM 60`, the speaker-plus-amp combination is at its ceiling for this hardware; [the project TODO](../TODO.md) records an external amplifier as the remedy if a noisier deployment environment ever needs it.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The whole playback path works — card 0, the ReSpeaker's processor, the amplifier, and the speaker — at the volume the frame will actually use. That volume lives only in the sound card's running memory so far; making it survive a reboot is next.

<a id="4-persist-the-alsa-mixer-state-across-reboots"></a>
<img src="https://img.shields.io/badge/STEP_04-Persist_the_ALSA_mixer_state_across_reboots-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Persist the ALSA mixer state across reboots"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The volume set in the previous step lives only in the sound card's running state. Left like this, a reboot could bring the frame back at whatever default level the driver picks — possibly too quiet to hear.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Save the current mixer settings to a file on disk — the system already restores that file automatically at every boot — then reboot to put the round trip to the test.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The `alsa-utils` package that ships with Raspberry Pi OS Lite Trixie includes `alsa-restore.service`, a static systemd unit that runs `alsactl restore` early in boot, reading the saved mixer values from `/var/lib/alsa/asound.state` and applying them to every sound card the system sees. Nothing needs to be enabled, installed, or written — the service is already pulled in by the sound subsystem and runs automatically; the only missing piece is the file it restores from. `sudo alsactl store` captures the current in-memory mixer state into `/var/lib/alsa/asound.state`, rewriting the file every time it runs — there is no "if-changed" guard needed because the file is itself the desired state, and running it twice is indistinguishable from running it once. The reboot exists purely so the next step can prove the restore really happens.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo alsactl store
sudo reboot
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Connection to framelink-douwe.local closed by remote host.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`alsactl store` prints nothing on success, so the disconnect line from the reboot is the only output of the whole block. If anything else prints before it — an error naming `/var/lib/alsa/asound.state` — the store failed; when the Pi is back, set the volume again with the `amixer -c 0 sset PCM 60` command from [step 3](#3-enable-the-speaker-amplifier-and-test-playback) and re-run `sudo alsactl store`. Wait for the Pi to come back up, then reconnect over SSH for the next step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The mixer levels are saved on disk and the Pi is rebooting. Whether the saved state actually comes back on boot has not been proven yet — that is exactly what the next step checks.

<a id="5-confirm-the-mixer-state-survived-the-reboot"></a>
<img src="https://img.shields.io/badge/STEP_05-Confirm_the_mixer_state_survived_the_reboot-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Confirm the mixer state survived the reboot"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The volume was saved just before the reboot, but nothing has shown that the Pi restored it on the way back up. A frame that silently loses its volume on every boot would only be discovered weeks later.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Now that you are reconnected, read the volume back and check that the automatic restore service ran during boot.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Line by line:
1. `amixer -c 0 sget PCM` reads the current `PCM` mixer control from card 0; `grep 'Front Left'` narrows the output to the one line showing the restored playback level.
2. `systemctl status alsa-restore.service` reports what the restore unit did during this boot; `--no-pager` prints straight to the terminal instead of opening an interactive pager, and `head -8` keeps just the summary block.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
amixer -c 0 sget PCM | grep 'Front Left'
systemctl status alsa-restore.service --no-pager | head -8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

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

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `Playback 60 [100%] [0.00dB]` line is PCM coming back up at the stored level. In the service block, `Active: active (exited)` together with `status=0/SUCCESS` shows the unit ran once at boot and exited cleanly; the timestamps, `Invocation`, and `Main PID` values will differ on your unit. The service also emits a couple of benign `failed to import hw:1 use case configuration` lines for the HDMI cards, which ship without UCM profiles — they fall outside the `head -8` slice and are safe to ignore. A `Playback` value lower than `60` means the restore did not apply — set the level again per [step 3](#3-enable-the-speaker-amplifier-and-test-playback), then repeat [step 4](#4-persist-the-alsa-mixer-state-across-reboots).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The volume now survives reboots with no manual step — the frame will always wake up at full playback level. The speaker side is complete; only the microphones remain untested.

<a id="6-validate-mic-capture-with-a-round-trip-recording"></a>
<img src="https://img.shields.io/badge/STEP_06-Validate_mic_capture_with_a_round--trip_recording-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Validate mic capture with a round-trip recording"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything so far has tested sound going out. The microphones have had no test at all — and a video-calling frame that cannot hear you is only half working.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Record three seconds of your own voice, confirm the recording file is exactly the size it should be, play it back through the speaker, and delete the file.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 emits two capture channels: the left channel carries the AEC-processed, beamformed, auto-selected voice output (what a voice call wants), and the right channel carries the ASR-ready output intended for downstream speech recognition. Recording at 48 kHz / 16-bit / stereo matches the device's native format — no resampling, no surprises. A pass here means the USB capture endpoint, the XVF3800's mic array and AEC processing, and ALSA's card-0 routing all line up correctly. Line by line:
1. `arecord` captures 3 seconds (`-d 3`) of stereo (`-c 2`), 16-bit little-endian (`-f S16_LE`), 48 kHz (`-r 48000`) audio from card 0 into `/tmp/mic_test.wav`.
2. `ls -l` confirms the size: a 3-second stereo capture is exactly 576,044 bytes (`3 s × 48000 Hz × 2 channels × 2 bytes + 44-byte header`).
3. `aplay` plays the capture back through the speaker.
4. `rm` deletes the test file, leaving nothing behind.

Speak at normal conversational volume from roughly 30 cm in front of the array during the recording window — it starts the moment `arecord` prints its `Recording WAVE` line.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
arecord -D plughw:0,0 -c 2 -f S16_LE -r 48000 -d 3 /tmp/mic_test.wav
ls -l /tmp/mic_test.wav
aplay -D plughw:0,0 /tmp/mic_test.wav
rm /tmp/mic_test.wav
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Recording WAVE '/tmp/mic_test.wav' : Signed 16 bit Little Endian, Rate 48000 Hz, Stereo
-rw-r--r-- 1 framelink framelink 576044 Apr 12 20:14 /tmp/mic_test.wav
Playing WAVE '/tmp/mic_test.wav' : Signed 16 bit Little Endian, Rate 48000 Hz, Stereo
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `ls -l` line must show exactly `576044` bytes (the date and time will differ on your unit). During the `aplay` you should hear your own voice reproduced clearly through the speaker — the mono speaker plays the left channel, so what you hear is the AEC-processed beamformed output, which is the channel a video call would actually use. If the file is exactly 576,044 bytes but plays back as silence, the capture endpoint opened but no samples were captured — check that the ReSpeaker's hardware mute button has not been pressed: it toggles `X0D30`, so re-run the `GPO_READ_VALUES` command from [step 3](#3-enable-the-speaker-amplifier-and-test-playback) and confirm the second value is `0`, not `1`. If `arecord` itself errors with `Device or resource busy`, something else already opened card 0's capture endpoint — the typical culprit is a stale `arecord` from a previous aborted run, found via `sudo fuser -v /dev/snd/*` and killed.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Both audio directions are proven: the array's microphones captured your voice through the full USB and processing chain, and the speaker played it back intelligibly. The audio hardware now does everything a voice call needs.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`aplay -l` and `arecord -l` both show the XVF3800 as card 0 on every boot, the `PCM` mixer comes back at `60/60` after a reboot with no manual step, a short playback through the speaker is audibly clear, and a three-second mic recording plays back intelligibly through the speaker.
