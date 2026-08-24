# Software Build Guide 04 — Audio Configuration

Configure the ReSpeaker XVF3800 USB mic array and its attached speaker so that audio capture and playback work reliably end-to-end: pin the USB device to a stable ALSA card index (and switch off the HDMI sound cards that can otherwise steal that index on a slow boot), install Seeed's host-side tool that talks to the array's on-board DSP, pin the array's firmware to a known version, enable the speaker amplifier and set **both** playback volumes (including a second, easily-missed one that ships at -20 dB and costs most of the speaker's loudness), persist it all across reboots, and prove both directions with a spoken-word playback test and a round-trip microphone recording.

---

<a id="1-pin-the-xvf3800-to-a-stable-alsa-card-index"></a>
<img src="https://img.shields.io/badge/STEP_01-Pin_the_XVF3800_to_a_stable_ALSA_card_index-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Pin the XVF3800 to a stable ALSA card index"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Every boot, Linux numbers the sound cards in whatever order it happens to find them. If any other USB device with an audio function ever joins the bus (a headset, another mic, a USB-C dock that happens to expose an audio endpoint), the ReSpeaker's card number can shift, and everything in this guide and beyond assumes it is card 0.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tell the USB sound driver, once and permanently, that the device with the ReSpeaker's unique USB identity is always card 0, then reboot so the rule applies from the earliest moment of boot.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

On a fresh install `snd-usb-audio` hands out card indices in enumeration order. Everything downstream (`aplay -D hw:0,0`, `alsactl store`, the XVF3800 control tool's HID lookup) assumes the array is card 0, so we pin it explicitly via the `snd-usb-audio` module's `index=` / `vid=` / `pid=` options. The options line is appended to `/etc/modprobe.d/alsa-base.conf`, a file Raspberry Pi OS does not create by default; the `grep -qxF` guard keeps the append idempotent, and `2>/dev/null` swallows grep's "No such file" complaint on the first run when the file has yet to exist. The reboot is required because `snd-usb-audio` only honours the options when the module loads, and it loads very early in boot. The vid/pid pair `2886:001a` matches the retail Seeed ReSpeaker XVF3800 as shipped today; a future hardware revision under a different PID would need this line updated.

The pin alone is not enough, and the failure it leaves open is nasty: on a cold boot where USB enumerates slowly, one of the Pi's built-in HDMI sound cards can claim index 0 *first*, and then `snd-usb-audio`, forced to exactly index 0 by our pin, fails outright (`cannot find the slot for index 0 ... error -16` in the kernel log) and the frame wakes up with **no working audio at all**. Measured on real hardware: intermittent, cold-boots only. The frame never uses HDMI audio (its display is DSI), so the second command removes the competitor entirely: appending `,noaudio` to the stock `dtoverlay=vc4-kms-v3d` line in `/boot/firmware/config.txt` disables the HDMI sound cards at the device-tree level, leaving index 0 with exactly one claimant on every boot. The `grep -q ... ||` guard makes the edit idempotent.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
grep -qxF 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' /etc/modprobe.d/alsa-base.conf 2>/dev/null || echo 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' | sudo tee -a /etc/modprobe.d/alsa-base.conf
grep -q 'vc4-kms-v3d,noaudio' /boot/firmware/config.txt || sudo sed -i 's/^dtoverlay=vc4-kms-v3d$/dtoverlay=vc4-kms-v3d,noaudio/' /boot/firmware/config.txt
sudo reboot
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
options snd-usb-audio index=0 vid=0x2886 pid=0x001a
Connection to framelink-douwe.local closed by remote host.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line is the options line echoed back as `tee` writes it into `/etc/modprobe.d/alsa-base.conf`; the `config.txt` edit prints nothing; the last line is your SSH session ending as the Pi reboots. If an error appears instead of the options line, the append did not land, so re-run the first command before rebooting. Wait for the Pi to come back up, then reconnect over SSH before the next step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The ReSpeaker is now card 0 on this boot and every future one, no matter what else is plugged into the USB ports. No sound has been made yet; the amplifier and volume come after the control tool is installed.

<a id="2-install-and-verify-the-xvf3800-host-control-tool"></a>
<img src="https://img.shields.io/badge/STEP_02-Install_and_verify_the_XVF3800_host_control_tool-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Install and verify the XVF3800 host control tool"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The ReSpeaker contains its own sound processor whose settings (the speaker amplifier, echo cancellation, the LED ring) cannot be reached through the Pi's normal volume controls. The Pi has no program yet that can talk to it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Download Seeed's ready-made control tool from their official repository, make it executable, and ask the device for its firmware version as proof the two can talk.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 exposes two things over the same USB cable: the audio interface that ALSA sees as card 0, and a separate USB HID control interface that speaks an XMOS command/response protocol for configuring the DSP: AEC parameters, mic/reference gains, GPIO, the LED ring, and device-management commands like `VERSION` / `SAVE_CONFIGURATION`. ALSA's mixer does not reach into these DSP-side parameters, so without a host-side tool that speaks the protocol we could not enable the speaker amplifier in the next step or read back DSP state for diagnostics. Seeed distribute a pre-built aarch64 `xvf_host` binary and its supporting `.so` files alongside the firmware releases in [respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY); we use that binary directly, with no compiler toolchain, no `pip` install and no XMOS SDK. Line by line:
1. `sudo apt-get update` refreshes the package lists so the next install resolves against current versions.
2. `sudo apt-get install -y git` installs git, which the clone needs.
3. The guarded `git clone --depth 1` fetches only the newest revision of the repository into `~/xvf3800`; the `[ -d ~/xvf3800/.git ]` test skips the clone when it is already there.
4. `chmod +x` marks the binary executable, which is trivially idempotent.
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

The `...` marks trimmed apt download-and-unpack lines. The last two lines are the smoke test: `Found device VID: 10374 PID: 26 interface: 3` confirms `xvf_host` found the right USB device (`10374` is `0x2886` in decimal and `26` is `0x001A`) and opened HID interface 3, the control interface. `VERSION 2 0 6` is the Seeed firmware version reported by the device; FrameLink uses this retail firmware as-shipped and never reflashes it; newer releases (currently up to v2.0.7) only adjust LED and DAC-volume behaviour that does not affect this build. If the smoke test instead prints `device_init() -- No device found`, unplug and re-seat the ReSpeaker's USB cable and re-run the last command, because the HID interface occasionally needs a fresh enumeration after the reboot from [step 1](#1-pin-the-xvf3800-to-a-stable-alsa-card-index).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Pi can now talk to the ReSpeaker's on-board sound processor, and the device answered with its firmware version. The speaker is still silent; switching on its amplifier and setting the volume is next.

<a id="3-pin-the-array-firmware-to-v2-0-10"></a>
<img src="https://img.shields.io/badge/STEP_03-Pin_the_array_firmware_to_v2.0.10-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Pin the array firmware to v2.0.10"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The ReSpeaker arrives with whatever firmware its production batch was flashed with, and the versions behave differently: different volume handling, different LED behaviour. A fleet of frames built months apart would drift apart in ways that are miserable to debug.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install the standard USB flashing tool, ask the array which firmware it runs, and flash it to v2.0.10, the version this build is validated against, only if it is not already there.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 supports standard USB Device Firmware Upgrade. The firmware images ship inside the same repository step 2 already cloned (`~/xvf3800/xmos_firmwares/usb/`), so nothing extra is downloaded. Line by line:
1. `dfu-util` is the stock Debian DFU flasher.
2. The first `VERSION` call prints the currently running firmware.
3. The guarded flash line runs `dfu-util` only when the version is not already `2 0 10`: `-a 1` targets the array's "DFU Upgrade" partition, `-e` detaches it into DFU mode, `-D` supplies the image, and `-R` resets it back into normal operation afterwards. The flash takes about thirty seconds, prints a progress bar, and the array then re-enumerates on USB.
4. The `sleep` gives the re-enumeration time to settle, and the final `VERSION` proves the array now runs v2.0.10.

The version pin matters concretely on the playback side: shipping firmware (v2.0.6-era) and v2.0.10 expose and default the DAC volume path differently, and the next step's volume settings are validated against v2.0.10. If a flash is ever interrupted, the array has a built-in recovery: hold the Mute button while re-plugging power to enter Safe Mode, then flash again against the factory partition.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sudo apt-get install -y dfu-util
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host VERSION)
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host VERSION | grep -q 'VERSION 2 0 10') || sudo dfu-util -R -e -a 1 -D ~/xvf3800/xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin
sleep 5
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host VERSION)
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. apt installs dfu-util; the first VERSION prints the shipped firmware (e.g. VERSION 2 0 6); dfu-util prints a download progress bar ending in "Done!" and a reset notice; the final VERSION prints VERSION 2 0 10.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The last line must read `VERSION 2 0 10`. If the array already ran v2.0.10, the `dfu-util` line is skipped silently and the two `VERSION` calls print the same thing, which is the idempotent re-run case. A `dfu-util` error about not finding the DFU device usually means the array is mid-re-enumeration, so wait a few seconds and re-run the block from the guarded line.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Every frame in the fleet now runs the exact firmware this build was validated against. The array is still silent; switching on its amplifier and setting the volumes correctly is next.

<a id="4-enable-the-speaker-amplifier-and-set-the-volumes"></a>
<img src="https://img.shields.io/badge/STEP_04-Enable_the_speaker_amplifier_and_set_the_volumes-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Enable the speaker amplifier and set the volumes"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Nothing has come out of the speaker yet: its amplifier is controlled by a switch inside the ReSpeaker that has not been checked, and the sound card hides **two** separate volume controls, one of which ships at a fraction of full level and silently costs the speaker most of its loudness if it is missed.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the ReSpeaker's pin states, explicitly switch the amplifier pin on, set both playback volumes to their validated levels, and play a short spoken test sound through the speaker.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 exposes five addressable GPO pins; the one that controls the speaker amplifier is `X0D31`, and it is active-low (low = amp enabled). Per Seeed's [host_control/README.md](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/blob/master/host_control/README.md#gpio-control), `GPO_READ_VALUES` returns five values in the fixed order `X0D11, X0D30, X0D31, X0D33, X0D39`, and `GPO_WRITE_VALUE` addresses the same five pins by their XMOS port number. Firmware v2.0.10 (pinned in [step 3](#3-pin-the-array-firmware-to-v2-0-10)) boots with `X0D31` low, so the amp is effectively enabled out of the box, and the `GPO_WRITE_VALUE 31 0` below is a belt-and-braces idempotent no-op against any future firmware that might ship with a different default. A class-D amp with no signal produces an audible hiss; this is the amp's noise floor, it starts at boot, and it is normal.

The volume part is where a fresh build goes wrong without noticing. The card exposes **two** playback mixer controls: the obvious stereo `PCM,0`, and a second mono stage `PCM,1` that ships at `40/60` = **-20 dB**, one hundredth of full electrical power. Setting only the obvious control (as an earlier revision of this guide did) leaves the frame sounding like it needs a bigger amplifier when in fact it is being throttled in software: raising `PCM,1` was measured on this hardware as roughly **+18 dB** at the speaker, the difference between "audible in a quiet room" and genuine desk-phone loudness. The validated production levels are both controls at `60` (0 dB), measured at ~94 dB close-miked with the Adafruit 3351, and verified clean on 45 seconds of continuous studio-quality narration (judge quality with real speech material: a low-bitrate test blip can crackle at levels where actual speech plays perfectly). Do not push software gain above 0 dB anywhere in the chain (e.g. a PipeWire sink volume over 100%): beyond digital full scale there is no loudness left, only clipping. The mono speaker only reproduces the left channel; the right channel on the TRS jack is either unused (TS plug) or summed into the single driver.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_READ_VALUES)
(cd ~/xvf3800/host_control/rpi_64bit && sudo ./xvf_host GPO_WRITE_VALUE 31 0)
amixer -c 0 sset PCM,0 60
amixer -c 0 sset PCM,1 60
aplay -D plughw:0,0 /usr/share/sounds/alsa/Front_Left.wav
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The two GPO calls print the device banner, a GPO_READ_VALUES line of five digits, and a banner for the write; the two amixer calls each print the control's state block — PCM,0 ending at Playback 60 [100%] [0.00dB] on both channels, PCM,1 ending at Mono: Playback 60 [100%] [0.00dB] — and aplay prints its Playing WAVE line while the words "Front Left" sound from the speaker.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

In the `GPO_READ_VALUES` readback (five digits, e.g. `0 0 0 1 0`) the third value is `X0D31`, and it must be `0`, confirming the amp is enabled. `X0D33=1` is the LED-ring power rail (active-high, so you should see the LED ring lit). Both `amixer` outputs must show `[0.00dB]`; if `PCM,1` still reads `[-20.00dB]`, the second `sset` did not land, and the speaker will be dramatically too quiet. During the `aplay` you should hear "Front Left" spoken **loudly and clearly** over the amp's steady hiss. At these levels the frame is in desk-phone territory, not whisper territory. The stock sample is low-bitrate and can sound slightly rough at full level; that is the sample, not the hardware. If you hear no voice at all, check the speaker's JST plug is fully seated and re-run `GPO_READ_VALUES` to verify the third value is still `0`.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The whole playback path works, from card 0 through the ReSpeaker's processor, the amplifier and both volume stages to the speaker, at the loudness the frame will actually use. Those volumes live only in the sound card's running memory so far; making them survive a reboot is next.

<a id="5-persist-the-alsa-mixer-state-across-reboots"></a>
<img src="https://img.shields.io/badge/STEP_05-Persist_the_ALSA_mixer_state_across_reboots-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Persist the ALSA mixer state across reboots"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The volume set in the previous step lives only in the sound card's running state. Left like this, a reboot could bring the frame back at whatever default level the driver picks, possibly too quiet to hear.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Save the current mixer settings to a file on disk, which the system already restores automatically at every boot, then reboot to put the round trip to the test.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The `alsa-utils` package that ships with Raspberry Pi OS Lite Trixie includes `alsa-restore.service`, a static systemd unit that runs `alsactl restore` early in boot, reading the saved mixer values from `/var/lib/alsa/asound.state` and applying them to every sound card the system sees. Nothing needs to be enabled, installed, or written: the service is already pulled in by the sound subsystem and runs automatically; the only missing piece is the file it restores from. `sudo alsactl store` captures the current in-memory mixer state into `/var/lib/alsa/asound.state`, rewriting the file every time it runs. There is no "if-changed" guard needed because the file is itself the desired state, and running it twice is indistinguishable from running it once. The reboot exists purely so the next step can prove the restore really happens.

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

`alsactl store` prints nothing on success, so the disconnect line from the reboot is the only output of the whole block. If anything else prints before it, such as an error naming `/var/lib/alsa/asound.state`, the store failed; when the Pi is back, set the volumes again with the two `amixer` commands from [step 4](#4-enable-the-speaker-amplifier-and-set-the-volumes) and re-run `sudo alsactl store`. Wait for the Pi to come back up, then reconnect over SSH for the next step.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The mixer levels are saved on disk and the Pi is rebooting. Whether the saved state actually comes back on boot has not been proven yet; that is exactly what the next step checks.

<a id="6-confirm-the-mixer-state-survived-the-reboot"></a>
<img src="https://img.shields.io/badge/STEP_06-Confirm_the_mixer_state_survived_the_reboot-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Confirm the mixer state survived the reboot"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The volume was saved just before the reboot, but nothing has shown that the Pi restored it on the way back up. A frame that silently loses its volume on every boot would only be discovered weeks later.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Now that you are reconnected, read the volume back and check that the automatic restore service ran during boot.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Line by line:
1. `amixer -c 0 sget PCM,0` reads the stereo playback control from card 0; `grep 'Front Left'` narrows the output to the one line showing the restored level.
2. `amixer -c 0 sget PCM,1` reads the second, mono playback stage the same way. This is the control whose loss would silently cost 15 dB, so its restore is verified explicitly.
3. `systemctl status alsa-restore.service` reports what the restore unit did during this boot; `--no-pager` prints straight to the terminal instead of opening an interactive pager, and `head -8` keeps just the summary block.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
amixer -c 0 sget PCM,0 | grep 'Front Left'
amixer -c 0 sget PCM,1 | grep 'Mono:'
systemctl status alsa-restore.service --no-pager | head -8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The first amixer line prints Front Left: Playback 60 [100%] [0.00dB] [on]; the second prints Mono: Playback 60 [100%] [0.00dB] [on]; the service block shows alsa-restore.service as active (exited) with status=0/SUCCESS and boot-time timestamps.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Both `Playback 60 [100%] [0.00dB]` lines are the two volume stages coming back up at the stored levels; `PCM,1` is the one to watch, since losing it costs ~18 dB. In the service block, `Active: active (exited)` together with `status=0/SUCCESS` shows the unit ran once at boot and exited cleanly; the timestamps, `Invocation`, and `Main PID` values will differ on your unit. A value lower than `60` on either control means the restore did not apply, so set the levels again per [step 4](#4-enable-the-speaker-amplifier-and-set-the-volumes), then repeat [step 5](#5-persist-the-alsa-mixer-state-across-reboots).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The volume now survives reboots with no manual step, so the frame will always wake up at full playback level. The speaker side is complete; only the microphones remain untested.

<a id="7-validate-mic-capture-with-a-round-trip-recording"></a>
<img src="https://img.shields.io/badge/STEP_07-Validate_mic_capture_with_a_round--trip_recording-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Validate mic capture with a round-trip recording"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything so far has tested sound going out. The microphones have had no test at all, and a video-calling frame that cannot hear you is only half working.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Record three seconds of your own voice, confirm the recording file is exactly the size it should be, play it back through the speaker, and delete the file.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The XVF3800 emits two capture channels: the left channel carries the AEC-processed, beamformed, auto-selected voice output (what a voice call wants), and the right channel carries the ASR-ready output intended for downstream speech recognition. Recording at 48 kHz / 16-bit / stereo matches the device's native format, so there is no resampling and no surprises. A pass here means the USB capture endpoint, the XVF3800's mic array and AEC processing, and ALSA's card-0 routing all line up correctly. Line by line:
1. `arecord` captures 3 seconds (`-d 3`) of stereo (`-c 2`), 16-bit little-endian (`-f S16_LE`), 48 kHz (`-r 48000`) audio from card 0 into `/tmp/mic_test.wav`.
2. `ls -l` confirms the size: a 3-second stereo capture is exactly 576,044 bytes (`3 s × 48000 Hz × 2 channels × 2 bytes + 44-byte header`).
3. `aplay` plays the capture back through the speaker.
4. `rm` deletes the test file, leaving nothing behind.

Speak at normal conversational volume from roughly 30 cm in front of the array during the recording window, which starts the moment `arecord` prints its `Recording WAVE` line.

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

The `ls -l` line must show exactly `576044` bytes (the date and time will differ on your unit). During the `aplay` you should hear your own voice reproduced clearly through the speaker. The mono speaker plays the left channel, so what you hear is the AEC-processed beamformed output, which is the channel a video call would actually use. If the file is exactly 576,044 bytes but plays back as silence, the capture endpoint opened but no samples were captured, so check that the ReSpeaker's hardware mute button has not been pressed: it toggles `X0D30`, so re-run the `GPO_READ_VALUES` command from [step 4](#4-enable-the-speaker-amplifier-and-set-the-volumes) and confirm the second value is `0`, not `1`. If `arecord` itself errors with `Device or resource busy`, something else already opened card 0's capture endpoint; the typical culprit is a stale `arecord` from a previous aborted run, found via `sudo fuser -v /dev/snd/*` and killed.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Both audio directions are proven: the array's microphones captured your voice through the full USB and processing chain, and the speaker played it back intelligibly. The audio hardware now does everything a voice call needs.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`aplay -l` and `arecord -l` both show the XVF3800 as card 0 on every boot (with no HDMI sound cards present to contest it), the array reports firmware `VERSION 2 0 10`, both playback controls, `PCM,0` and `PCM,1`, come back at `60` (0 dB) after a reboot with no manual step, a short playback through the speaker is loud and clear, and a three-second mic recording plays back intelligibly through the speaker.
