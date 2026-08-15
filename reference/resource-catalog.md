# FrameLink v2 — Resource Catalog

The enumeration of every atomic device setting extracted from build guides 3–12, as required by
[version2.md Appendix B item 1](../version2.md). This is reference material, not a build guide:
the seven-block step structure of [CLAUDE.md §2.1](../CLAUDE.md) does not apply here.

---

## Method and provenance

**Sources read in full.** The ten in-scope guides — [guide 3](../docs/3-hardware-configuration.md),
[4](../docs/4-audio-configuration.md), [5](../docs/5-kiosk-base.md), [6](../docs/6-camera.md),
[7](../docs/7-livekit-server.md), [8](../docs/8-webrtc-validation.md), [9](../docs/9-immich-kiosk.md),
[10](../docs/10-spa.md), [11](../docs/11-gpio-button.md), [12](../docs/12-systemd-and-reliability.md) —
plus [guide 13](../docs/13-multi-device-deploy.md) for the fleet-manager split, the
[v1 state inventory](v1-state-inventory.txt) as the parity cross-check, and the deploy artifacts the
guides install (`deploy/systemd/*`, `deploy/docker/*`, `deploy/gpio/framelink-gpio.py`,
`deploy/wireplumber/*`, `app/config.example.json`).

**Granularity rule applied.** Per [version2.md §2.2](../version2.md), *one differential diagnosis = one
resource*. Two sub-rules were needed to apply it consistently and are stated here so a future
implementer does not have to re-derive them:

1. **One file written atomically = one resource**, even when the file carries several directives —
   because you cannot *act* on the directives independently, and a content compare already names
   which directive is wrong. This is why the two WirePlumber monitor keys, the two journald keys, and
   the two `APT::Periodic` switches are one resource each.
2. **A setting and its post-boot effect are two resources when they can disagree.** The CPU governor
   unit and the live governor value are separate; so are the camera unit and the PipeWire node it is
   supposed to have created; so are the unit file's `ExecStart` and the running process's command
   line. This split is exactly what [§2.4](../version2.md) demands — "applied" is never claimed from a
   successful write.

**Observability rule applied.** Per [§2.4](../version2.md), every Observe below is something you can
read on a **freshly booted** frame, with no preceding action in the same session. Where a guide's own
verification is only meaningful after a runtime event (a call, a touch, a button press), the Verify
field says so explicitly and names what makes it different.

**Format.** One block per resource. `dependsOn` lists resource ids that must be `InSync` first;
`—` means it depends only on the agent roots. Value source is either *fixed by the catalog* or a
named Fleet Manager setting per [§3.4](../version2.md) (every setting is a fleet default with a
per-device override).

**Counts.** 71 resources come from guides 3–12; 8 more are cross-guide or mandated by v2 itself and
are listed in their own section. Total **79**.

| Guide | Resources |
| --- | ---: |
| 3 — Hardware configuration | 2 |
| 4 — Audio configuration | 13 |
| 5 — Kiosk base | 14 |
| 6 — Camera | 16 |
| 7 — LiveKit server | 0 |
| 8 — WebRTC validation | 1 |
| 9 — Immich Kiosk | 8 |
| 10 — SPA | 6 |
| 11 — GPIO button | 2 |
| 12 — systemd & reliability | 9 |
| **Subtotal (guides 3–12)** | **71** |
| Cross-guide and v2-mandated | 8 |
| **Total** | **79** |

---

## Guide 3 — Hardware configuration

**`boot.config.dtoverlay-waveshare-panel`**

- **From** — [guide 3 step 1](../docs/3-hardware-configuration.md#1-enable-the-dsi-touch-display-and-rotate-the-console-to-landscape)
- **Sets** — the exact line `dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a` present once under `[all]` in `/boot/firmware/config.txt`.
- **Observe** — `grep -cxF 'dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a' /boot/firmware/config.txt` (must be exactly `1`).
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog (panel model is a hardware fact). The `,dsi0` suffix variant applies only if the ribbon is on the LAN-side DSI port.
- **Risk** — **brick-capable** (`config.txt`).
- **Notes** — Checkpoint assertion, separately observable post-boot: `/sys/class/drm/card*-DSI-*/status` reads `connected`. Kept out of Observe because a present line with a disconnected panel is a *hardware* diagnosis (ribbon/5 V), not a drift the agent can act on. Duplicate-line detection matters: `grep -c` rather than `grep -q`, because a non-idempotent write history is the failure this guards.

**`boot.cmdline.fbcon-rotate`**

- **From** — [guide 3 step 1](../docs/3-hardware-configuration.md#1-enable-the-dsi-touch-display-and-rotate-the-console-to-landscape)
- **Sets** — `fbcon=rotate:1` appended to the single line of `/boot/firmware/cmdline.txt`; exactly one `fbcon=rotate:` token.
- **Observe** — `grep -o 'fbcon=rotate:[0-9]*' /boot/firmware/cmdline.txt` **and** `grep -o 'fbcon=rotate:[0-9]*' /proc/cmdline`.
- **Verify** — identical. Both halves are deliberate: the file is the desired state, `/proc/cmdline` proves the bootloader actually handed it to the kernel. [§2.4](../version2.md) records the governor case where `/proc/cmdline` agreed and the effect still did not land, so `/proc/cmdline` alone is not sufficient evidence either — it is necessary, not sufficient.
- **dependsOn** — —
- **Value source** — fleet setting `display.consoleRotation` (fixed at `1` today; guide names `3` as the upside-down remedy).
- **Risk** — **brick-capable** (`cmdline.txt`; a malformed single line is unbootable).
- **Notes** — `cmdline.txt` must stay one line. The v1 reference line also carries `cfg80211.ieee80211_regdom=NL` and `ds=nocloud;i=rpi-imager-…`; see `boot.cmdline.wifi-regdom` and `identity.hostname`. Any writer that appends here competes with kernel-package postinst hooks from `raspberrypi-sys-mods`.

---

## Guide 4 — Audio configuration

**`audio.modprobe.snd-usb-audio-index`**

- **From** — [guide 4 step 1](../docs/4-audio-configuration.md#1-pin-the-xvf3800-to-a-stable-alsa-card-index)
- **Sets** — `options snd-usb-audio index=0 vid=0x2886 pid=0x001a` present in `/etc/modprobe.d/alsa-base.conf`.
- **Observe** — `grep -cxF 'options snd-usb-audio index=0 vid=0x2886 pid=0x001a' /etc/modprobe.d/alsa-base.conf` **and** `head -1 /proc/asound/cards` (card `0` must be `Array` / reSpeaker XVF3800).
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog. The `2886:001a` pair is the retail Seeed PID; a hardware revision changes it.
- **Risk** — not brick-capable, but a wrong value costs all audio.
- **Notes** — File does not exist on a stock image; the agent creates it. Measured failure this guards against is cold-boot-only and intermittent: an HDMI card can claim index 0 first, and the pinned module then fails outright with `cannot find the slot for index 0 … error -16`. That is why `boot.config.dtoverlay-vc4-kms-v3d-noaudio` exists as a second, separate resource — same symptom, different cause, different fix.

**`boot.config.dtoverlay-vc4-kms-v3d-noaudio`**

- **From** — [guide 4 step 1](../docs/4-audio-configuration.md#1-pin-the-xvf3800-to-a-stable-alsa-card-index)
- **Sets** — the stock `dtoverlay=vc4-kms-v3d` line in `/boot/firmware/config.txt` rewritten to `dtoverlay=vc4-kms-v3d,noaudio`.
- **Observe** — `grep -c '^dtoverlay=vc4-kms-v3d,noaudio$' /boot/firmware/config.txt` (`1`) **and** `grep -c '^dtoverlay=vc4-kms-v3d$' …` (`0`) **and** `wc -l < /proc/asound/cards` shows no HDMI cards.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog (the frame's display is DSI; HDMI audio is never used).
- **Risk** — **brick-capable** (`config.txt`).
- **Notes** — Distinct resource from the Waveshare overlay line even though both live in `config.txt`: different lines, different owners, different failure signatures. The `sed` in the guide is anchored to the exact stock line, so a `config.txt` whose vc4 line has any other suffix silently does not match — the agent must handle the general case rather than reproduce the anchored `sed`.

**`pkg.git`**

- **From** — [guide 4 step 2](../docs/4-audio-configuration.md#2-install-and-verify-the-xvf3800-host-control-tool); re-installed in [guide 10 step 1](../docs/10-spa.md#1-clone-the-framelink-app-onto-the-pi) (same resource, not counted twice).
- **Sets** — apt package `git` installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' git`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Only reason for `git` on the frame is fetching the reSpeaker repository (guide 4) and the FrameLink checkout (guide 10). Guide 10's use is superseded by the embedded app. Whether guide 4's use survives depends on how the agent obtains `xvf_host` — see open question 3. If it does not, this resource disappears.

**`tool.xvf-host.installed`**

- **From** — [guide 4 step 2](../docs/4-audio-configuration.md#2-install-and-verify-the-xvf3800-host-control-tool)
- **Sets** — Seeed's aarch64 `xvf_host` binary and its three sibling `.so` files present at a fixed path, binary executable. v1 path: `~/xvf3800/host_control/rpi_64bit/xvf_host`.
- **Observe** — `test -x <path>/xvf_host` and the three `.so` files present with the pinned SHA-256 set.
- **Verify** — presence and hashes **plus** a live `(cd <dir> && ./xvf_host VERSION)` returning the `Found device VID: 10374 PID: 26 interface: 3` banner. The extra half proves the HID control interface is reachable, which presence alone does not.
- **dependsOn** — —
- **Value source** — fixed; the upstream release is pinned by the catalog.
- **Risk** — —
- **Notes** — The binary loads its `.so` files **relative to its own directory**, so the working directory is part of the contract, not an incidental `cd`. Root is required — Seeed ship no udev rule for the HID node. Under v2 this is the same shape as Immich Kiosk (pinned upstream artifact, checksum-verified) rather than a git clone.

**`pkg.dfu-util`**

- **From** — [guide 4 step 3](../docs/4-audio-configuration.md#3-pin-the-array-firmware-to-v2-0-10)
- **Sets** — apt package `dfu-util` installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' dfu-util`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —

**`firmware.xvf3800.version`**

- **From** — [guide 4 step 3](../docs/4-audio-configuration.md#3-pin-the-array-firmware-to-v2-0-10)
- **Sets** — the array running firmware `VERSION 2 0 10`.
- **Observe** — `(cd <xvf dir> && sudo ./xvf_host VERSION)` → last line must read `VERSION 2 0 10`.
- **Verify** — identical, after the array has re-enumerated on USB. A short settle delay is part of the Act, not of Verify; on a freshly booted frame the array is always enumerated.
- **dependsOn** — `tool.xvf-host.installed`, `pkg.dfu-util`
- **Value source** — fixed by the catalog (the version the build is validated against).
- **Risk** — **brick-capable** (DFU). Recovery is a physical action at the frame: hold Mute while re-plugging power to enter Safe Mode, then reflash against the factory partition. The Pi itself still boots — see the ordering note in the sequencing section.
- **Notes** — v1 reference is at `2 0 10` (inventory `XVF3800_FIRMWARE`). The guide's own captured smoke test shows `2 0 6` shipping firmware, so a fresh array *will* need flashing. The version pin is load-bearing for the mixer resources below: 2.0.6-era and 2.0.10 firmware expose and default the DAC volume path differently.

**`audio.xvf3800.gpo-x0d31-amp-enable`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes)
- **Sets** — GPO pin `X0D31` low (active-low = speaker amplifier enabled).
- **Observe** — `(cd <xvf dir> && sudo ./xvf_host GPO_READ_VALUES)` → five values in the fixed order `X0D11, X0D30, X0D31, X0D33, X0D39`; the **third** must be `0`.
- **Verify** — identical.
- **dependsOn** — `firmware.xvf3800.version`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — On firmware 2.0.10 this boots low, so Act is normally a no-op — but it is still independently verifiable and a future firmware could default differently, which is exactly why it is its own resource. The same readback carries two diagnostics worth keeping: the **second** value is `X0D30`, the hardware Mute button (a `1` means someone pressed it, and mic capture is silent), and the **fourth** is `X0D33`, the LED ring rail (active-high). Neither is agent-settable; both belong in telemetry.

**`audio.mixer.pcm0-playback-volume`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes); verified by step 6
- **Sets** — ALSA simple control `PCM,0` on card 0 at `60` (0.00 dB) on both channels.
- **Observe** — `amixer -c 0 sget PCM,0` → `Front Left`/`Front Right: Playback 60 [100%] [0.00dB]`.
- **Verify** — identical. Guide 4 step 6 *is* this Verify, run after a reboot.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`, `firmware.xvf3800.version`, `audio.mixer.pcm0-playback-switch`
- **Value source** — fleet setting `audio.playbackVolume` ([§3.4](../version2.md) names audio volume explicitly). Do not permit values above 0 dB anywhere in the chain.
- **Risk** — —
- **Notes** — See the WirePlumber revert warning under `audio.alsa.stored-state`.

**`audio.mixer.pcm1-playback-volume`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes); verified by step 6
- **Sets** — ALSA simple control `PCM,1` (second, mono gain stage) on card 0 at `60` (0.00 dB).
- **Observe** — `amixer -c 0 sget PCM,1` → `Mono: Playback 60 [100%] [0.00dB]`.
- **Verify** — identical.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`, `firmware.xvf3800.version`, `audio.mixer.pcm1-playback-switch`
- **Value source** — fleet setting `audio.playbackVolume` (same setting, applied to both stages).
- **Risk** — —
- **Notes** — **The highest-value resource in this guide.** Ships at `40/60` = −20 dB; measured at roughly **+18 dB at the speaker** when corrected. A frame with `PCM,0` correct and `PCM,1` at default is fully functional and merely quiet — the class of fault nobody reports and nobody finds. Separate resource from `PCM,0` because it is a genuinely separate gain stage with its own default, not a second view of the same control.

**`audio.mixer.pcm0-playback-switch`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes) (implied); v1 inventory `ALSA_MIXER` / `ALSA_CARDS` state file
- **Sets** — `PCM Playback Switch` (index 0) unmuted, both channels `[on]`.
- **Observe** — `amixer -c 0 sget PCM,0` → `[on]` on both channels; equivalently `control.3` in `/var/lib/alsa/asound.state`.
- **Verify** — identical.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`
- **Value source** — fixed (`on`).
- **Risk** — —
- **Notes** — Not set by any guide command — it defaults on and `alsactl store` persists it. Listed because mute and volume are different diagnoses with the same symptom (silence), and the v1 reference records both switches `true`. Cheap to observe, so worth having.

**`audio.mixer.pcm1-playback-switch`**

- **From** — as above; v1 inventory `control.4` (`PCM Playback Switch`, `index 1`)
- **Sets** — `PCM Playback Switch` index 1 unmuted (`pswitch-joined`, mono).
- **Observe** — `amixer -c 0 sget PCM,1` → `[on]`.
- **Verify** — identical.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`
- **Value source** — fixed (`on`).
- **Risk** — —

**`audio.mixer.headset-capture-volume`**

- **From** — [guide 4 step 7](../docs/4-audio-configuration.md#7-validate-mic-capture-with-a-round-trip-recording) (implied); v1 inventory `ALSA_MIXER`
- **Sets** — capture controls `Headset,0` and `Headset,1` at `60` (0.00 dB), both `[on]`.
- **Observe** — `amixer -c 0 sget Headset,0` and `amixer -c 0 sget Headset,1`.
- **Verify** — identical.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`
- **Value source** — fleet setting `audio.captureVolume`.
- **Risk** — —
- **Notes** — Not set by any guide command; recorded here because it is persisted by `alsactl store` and is the mic-side twin of the `PCM,1` trap — a frame that cannot be heard while everything reports healthy. Kept as one resource pending open question 9: whether `Headset,0`/`Headset,1` are two real gain stages (as `PCM,0`/`PCM,1` turned out to be) or two views of one. If two, split it.

**`audio.alsa.stored-state`**

- **From** — [guide 4 step 5](../docs/4-audio-configuration.md#5-persist-the-alsa-mixer-state-across-reboots)
- **Sets** — `/var/lib/alsa/asound.state` containing the desired values for every mixer resource above.
- **Observe** — parse `/var/lib/alsa/asound.state` for card `Array` and compare each control against desired.
- **Verify** — identical. Deliberately **not** the same as reading the live mixer: the running value can be correct while the stored value is wrong (nothing has rebooted yet), and the stored value can be correct while the running value is wrong (something changed it after boot). Those are two different faults and the catalog keeps both observable.
- **dependsOn** — every `audio.mixer.*` resource
- **Value source** — derived (it is the persisted form of the mixer settings).
- **Risk** — —
- **Notes** — `alsa-restore.service` is a **static** unit shipped with `alsa-utils`; it is not enabled or installed and has no enablement resource — its post-boot state (`active (exited)`, `status=0/SUCCESS`) is a checkpoint assertion, not a setting. Rewriting the file wholesale is idempotent by construction. **Revert risk:** WirePlumber's `restore-device` policy keeps its own per-device volume/route state under `~/.local/state/wireplumber/` and applies it when the session starts — after `alsa-restore` has run. See the suspected-revert list.

---

## Guide 5 — Kiosk base

**`swap.zram-active`**

- **From** — [guide 5 step 1](../docs/5-kiosk-base.md#1-confirm-zram-swap-is-active); re-asserted by [guide 12 step 5](../docs/12-systemd-and-reliability.md#5-cut-sd-card-writes-to-make-the-card-last)
- **Sets** — `/dev/zram0` active as swap, type `partition`, ~2 GB, priority 100.
- **Observe** — `swapon --show` → a `/dev/zram0` row.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed (provided by the stock `rpi-swap` package; `systemd-zram-generator` is the mechanism).
- **Risk** — —
- **Notes** — Assert-only in practice; there is no Act beyond ensuring `rpi-swap` is installed and the generator's config is untouched. Distinct from `swap.no-file-backed`, which is the negative assertion with a real Act.

**`pkg.labwc`** · **`pkg.chromium`** · **`pkg.pipewire-alsa`** · **`pkg.wireplumber`** · **`pkg.wlr-randr`**

- **From** — [guide 5 step 2](../docs/5-kiosk-base.md#2-install-the-kiosk-packages) (five resources, one per package)
- **Sets** — each apt package installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' <pkg>`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed. Versions float and are frozen by the build per [§7.1](../version2.md); the catalog pins the *presence*, not the version.
- **Risk** — —
- **Notes** — One package = one resource is explicit in [§2.2](../version2.md). ~215 dependencies, ~256 MB download, ~750 MB on disk; apt resolves the transitive set, which is not enumerated here. On Trixie the browser package is `chromium`, not `chromium-browser`, and the binary is `/usr/bin/chromium`. `pkg.chromium` also drags in `rpi-chromium-mods`, which injects flags from `/etc/chromium.d/` — relevant to `unit.chromium-kiosk.running-matches-content`.

**`boot.autologin.getty-tty1`**

- **From** — [guide 5 step 3](../docs/5-kiosk-base.md#3-enable-console-autologin)
- **Sets** — `/etc/systemd/system/getty@tty1.service.d/autologin.conf` containing exactly `[Service]` / `ExecStart=` / `ExecStart=-/sbin/agetty --autologin framelink --noclear %I $TERM`.
- **Observe** — `cat /etc/systemd/system/getty@tty1.service.d/autologin.conf` **and** `systemctl show getty@tty1.service -p ExecStart` **and** `who` showing `framelink` on `tty1`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fleet setting `device.user` (the username; `framelink` in the running example).
- **Risk** — not brick-capable, but a wrong username means no user session, and every user unit below is then `Blocked`.
- **Notes** — Written by `raspi-config nonint do_boot_behaviour B2`, which is a **competing owner**: any later `raspi-config` boot-behaviour call rewrites or removes this file. The empty first `ExecStart=` is required by systemd to clear the inherited value; a drop-in missing that line does not override. **The whole user-unit layer hangs off this one file** — there is no `loginctl enable-linger` anywhere in the v1 build (see open question 7).

**`session.bash-profile-exec-labwc`**

- **From** — [guide 5 step 4](../docs/5-kiosk-base.md#4-start-labwc-on-tty1-after-autologin)
- **Sets** — `~/.bash_profile` with the `~/.profile` source line and the guarded `exec labwc` block (guards: `-z "$WAYLAND_DISPLAY"` and `"$(tty)" = "/dev/tty1"`).
- **Observe** — SHA-256 of `~/.bash_profile` against the desired content.
- **Verify** — identical, plus `pgrep -x labwc` on a booted frame.
- **dependsOn** — `pkg.labwc`, `boot.autologin.getty-tty1`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Both guards are load-bearing: without the tty test, `exec labwc` fires on **SSH logins** and breaks remote administration — which would also break the agent's own diagnostics channel. v1 reference file is 118 bytes.

**`unit.chromium-kiosk.content`**

- **From** — created in [guide 5 step 5](../docs/5-kiosk-base.md#5-create-the-chromium-systemd-user-service); **final desired value supplied by [guide 10 step 4](../docs/10-spa.md#4-point-the-kiosk-browser-at-the-app)** (one resource, not two)
- **Sets** — `~/.config/systemd/user/chromium-kiosk.service` matching the guide 10 form: `After=graphical-session.target framelink-spa.service framelink-camera.service`, `Wants=` the same two, `Requires=graphical-session.target`, `Environment="WAYLAND_DISPLAY=wayland-0"`, three `ExecStartPre` guards (profile wipe, Wayland-socket wait, `curl` readiness poll), the twelve-flag `ExecStart`, `Restart=always`, `RestartSec=5`, `WantedBy=default.target`.
- **Observe** — SHA-256 of the unit file against desired. v1 reference hash: `4d10e25c04715aff0ac2a98c7e1bcfb5ace440b0cd2f08efad90f8d687c1f65d`.
- **Verify** — identical.
- **dependsOn** — `pkg.chromium`, `boot.autologin.getty-tty1`
- **Value source** — mostly fixed; the target URL and the `framelink-spa`/`framelink-camera` ordering references change under v2 (the agent serves the app itself), so the desired content is a v2 rewrite of the v1 file rather than a copy.
- **Risk** — —
- **Notes** — Guide 5's interim version (placeholder URL, no SPA/camera ordering, no `curl` guard) is a transitional state, not a separate desired value; the catalog holds only the final form. Flags that are individually load-bearing and must not be lost in a rewrite: `--ozone-platform=wayland` (X11 default fails silently under labwc), `--user-data-dir=/tmp/framelink-chromium` (tmpfs profile — no stale `SingletonLock` after a power cut), `--auto-accept-camera-and-microphone-capture` (**must not** be combined with `--use-fake-ui-for-media-stream`: silent startup crash on this build), `--enable-features=UsePipeWireCamera`, `--autoplay-policy=no-user-gesture-required`, `--disable-background-timer-throttling`, `--disable-renderer-backgrounding`. The `rm -rf /tmp/framelink-chromium` pre-start is what makes an app update actually reach the browser; the portal camera permission survives it because it lives in `~/.local/share/flatpak/db`.

**`unit.chromium-kiosk.enabled`**

- **From** — [guide 5 step 5](../docs/5-kiosk-base.md#5-create-the-chromium-systemd-user-service)
- **Sets** — `default.target.wants/chromium-kiosk.service` symlink present in the user manager.
- **Observe** — `systemctl --user is-enabled chromium-kiosk.service` → `enabled`.
- **Verify** — identical.
- **dependsOn** — `unit.chromium-kiosk.content`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Separate from content per [§2.2](../version2.md): a unit can be byte-perfect and not enabled. Note that in v1 the *start* comes from labwc's autostart, not from the enablement — both paths exist and either alone is sufficient, which is a redundancy worth preserving deliberately rather than by accident.

**`labwc.autostart.content`**

- **From** — [guide 5 step 6](../docs/5-kiosk-base.md#6-create-the-labwc-autostart)
- **Sets** — `~/.config/labwc/autostart` containing `wlr-randr --output DSI-2 --transform 270` and `systemctl --user start chromium-kiosk.service &`.
- **Observe** — SHA-256 of the file (v1 reference size: 91 bytes).
- **Verify** — identical.
- **dependsOn** — `pkg.labwc`, `pkg.wlr-randr`
- **Value source** — output name fixed (`DSI-2` on this build); transform from fleet setting `display.rotation`.
- **Risk** — —

**`labwc.autostart.executable`**

- **From** — [guide 5 step 6](../docs/5-kiosk-base.md#6-create-the-labwc-autostart)
- **Sets** — owner-executable bit on `~/.config/labwc/autostart`.
- **Observe** — `stat -c '%a' ~/.config/labwc/autostart` (v1: `775`).
- **Verify** — identical.
- **dependsOn** — `labwc.autostart.content`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Its own resource because the guide itself names the distinct failure: labwc **silently ignores** a non-executable autostart. Perfect content plus a missing mode bit produces a frame that boots to a bare compositor with no rotation and no browser, and nothing logs a complaint.

**`display.dsi2-transform`**

- **From** — [guide 5 step 6](../docs/5-kiosk-base.md#6-create-the-labwc-autostart), verified by [step 9](../docs/5-kiosk-base.md#9-verify-the-kiosk-came-up)
- **Sets** — the `DSI-2` Wayland output carrying transform `270` (1280×800 landscape from an 800×1280 panel).
- **Observe** — `WAYLAND_DISPLAY=wayland-0 wlr-randr | grep Transform` → `Transform: 270`.
- **Verify** — identical.
- **dependsOn** — `labwc.autostart.content`, `labwc.autostart.executable`, `session.bash-profile-exec-labwc`
- **Value source** — fleet setting `display.rotation`.
- **Risk** — —
- **Notes** — Separate from the autostart file because it is independently actionable at runtime (`wlr-randr` one-shot) and because a correct autostart with `Transform: normal` is a distinct diagnosis — a renamed output, or `wlr-randr` failing before labwc finished bringing the output up. Requires a live Wayland session, which on a freshly booted frame always exists; if it does not, that is `session.bash-profile-exec-labwc` failing and this resource is `Blocked`.

**`labwc.rc-xml.touch-map`**

- **From** — [guide 5 step 7](../docs/5-kiosk-base.md#7-map-touch-input-to-the-rotated-dsi-output)
- **Sets** — `~/.config/labwc/rc.xml` containing `<touch mapToOutput="DSI-2"/>` inside `<labwc_config>`.
- **Observe** — SHA-256 / XML compare of `~/.config/labwc/rc.xml`.
- **Verify** — **differs from Observe.** The file is observable post-boot; whether taps actually land on the right pixels is only observable by a human touching the screen, or by injecting a synthetic touch event. wlroots offers no readback of the applied touch mapping. The agent verifies the file and must treat correct touch geometry as a human-confirmed checkpoint, not an automated one.
- **dependsOn** — `pkg.labwc`
- **Value source** — output name fixed (`DSI-2`).
- **Risk** — —
- **Notes** — A misspelled output identifier fails **silently** to the identity transform. This is also the resource behind [§2.7](../version2.md) point 4: the repair screen's "Reboot now" button is the one place v1's touch shield must not block input.

---

## Guide 6 — Camera

**`pkg.xdg-desktop-portal`** · **`pkg.xdg-desktop-portal-gtk`** · **`pkg.gstreamer1.0-tools`** · **`pkg.gstreamer1.0-plugins-base`** · **`pkg.gstreamer1.0-libcamera`** · **`pkg.gstreamer1.0-pipewire`**

- **From** — [guide 6 step 1](../docs/6-camera.md#1-install-the-camera-packages) (six resources, one per package)
- **Sets** — each apt package installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' <pkg>`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — `xdg-desktop-portal-gtk` is not cosmetic: the portal frontend only registers the Camera interface when a backend implementing the `Access` permission service is present.

**`pkg.libspa-0.2-libcamera.absent`**

- **From** — [guide 6 step 1](../docs/6-camera.md#1-install-the-camera-packages) ("just as important is what is *not* installed") and [step 4](../docs/6-camera.md#4-route-the-camera-through-a-dedicated-pipewire-node)
- **Sets** — apt package `libspa-0.2-libcamera` **not installed**.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' libspa-0.2-libcamera` → absent/not-installed.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Confirmed absent in the v1 reference (the inventory carries `libspa-0.2-modules`, which is a different package). The measured failure it causes: a camera node hard-capped near 30 fps that advertises no framerates, rejects sizes outside its own menu, and that Chromium cannot acquire above 720p. Absence is a real, actionable, independently verifiable state — hence a resource, not a note. `wireplumber.conf.camera-monitors-disabled` is the belt to this braces, for the case where a future dependency drags the plugin back in.

**`unit.xdg-desktop-portal.dropin-desktop`**

- **From** — [guide 6 step 2](../docs/6-camera.md#2-point-the-desktop-portal-at-the-labwc-session)
- **Sets** — `~/.config/systemd/user/xdg-desktop-portal.service.d/desktop.conf` containing `[Service]` / `Environment=XDG_CURRENT_DESKTOP=labwc`.
- **Observe** — `cat` the drop-in **and** `systemctl --user show xdg-desktop-portal.service -p Environment`.
- **Verify** — identical. `systemctl show` reads unit configuration, so it works whether or not the portal is currently running.
- **dependsOn** — `pkg.xdg-desktop-portal`, `boot.autologin.getty-tty1`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Without it the portal falls back to a degraded interface set with **no Camera interface**, because `exec labwc` never exports `XDG_CURRENT_DESKTOP` and `/usr/share/xdg-desktop-portal/labwc-portals.conf` is therefore never selected. A drop-in rather than a session-wide export is deliberate: it also covers the cold-boot D-Bus-activation path.

**`portal.permission-store.camera`**

- **From** — [guide 6 step 3](../docs/6-camera.md#3-pre-authorize-camera-access-for-the-kiosk)
- **Sets** — permission-store table `devices`, id `camera`, application id `""` → `["yes"]`. Backing file: `~/.local/share/flatpak/db/devices`.
- **Observe** — `busctl --user call org.freedesktop.impl.portal.PermissionStore /org/freedesktop/impl/portal/PermissionStore org.freedesktop.impl.portal.PermissionStore Lookup ss devices camera` → contains `"" 1 "yes"`.
- **Verify** — identical.
- **dependsOn** — `pkg.xdg-desktop-portal-gtk`, `boot.autologin.getty-tty1`
- **Value source** — fixed (`yes`).
- **Risk** — —
- **Notes** — Unset (not `no`) is what triggers the blocking GTK "Allow?" dialog that nobody on a wall-mounted frame will ever click — the fault presents as a permanently black self-view, not as an error. The empty application id is correct for an unsandboxed host Chromium. Lives outside the Chromium profile, so the per-start profile wipe does not touch it. Observing over D-Bus **may D-Bus-activate the permission store**; that is a read, and acceptable, but the agent should not treat "the service started" as evidence of anything.

**`wireplumber.conf.camera-monitors-disabled`**

- **From** — [guide 6 step 4](../docs/6-camera.md#4-route-the-camera-through-a-dedicated-pipewire-node)
- **Sets** — `~/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf` disabling `monitor.libcamera` and `monitor.v4l2` in the `main` profile.
- **Observe** — SHA-256 of the file **and** `wpctl status` showing no camera under the Video section's `Sources:` other than `FrameLinkCam`.
- **Verify** — identical.
- **dependsOn** — `pkg.wireplumber`, `boot.autologin.getty-tty1`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — One resource, two keys, by sub-rule 1: a single file write sets both, and a content compare already names which key drifted. The two failure signatures are still worth recording separately for telemetry — `monitor.libcamera` on gives you the crippled stock node; `monitor.v4l2` on surfaces the Pi's raw CFE/ISP pipeline stages as bogus cameras that **hang Chromium while it probes them**. The `99-` prefix is load-bearing (last fragment wins). The ALSA monitor is deliberately unmentioned, so audio is untouched. Watch for schema drift across WirePlumber majors: the `wireplumber.profiles` form is 0.5.x; the frame runs 0.5.8.

**`unit.framelink-camera.content`**

- **From** — [guide 6 step 5](../docs/6-camera.md#5-run-the-camera-node-service)
- **Sets** — `~/.config/systemd/user/framelink-camera.service`: `After=pipewire.service`, `Type=simple`, the single-line `gst-launch-1.0` pipeline, `Restart=always`, `RestartSec=3`, `WantedBy=default.target`.
- **Observe** — SHA-256 of the unit file. v1 reference hash: `a2c9ef326c8d53a7bf17086e786876b447a3c385e088948a19ca23c5b1e75e3e`.
- **Verify** — identical.
- **dependsOn** — `pkg.gstreamer1.0-tools`, `pkg.gstreamer1.0-plugins-base`, `pkg.gstreamer1.0-libcamera`, `pkg.gstreamer1.0-pipewire`, `boot.autologin.getty-tty1`
- **Value source** — fixed. Resolution/framerate could become fleet settings but are hardware-tuned, not preferences.
- **Risk** — —
- **Notes** — Every element of the pipeline is measured, not chosen: `width=1920,height=1080` forces the IMX708's **full-field-of-view 2304×1296 sensor mode**, which the ISP then scales in hardware — asking for 900 lines or fewer selects a cropped mode that behaves like a ~1.5× zoom, and scaling in software was measured at a ~51 fps single-thread ceiling. `queue max-size-buffers=4 leaky=downstream` drops old frames rather than back-pressuring the sensor. `pipewiresink mode=provide sync=false` publishes a standalone node; the `stream-properties` (`media.class=Video/Source`, `media.role=Camera`, `node.name=framelink-cam`, `node.description=FrameLinkCam`) are what make PipeWire and the portal treat it as a camera.

**`unit.framelink-camera.enabled`**

- **From** — [guide 6 step 5](../docs/6-camera.md#5-run-the-camera-node-service)
- **Sets** — `default.target.wants/framelink-camera.service` symlink present.
- **Observe** — `systemctl --user is-enabled framelink-camera.service` → `enabled`.
- **Verify** — identical.
- **dependsOn** — `unit.framelink-camera.content`
- **Value source** — fixed.
- **Risk** — —

**`camera.pipewire-node.framelink-cam`**

- **From** — [guide 6 step 5](../docs/6-camera.md#5-run-the-camera-node-service) LOOK FOR and CHECKPOINT
- **Sets** — exactly one entry under the Video section's `Sources:` in PipeWire, named `FrameLinkCam`, and nothing else camera-like anywhere.
- **Observe** — `wpctl status | sed -n '/^Video/,/^$/p'`.
- **Verify** — identical.
- **dependsOn** — `unit.framelink-camera.enabled`, `wireplumber.conf.camera-monitors-disabled`
- **Value source** — derived.
- **Risk** — —
- **Notes** — **This resource exists specifically because the unit lies.** Guides 6 and 11 both record the measured bug: `pipewiresink` in PipeWire 1.4.x can be left permanently broken by an abrupt consumer disconnect, and **the service keeps reporting `active` while the camera is dead**. Unit-active and node-present are therefore different diagnoses and must be separately observable. Upstream fixed it in PipeWire 1.6.0; when the OS ships that, this resource stays (it is still the right assertion) but the per-call restart behaviour inherited from guide 11 can be retired. If an `imx708` device or extra V4L2 sources appear alongside it, the WirePlumber fragment did not load.

**`boot.config.camera-auto-detect`**

- **From** — [guide 6 step 5](../docs/6-camera.md#5-run-the-camera-node-service) LOOK FOR ("recheck … the `camera_auto_detect=1` line in `/boot/firmware/config.txt`"); v1 inventory `BOOT_CONFIG`
- **Sets** — `camera_auto_detect=1` present in `/boot/firmware/config.txt`.
- **Observe** — `grep -c '^camera_auto_detect=1$' /boot/firmware/config.txt` **and** `libcamera` enumerating the IMX708 (for example via the camera service's own journal on a freshly booted frame).
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — **brick-capable** (`config.txt`).
- **Notes** — Stock-image default, not written by any guide, but the guide names it as the thing to check when the camera is missing — so it is a real resource with a real Act (restore the line). See open question 8 on the wider set of stock `config.txt` lines.

**`portal.camera-interface-published`**

- **From** — [guide 6 step 6](../docs/6-camera.md#6-confirm-the-camera-portal-is-on-the-session-bus)
- **Sets** — `org.freedesktop.portal.Camera` published at `/org/freedesktop/portal/desktop` on the session bus.
- **Observe** — `busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop | grep -i camera` → one `interface` line.
- **Verify** — identical.
- **dependsOn** — `unit.xdg-desktop-portal.dropin-desktop`, `pkg.xdg-desktop-portal-gtk`
- **Value source** — derived.
- **Risk** — —
- **Notes** — Separate from the drop-in because a correct drop-in with a missing interface is a real, distinct fault (backend not installed, portal started before the drop-in was read). **Caveat the implementer must know:** the portal is D-Bus-activated, so on a frame that has not run a call since boot it is legitimately `inactive`, and `busctl introspect` **starts it as a side effect of observing**. That is an acceptable read, but it means "the portal is running" is never evidence — only the interface list is. Act is `systemctl --user restart xdg-desktop-portal`.

**`unit.chromium-kiosk.running-matches-content`**

- **From** — [guide 6 step 7](../docs/6-camera.md#7-confirm-chromium-uses-the-pipewire-camera-path); the same check appears in [guide 10 step 4](../docs/10-spa.md#4-point-the-kiosk-browser-at-the-app) LOOK FOR for the URL
- **Sets** — the running Chromium's command line carrying the flags and URL of the current `unit.chromium-kiosk.content`, at minimum `--enable-features=UsePipeWireCamera` and the configured origin.
- **Observe** — `pgrep -a chromium` and compare against the unit's `ExecStart`.
- **Verify** — identical.
- **dependsOn** — `unit.chromium-kiosk.content`, `unit.chromium-kiosk.enabled`
- **Value source** — derived.
- **Risk** — —
- **Notes** — Its own resource because "unit file correct, running process stale" is the single most common post-edit drift, and the guide states the principle outright: *the command line is the authoritative truth — if the flag is not here, it is not in effect, whatever a config file says.* Act is `systemctl --user daemon-reload && systemctl --user restart chromium-kiosk.service`. **Comparison caveat:** `rpi-chromium-mods` injects flags from `/etc/chromium.d/`, so the running command line is a legitimate **superset** of `ExecStart`; the compare must be containment, not equality.

---

## Guide 7 — LiveKit server

**Zero device resources.** See the "does not become a device resource" section — guide 7 becomes the
Fleet Manager's bundled LiveKit per [§3.7](../version2.md). The values it produced (`livekitUrl`,
API secret, room, per-frame token) survive as fleet settings consumed by `app.config.*` in guide 10.

---

## Guide 8 — WebRTC hardware validation

**`pkg.grim`**

- **From** — [guide 8 step 7](../docs/8-webrtc-validation.md#7-run-the-four-hour-soak-test) (offered as optional there); promoted to required by [§3.6](../version2.md), which allowlists **screenshot** as one of exactly two remote diagnostics
- **Sets** — apt package `grim` installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' grim`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Present in the v1 reference (`grim 1.4.0+ds-2+b2`). Capture requires the session environment: `WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 grim <path>`. The rest of guide 8 is a one-time hardware go/no-go and contributes no device state.

---

## Guide 9 — Immich Kiosk

Docker is removed from the frame entirely ([§2.1](../version2.md)); Immich Kiosk stays as a pinned
upstream binary supervised as a child process of the agent ([decisions 40 and 41](../version2.md)).
The resources below are the v2 shape of what guide 9's Compose file configured.

**`kiosk.binary.pinned-release`**

- **From** — [guide 9 step 3](../docs/9-immich-kiosk.md#3-start-the-immich-kiosk-container) (image pin `0.39.3`), restated by [§2.1](../version2.md) (`immich-kiosk_Linux_arm64.tar.gz`, ~7.4 MB, static Go, AGPL-3.0)
- **Sets** — the pinned Immich Kiosk binary present at a fixed path under `/var/lib/fl-agent`, SHA-256 matching the catalog's pinned checksum, executable.
- **Observe** — file present, `sha256sum` matches, `<binary> --version`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — version fixed by the catalog; the fetch origin is upstream by default with a Fleet-Manager mirror as a later operator setting.
- **Risk** — —
- **Notes** — Fetching rather than redistributing keeps AGPL source-offer obligations off this project and off every self-hoster. v1 ran `0.39.3`; [§6.2](../version2.md) records `v0.42.0` as the version verified 2026-08-15 — the catalog pin is a release decision, not a copy of v1.

**`kiosk.offline-cache.dir`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration)
- **Sets** — the offline-assets directory exists and is writable by the kiosk process.
- **Observe** — `test -d` and a write probe as the kiosk's uid.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — path fixed by the catalog.
- **Risk** — —
- **Notes** — The v1 `chown -R 65532:65532` is a **Docker artifact** (the container's non-root uid) and does not carry over; under v2 the ownership requirement is whatever uid the agent runs the child as. Do not transcribe `65532`.

**`kiosk.config.immich-url`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`KIOSK_IMMICH_URL`)
- **Sets** — the Immich server URL passed to the kiosk child process.
- **Observe** — the agent's persisted desired-value store under `/var/lib/fl-agent`, cross-checked against the running child's `/proc/<pid>/environ`.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — fleet setting `immich.serverUrl` (collected at Fleet Manager first run, [§3.2](../version2.md)).
- **Risk** — —

**`kiosk.config.immich-api-key`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`KIOSK_IMMICH_API_KEY`)
- **Sets** — the Immich read-only API key passed to the kiosk child process.
- **Observe** — presence and fingerprint (never the value) in the agent's root-only store; liveness confirmed by the kiosk answering `200` rather than `401`/`403`.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — fleet setting `immich.apiKey`. **Secret** — root-only file per [§2.9](../version2.md), never in logs, never in telemetry.
- **Risk** — —
- **Notes** — Its own resource because a wrong key and a wrong URL produce the *same* visible symptom (no photos) with different fixes — the textbook case for the granularity rule.

**`kiosk.config.offline-mode-enabled`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`KIOSK_OFFLINE_MODE_ENABLED: "true"`)
- **Sets** — offline caching enabled on the kiosk child.
- **Observe** — as `kiosk.config.immich-url`.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — fleet setting `slideshow.offlineMode` (default `true`).
- **Risk** — —
- **Notes** — Two settings cooperate for offline operation and only one of them is here: this one makes Kiosk **download and cache**; `use_offline_mode=true` inside `app.config.immich-kiosk-url` makes Kiosk **serve from that cache**. Either alone leaves the frame blank when Immich is unreachable — which [§2.6](../version2.md) says must never happen in someone else's house.

**`kiosk.config.offline-asset-count`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS: "200"`)
- **Sets** — the offline cache size cap.
- **Observe** — as above.
- **Verify** — identical.
- **dependsOn** — `kiosk.config.offline-mode-enabled`
- **Value source** — fleet setting `slideshow.offlineAssetCount` (default `200`).
- **Risk** — —

**`kiosk.listen-address`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`ports: "127.0.0.1:3000:3000"`)
- **Sets** — the kiosk child listening on `127.0.0.1:3000` and nowhere else.
- **Observe** — `ss -tlnp` shows a LISTEN socket on `127.0.0.1:3000` owned by the kiosk process, and no `0.0.0.0`/`::` binding.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — fixed (must agree with `app.config.immich-kiosk-url`).
- **Risk** — —
- **Notes** — Loopback-only is a security property, not a convenience: the slideshow must be reachable by the frame's own browser and by nothing on the network.

**`kiosk.process.supervised`**

- **From** — [guide 9 steps 3–4](../docs/9-immich-kiosk.md#3-start-the-immich-kiosk-container) (the `restart: always` policy and the `HTTP 200` check)
- **Sets** — the kiosk child process running under the agent's supervision and answering `HTTP 200`.
- **Observe** — child process alive **and** `curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:3000` → `200`.
- **Verify** — identical.
- **dependsOn** — `kiosk.listen-address`, `kiosk.config.immich-url`, `kiosk.config.immich-api-key`
- **Value source** — derived.
- **Risk** — —
- **Notes** — Two observations, one diagnosis boundary: "process alive" and "answering" are folded here because the agent owns the process lifetime and its response to either being false is the same (restart the child). The failure this replaces is the whole reason Docker leaves the frame — a dead slideshow port drove the browser-renderer leak measured at ~50 MB/min, ending in an OOM kill.

---

## Guide 10 — Kiosk SPA

The git checkout and the `busybox httpd` service are superseded ([§2.1](../version2.md): the agent
embeds and serves the app). What survives is the app's **configuration**, which becomes
Fleet-Manager-supplied values, and the **local origin** the agent must publish.

**`app.http.local-origin`**

- **From** — [guide 10 step 3](../docs/10-spa.md#3-serve-the-app-locally) (v1: `busybox httpd -f -p 127.0.0.1:8888 -h ~/FrameLink/app`)
- **Sets** — the agent's embedded HTTP server answering on `127.0.0.1:8888`, serving the product app and the repair screen from one origin.
- **Observe** — `ss -tlnp` shows LISTEN on `127.0.0.1:8888` owned by `fl-agent`, **and** `curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:8888/` → `200`.
- **Verify** — identical.
- **dependsOn** — `agent.version`
- **Value source** — port fixed by the catalog (must agree with the kiosk unit's URL and its `curl` readiness guard).
- **Risk** — —
- **Notes** — [§2.7](../version2.md) requires the repair screen and the product to share **one local origin**; that is why this is a single resource rather than two servers. In v1 this was `framelink-spa.service` (unit hash `dd30426f925a258476960ed9aabf425417e9ba42436a0911e2b5fd80473551c0`) — both its content and its enablement disappear with the embedded app.

**`app.config.identity`**

- **From** — [guide 10 step 2](../docs/10-spa.md#2-create-the-app-configuration); per-device per [guide 13 step 5](../docs/13-multi-device-deploy.md#5-give-the-clone-its-own-app-identity)
- **Sets** — the frame's LiveKit participant identity (v1 example `framelink-douwe`).
- **Observe** — the agent's persisted desired value under `/var/lib/fl-agent`, cross-checked against the running app's reported configuration over the local channel.
- **Verify** — identical.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `call.identity`, issued at adoption. Under [§3.3](../version2.md) the *device* identity is the keypair fingerprint; this is the human/room-facing name bound to it.
- **Risk** — —
- **Notes** — Must be unique per unit. Two frames sharing an identity are treated as one device by LiveKit and calls misbehave — a fleet-wide fault with no local symptom.

**`app.config.room`**

- **From** — [guide 10 step 2](../docs/10-spa.md#2-create-the-app-configuration)
- **Sets** — the LiveKit room every household device joins (v1: `family`).
- **Observe** — as above.
- **Verify** — identical.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `call.room` ([§3.4](../version2.md): room is per-device, which is what makes group calling reachable by configuration alone).
- **Risk** — —

**`app.config.livekit-url`**

- **From** — [guide 10 step 2](../docs/10-spa.md#2-create-the-app-configuration)
- **Sets** — the LiveKit WebSocket address (v1: `ws://10.20.30.250:7880`).
- **Observe** — as above; liveness by the app's reported connection state.
- **Verify** — identical.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `call.livekitUrl`, supplied by the Fleet Manager, which now owns the LiveKit server ([§3.7](../version2.md)).
- **Risk** — —
- **Notes** — Guide 13 step 10 records the failure this causes across households: a frame that shows its slideshow but never joins calls is almost always a `livekitUrl` reachable only from the build network.

**`app.config.livekit-token`**

- **From** — [guide 10 step 2](../docs/10-spa.md#2-create-the-app-configuration); minted in [guide 7 step 6](../docs/7-livekit-server.md#6-mint-a-long-lived-token-for-the-frame)
- **Sets** — a valid LiveKit access token for this identity and room.
- **Observe** — token present in the agent's root-only store **and** not expired (decode `exp` without verifying the signature) **and** the app's last connection outcome.
- **Verify** — identical.
- **dependsOn** — `app.config.identity`, `app.config.room`, `app.config.livekit-url`
- **Value source** — fleet setting `call.token`, minted at adoption and rotatable at will.
- **Risk** — —
- **Notes** — **Secret.** This resource is the direct descendant of the July-23 expiry post-mortem: a token that silently aged out took the frame from working to degrading on every boot with nothing on screen to say why. v1's answer was a ten-year token; v2's is better — the Fleet Manager mints and rotates, and [§3.7](../version2.md) calls that "permanently retiring the credential-expiry failure class". The expiry check in Observe is what makes it visible *before* it bites.

**`app.config.immich-kiosk-url`**

- **From** — [guide 10 step 2](../docs/10-spa.md#2-create-the-app-configuration)
- **Sets** — the slideshow URL the app embeds, including its full display query string. v1 value: `http://127.0.0.1:3000/?disable_ui=true&hide_cursor=true&disable_navigation=true&frameless=true&image_fit=cover&transition=fade&duration=30&background_blur=false&show_more_info=false&use_offline_mode=true`.
- **Observe** — as `app.config.identity`.
- **Verify** — identical.
- **dependsOn** — `kiosk.listen-address`
- **Value source** — base URL fixed; `duration` and album selection are fleet settings (`slideshow.interval`, `slideshow.album`) per [§3.4](../version2.md).
- **Risk** — —
- **Notes** — `use_offline_mode=true` is the serve-half of the offline pair (see `kiosk.config.offline-mode-enabled`). The v1 inventory's `APP_CONFIG` shows this parameter **missing** on the running frame while `config.example.json` carries it — a live drift in the parity reference, worth resolving before parity is declared.

---

## Guide 11 — GPIO button daemon

The Python daemon, its three apt packages, and its user unit are FrameLink code and therefore move
inside the agent binary ([§2.1](../version2.md)). What remains as device state is the GPIO line
itself and the group membership that reaches it.

**`gpio.button.line`**

- **From** — [guide 11 step 2](../docs/11-gpio-button.md#2-wire-the-button-and-set-its-pin) and [step 3](../docs/11-gpio-button.md#3-run-the-daemon-as-a-service)
- **Sets** — BCM line 17 (physical pin 11) claimed by the agent as an input with the internal pull-up enabled and a 50 ms debounce.
- **Observe** — `gpioinfo` (from the stock `gpiod` package) showing the line requested by the agent's consumer name with `pull-up` bias; the agent additionally exposes its configured pin in telemetry.
- **Verify** — **differs from Observe.** The claim and bias are observable on a freshly booted frame; that the *button* is wired to that line is only provable by a physical press. Guide 11 makes exactly this split — step 4's `SIGUSR1` simulation exercises everything except the wire, and step 5 is the human step that closes it. The agent can verify the line; the wiring is a human-confirmed checkpoint.
- **dependsOn** — `agent.version`, `user.framelink.supplementary-groups`
- **Value source** — fleet setting `button.gpioPin` (default `17`).
- **Risk** — —
- **Notes** — Kept as one resource because claim, bias, and pin number are a single line-request operation and cannot be acted on independently — but the two failure signatures are distinct and both visible in `gpioinfo`: wrong pin (line 17 shows unused) versus contended line (line 17 shows a different consumer). v1's daemon used `gpiozero` with the `lgpio` backend; its most dangerous property is worth carrying into the v2 implementation as a test: **without `python3-lgpio`, gpiozero silently falls back to a mock pin factory** — the daemon starts cleanly, reports healthy, and the button simply never fires. Whatever the v2 GPIO path is, it must fail loudly when the real hardware backend is unavailable.

**`user.framelink.supplementary-groups`**

- **From** — [guide 11 step 3](../docs/11-gpio-button.md#3-run-the-daemon-as-a-service) (`gpio`), [guide 9 step 1](../docs/9-immich-kiosk.md#1-install-docker-engine) (`docker`, superseded); v1 inventory `USERS_GROUPS`
- **Sets** — the `framelink` user's supplementary group set.
- **Observe** — `id framelink`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog.
- **Risk** — —
- **Notes** — v1 parity set: `adm dialout cdrom sudo audio video plugdev games users netdev input render spi i2c gpio docker`. **`docker` must be dropped** — it disappears with Docker, and a naive state-diff against the frozen v1 reference would otherwise demand it forever. Most of the rest is the stock `userconf-pi` set. Group membership only takes effect in a new login session, which for this frame means a reboot — consistent with [§2.4](../version2.md) anyway.

---

## Guide 12 — systemd services and reliability hardening

**`mount.tmp.tmpfs`**

- **From** — [guide 12 step 5](../docs/12-systemd-and-reliability.md#5-cut-sd-card-writes-to-make-the-card-last)
- **Sets** — `/tmp` backed by tmpfs.
- **Observe** — `findmnt -n -t tmpfs /tmp`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed (present by default on Trixie).
- **Risk** — —
- **Notes** — **Parity trap.** The v1 reference has `/tmp` on tmpfs at `size=1029504k` (~1006 MB) — systemd's default of half of RAM, meaning the guide's `/etc/fstab` fallback **never fired**. If an agent takes that fallback branch it will mount `/tmp` at **100 MB**, and Chromium's entire profile lives in `/tmp/framelink-chromium`. Same predicate, radically different frame. The agent should assert "tmpfs, and at least N MB", not merely "tmpfs", and should prefer a systemd `tmp.mount` drop-in over an `/etc/fstab` line that competes with the fstab generator.

**`journal.storage-persistent`**

- **From** — [guide 12 step 5](../docs/12-systemd-and-reliability.md#5-cut-sd-card-writes-to-make-the-card-last)
- **Sets** — `/etc/systemd/journald.conf.d/persistent.conf` containing `[Journal]` / `Storage=persistent` / `SystemMaxUse=64M`.
- **Observe** — SHA-256 of the drop-in **and** `/var/log/journal/<machine-id>/` exists and holds journal files **and** `journalctl --disk-usage` under the cap.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — `SystemMaxUse` is a fleet setting `logging.journalMaxUse` (default `64M`); `Storage=persistent` is fixed.
- **Risk** — —
- **Notes** — One resource, two directives (sub-rule 1) — but the `/var/log/journal` directory check is folded into Observe deliberately, because `Storage=persistent` with a missing directory silently stays volatile. This setting is the reason the August 2026 leak-and-watchdog-reset chain became diagnosable at all; a volatile journal made days of failures leave no evidence. 64 MB is one to two weeks of this frame's logs.

**`swap.no-file-backed`**

- **From** — [guide 12 step 5](../docs/12-systemd-and-reliability.md#5-cut-sd-card-writes-to-make-the-card-last)
- **Sets** — no file-backed swap active; `dphys-swapfile` disabled and stopped if present.
- **Observe** — `swapon --show` lists only `/dev/zram0` (or nothing); `systemctl is-enabled dphys-swapfile` is `disabled`/`not-found`.
- **Verify** — identical.
- **dependsOn** — `swap.zram-active`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — `dphys-swapfile` is **not installed** in the v1 reference, so the guard is a no-op today; it exists for images carrying the older Raspberry Pi OS swap mechanism. Kept as a resource because the negative assertion is what protects the SD card, and it is cheap.

**`pkg.unattended-upgrades`**

- **From** — [guide 12 step 6](../docs/12-systemd-and-reliability.md#6-turn-on-unattended-security-updates)
- **Sets** — apt package `unattended-upgrades` installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status}\n' unattended-upgrades`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed; on/off is a fleet setting per [Appendix B item 4](../version2.md).
- **Risk** — —
- **Notes** — **Not present in the v1 reference.** Guide 12 step 6 was never applied to the frame that defines parity, so this resource has no v1 counterpart to diff against. See open question 10.

**`apt.auto-upgrades-enabled`**

- **From** — [guide 12 step 6](../docs/12-systemd-and-reliability.md#6-turn-on-unattended-security-updates)
- **Sets** — `/etc/apt/apt.conf.d/20auto-upgrades` with `APT::Periodic::Update-Package-Lists "1";` and `APT::Periodic::Unattended-Upgrade "1";`.
- **Observe** — `apt-config dump | grep -i 'APT::Periodic'` (reads the effective merged value, not just this file).
- **Verify** — identical.
- **dependsOn** — `pkg.unattended-upgrades`
- **Value source** — fleet setting `updates.osSecurityAuto` (default on).
- **Risk** — —
- **Notes** — The guide reaches this file through `dpkg-reconfigure -plow unattended-upgrades`, which is **interactive** — a full-screen dialog. The agent cannot use it and must write the file (or preseed debconf) directly. Observing through `apt-config dump` rather than `cat` is deliberate: the effective value is what matters and other files in `apt.conf.d` can override.

**`apt.unattended-upgrades.allowed-origins`**

- **From** — [guide 12 step 6](../docs/12-systemd-and-reliability.md#6-turn-on-unattended-security-updates) TECHNICAL EXPLANATION (security-only policy), required as a resource by [Appendix B item 4](../version2.md) ("expressed as a reconciled resource so the policy is visible and centrally changeable")
- **Sets** — the `Unattended-Upgrade::Origins-Pattern` / `Allowed-Origins` policy restricted to security updates.
- **Observe** — `apt-config dump | grep -i 'Unattended-Upgrade::'`, plus `unattended-upgrade --dry-run -d` as a policy readback.
- **Verify** — identical.
- **dependsOn** — `pkg.unattended-upgrades`
- **Value source** — fleet setting `updates.osUpgradePolicy`.
- **Risk** — —
- **Notes** — Untouched by the guide (Debian's default is already security-only), but Appendix B item 4 explicitly calls for it to be a visible, centrally changeable resource rather than an inherited default. **Interaction worth flagging:** with auto-upgrades on, `raspi-firmware` and kernel packages can rewrite `/boot/firmware/config.txt` and `cmdline.txt` unattended — the security-update channel is a live drift source pointed straight at the brick-capable resources.

**`unit.cpu-performance.content`**

- **From** — [guide 12 step 7](../docs/12-systemd-and-reliability.md#7-pin-the-cpu-governor-to-performance)
- **Sets** — `/etc/systemd/system/cpu-performance.service`: `After=multi-user.target`, `Type=oneshot`, `ExecStart=/bin/sh -c 'echo performance | tee /sys/devices/system/cpu/cpufreq/policy*/scaling_governor'`, `WantedBy=multi-user.target`.
- **Observe** — SHA-256 of the unit file.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — A **system** unit, not a user unit — the only one of the frame's own units that is.

**`unit.cpu-performance.enabled`**

- **From** — [guide 12 step 7](../docs/12-systemd-and-reliability.md#7-pin-the-cpu-governor-to-performance)
- **Sets** — `multi-user.target.wants/cpu-performance.service` symlink present.
- **Observe** — `systemctl is-enabled cpu-performance.service` → `enabled`.
- **Verify** — identical.
- **dependsOn** — `unit.cpu-performance.content`
- **Value source** — fixed.
- **Risk** — —

**`cpu.governor.performance`**

- **From** — [guide 12 step 7](../docs/12-systemd-and-reliability.md#7-pin-the-cpu-governor-to-performance) LOOK FOR and CHECKPOINT
- **Sets** — every cpufreq policy's `scaling_governor` reading `performance` on a freshly booted frame.
- **Observe** — `cat /sys/devices/system/cpu/cpufreq/policy*/scaling_governor` — all must read `performance`.
- **Verify** — identical.
- **dependsOn** — `unit.cpu-performance.enabled`
- **Value source** — fleet setting `power.cpuGovernor` (default `performance`; mains-powered kiosk, no battery to protect).
- **Risk** — —
- **Notes** — **The archetype for the whole catalog.** [§2.4](../version2.md) cites this exact case: the `cpufreq.default_governor=performance` kernel parameter *landed in `/proc/cmdline`* and the governor still came up `ondemand`. Unit enabled and governor wrong is a real, observed state — which is why the setting and its effect are two resources and why every resource reboots. Observe all policies with the glob, not just `policy0`: a partial application is a distinct fault.

---

## Cross-guide and v2-mandated resources

Not extracted from guides 3–12, but required for a complete catalog. Provenance is named per entry.

**`agent.version`**

- **From** — [version2.md §2.8](../version2.md): "The applied version is an ordinary resource — the root of the DAG."
- **Sets** — the installed `fl-agent` binary matching the version served by the Fleet Manager (upgrade **or** downgrade).
- **Observe** — `fl-agent --version` against the version reported by the Fleet Manager's versionless update endpoint.
- **Verify** — identical.
- **dependsOn** — — (root)
- **Value source** — a function of the Fleet Manager's container version; not operator-settable per device beyond enable/disable of updates.
- **Risk** — —
- **Notes** — Reconciled hourly out-of-band, independent of the socket; the handshake only makes it immediate.

**`agent.keypair`**

- **From** — [§2.9 and §3.3](../version2.md)
- **Sets** — the device keypair present in a root-only file under `/var/lib/fl-agent`, generated on first boot; its public-key fingerprint is the immutable device id.
- **Observe** — key file present, correct mode/ownership, fingerprint stable across boots.
- **Verify** — identical.
- **dependsOn** — `agent.version`
- **Value source** — generated on device; never supplied.
- **Risk** — —
- **Notes** — Never in the repository, never in logs. Decommissioning erases it and returns the device to pending.

**`agent.adoption`**

- **From** — [decision 34](../version2.md) ("Adoption: a reconciled resource; an unadopted frame runs no product") and [§3.3](../version2.md)
- **Sets** — the device adopted by its Fleet Manager, holding issued identity, room, token and desired values.
- **Observe** — the adoption record and issued values present in `/var/lib/fl-agent`; last authoritative server answer was `adopted` rather than `pending`/`blocked`/`not-configured`.
- **Verify** — identical.
- **dependsOn** — `agent.keypair`
- **Value source** — Fleet Manager.
- **Risk** — —
- **Notes** — Gates every `app.config.*` resource. **Rejection is an answer; silence is not** — an unreachable server does not un-adopt a frame that was green when contact dropped.

**`identity.hostname`**

- **From** — set at flash time in [guide 2](../docs/2-sd-flash-first-boot.md); changed per unit in [guide 13 step 4](../docs/13-multi-device-deploy.md#4-give-the-clone-its-own-hostname); **trap recorded in [version2.md Appendix B item 1](../version2.md)**
- **Sets** — the system hostname (and the matching `/etc/hosts` entry), for example `framelink-douwe`.
- **Observe** — `hostnamectl --static` **and** the cloud-init source of truth on the boot partition (`/boot/firmware/meta-data` and `/boot/firmware/user-data`, plus any `/etc/cloud/cloud.cfg.d/*` drop-in) **and** `/etc/hosts`.
- **Verify** — identical, and only meaningful **after a reboot** — that is the entire point of the trap.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `device.hostname` (per-device override; the guide-2 pattern is `framelink-<recipient>`).
- **Risk** — **brick-adjacent** — the corrective write targets `/boot/firmware`, the same partition as the brick-capable resources, and shares their validate-and-back-up discipline.
- **Notes** — **Observed on the mule 2026-08-15:** the hostname is cloud-init managed on this image. `hostnamectl set-hostname` appears to succeed and is **silently reverted at the next boot**, and a `preserve_hostname: true` drop-in was **not** sufficient. The concrete mechanism is visible in the v1 kernel command line: `ds=nocloud;i=rpi-imager-1776005232619` — the NoCloud datasource, seeded from the boot partition by Raspberry Pi Imager, with `rpi-cloud-init-mods` and `cloud-init 25.2` installed and `cloud-init-local`/`cloud-init-main`/`cloud-init-network`/`cloud-config`/`cloud-final` all enabled. The resource must therefore act on **cloud-init's seed**, not on `hostnamectl`, and its Verify must be a post-reboot read. A write-only check would have marked this `InSync` while it was quietly wrong. Guide 13's `raspi-config nonint do_hostname` is subject to the same revert and cannot be transcribed as the Act.

**`system.timezone`**

- **From** — [guide 2 step 9](../docs/2-sd-flash-first-boot.md) (Imager localisation); required as a fleet setting by [§3.4](../version2.md)
- **Sets** — the system time zone.
- **Observe** — `timedatectl show -p Timezone --value` **and** the cloud-init seed's `timezone:` directive if present.
- **Verify** — identical, after a reboot.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `locale.timeZone`.
- **Risk** — brick-adjacent if the Act writes the boot-partition seed.
- **Notes** — **Same owner as the hostname.** cloud-init has a `timezone` module and Imager seeds it, so this is a first-class suspect for the same silent revert. Verify after a reboot; do not trust `timedatectl set-timezone` alone. Directly visible to the user — the 3 AM restart window and the slideshow both depend on local time.

**`system.locale`**

- **From** — [guide 2 step 9](../docs/2-sd-flash-first-boot.md); required as a fleet setting by [§3.4](../version2.md)
- **Sets** — system locale and keyboard layout.
- **Observe** — `localectl status`; `/etc/default/keyboard`; cloud-init seed.
- **Verify** — identical, after a reboot.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `locale.language` / `locale.keyboard`.
- **Risk** — —
- **Notes** — Same cloud-init suspicion as the time zone. `console-setup.service` and `keyboard-setup.service` are enabled in the v1 reference and re-apply keyboard configuration at boot — a second competing owner.

**`boot.cmdline.wifi-regdom`**

- **From** — v1 inventory `KERNEL_CMDLINE` (`cfg80211.ieee80211_regdom=NL`); seeded at flash time
- **Sets** — the 802.11 regulatory domain kernel parameter in `/boot/firmware/cmdline.txt`.
- **Observe** — `grep -o 'cfg80211.ieee80211_regdom=[A-Z]*' /boot/firmware/cmdline.txt` **and** `/proc/cmdline`; `iw reg get`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fleet setting `locale.wifiCountry`.
- **Risk** — **brick-capable** (`cmdline.txt`).
- **Notes** — Low operational importance on a wired frame, high parity importance: it is part of the single `cmdline.txt` line that `boot.cmdline.fbcon-rotate` also edits, so both resources write the same file and must not fight. Any `cmdline.txt` writer must be a single line-aware editor, not two independent appenders.

**`eeprom.config`**

- **From** — v1 inventory `EEPROM_CONFIG`; not set by any guide
- **Sets** — the Pi 5 bootloader EEPROM configuration: `BOOT_UART=1`, `POWER_OFF_ON_HALT=1`, `BOOT_ORDER=0xf461`.
- **Observe** — `rpi-eeprom-config`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog.
- **Risk** — **brick-capable** (EEPROM). Recovery is a card swap at best, a recovery-image flash at worst.
- **Notes** — Included because it is parity state that the state-diff harness will compare and because `rpi-eeprom-update.service` is **enabled** in the v1 reference — an autonomous owner that can flash a newer bootloader and change this configuration without anyone asking. `POWER_OFF_ON_HALT=1` matters for the smart-plug power-cycle harness in [§5.1](../version2.md). The v1 inventory captures the EEPROM *config* but not the bootloader *version*; see open question 13.

---

## Does not become a device resource

Each line names the guide content excluded and what supersedes it.

**Whole guides**

- **Guide 7, all six steps** (Docker on the server, `livekit.yaml` with a generated secret, `compose.yaml`, container start, workstation CLI install, ten-year token minting) — superseded by [§3.7](../version2.md): the Fleet Manager bundles `livekit-server`, generates its configuration, owns the API secret, supervises it as a child process, and mints per-device tokens at adoption. Guide 7 disappears; its outputs survive as the `call.*` fleet settings feeding `app.config.*`.
- **Guide 8, steps 1–8** (LiveKit CLI, `validation.html`, Python HTTP server, five simulated publishers, kiosk-URL swap, baseline snapshot, four-hour soak loop, evaluate-and-restore) — a one-time hardware go/no-go, not device state. Superseded by the M3 triple bar in [§5.1](../version2.md): state-diff, checkpoint assertions, validation battery. Only `pkg.grim` survives, promoted by the [§3.6](../version2.md) diagnostics allowlist. Note that step 5's `sed` into `chromium-kiosk.service` is precisely the **conflict drift** [§2.6](../version2.md) says must interrupt the product — under v2 that manipulation is not available and equivalent testing runs through maintenance mode.

**Docker and everything that exists because of it**

- **Guide 9 step 1** (`get.docker.com`, `usermod -aG docker`) — superseded by [§2.1](../version2.md): Docker is removed from the frame entirely.
- **Guide 9 step 2's Compose file, `chown 65532`, and step 3's `docker compose up -d`** — superseded by the agent supervising the Immich Kiosk binary as a child process; the settings survive as `kiosk.config.*`.
- **Guide 12 step 8, all three artifacts** (`daemon.json` with `live-restore`, `docker-selfheal.sh`, `docker-selfheal.service`) — superseded by Docker's removal, which deletes the whole corrupt-network-store failure class that began the August 2026 incident chain rather than repairing it.
- **v1 inventory items that follow Docker out:** the `docker.list` apt source; packages `docker-ce`, `docker-ce-cli`, `docker-ce-rootless-extras`, `docker-buildx-plugin`, `docker-compose-plugin`, `docker-model-plugin`, `containerd.io`; enabled units `docker.service`, `docker.socket`, `containerd.service`, `docker-selfheal.service`; the `docker` group membership; the `docker0` and `br-*` bridge interfaces; `~/immich-kiosk/`.

**The v1 SPA delivery**

- **Guide 10 step 1** (`apt install git`, `git clone` into `~/FrameLink`) — superseded by [§2.1](../version2.md): the app ships inside the agent binary, so the on-disk checkout is gone and cannot drift from the agent managing it.
- **Guide 10 step 3** (`framelink-spa.service`, `busybox httpd`) — superseded by `app.http.local-origin`, the agent's own embedded server on the same port.
- **Guide 10 step 2's `config.json` file** — superseded by Fleet-Manager-supplied values held in `/var/lib/fl-agent`; the five fields survive as the five `app.config.*` resources.
- **v1 inventory items that follow:** `~/FrameLink/` and its `app/config.json`, `config.json.expired-token.bak`.

**FrameLink code that moves inside the agent binary**

- **Guide 11 step 1** (`python3-gpiozero`, `python3-lgpio`, `python3-websockets`) and **step 3** (`framelink-gpio.service`, `framelink-gpio.py`) — superseded by agent-internal GPIO handling and the agent's own local channel. The WebSocket server on `127.0.0.1:8889` is an internal detail of the v1 split between daemon and SPA; with both inside one binary there is no port. Three behaviours from the daemon must be **reimplemented, not dropped**: camera-service restart after every call-end, the 90 s/300 s/15 s kiosk-liveness watchdog, and `SIGUSR1`-equivalent simulated press for testing.
- **Guide 12 step 2** (`~/chromium-watchdog.sh`) and **steps 3–4** (`chromium-watchdog.service`/`.timer`, `chromium-restart.service`/`.timer`) — superseded by agent supervision. The measured constants survive as fleet settings: tree RSS ceiling `1843200` kB, `MemAvailable` floor `358400` kB, five-minute interval, `OnCalendar=*-*-* 03:00:00` with `Persistent=true`. See open question 5 — v2 currently names no home for this behaviour.
- **v1 inventory items that follow:** user units `chromium-watchdog.service`/`.timer`, `chromium-restart.service`/`.timer`, `framelink-gpio.service`, `framelink-spa.service`; `~/chromium-watchdog.sh`.

**Verification-only steps (they become Observe/Verify implementations, not resources)**

- Guide 4 step 6 (mixer readback after reboot) and step 7 (round-trip mic recording); guide 5 step 1 (`swapon --show`) and step 9 (three kiosk checks); guide 6 step 6 and step 7; guide 9 step 4; guide 10 step 5; guide 11 steps 4 and 5; guide 12 step 1 and step 9. Every `sudo reboot` is the reboot discipline itself, not a resource.

**v1 artifacts present in the parity reference that are not in any guide and must not be replicated**

- `sshd-mute-monitor.service` and `/usr/local/sbin/sshd-mute-monitor.sh` — the unit's own description says *"FrameLink testbed: sshd mute monitor (diagnostics, remove at fresh-flash)"*. It is **enabled** in the frozen v1 reference, so a naive state-diff will demand it on every v2 frame. Exclude explicitly.
- Home-directory debris: `audio-test.sh`, `noise-test.sh`, `speech-test.sh`, `gen-noise.py`, `fw-v2.0.10.bin`, `xvf-commands.txt`, `testaudio/`, `~/xvf3800/`, and the `*.png` screenshots. Diagnostic residue from the build sessions.
- `~/.bash_history` and the `.lgd-nfy0` FIFO.

---

## Proposed dependency ordering

Topological, with brick-capable resources scheduled last per [§5.5](../version2.md). Phase boundaries
are for reading; the DAG is what the loop actually orders.

**One deliberate refinement of §5.5.** The brick-capable set is split by *recovery cost*, not by
category. `firmware.xvf3800.version` is a DFU flash that can brick the **mic array** — the Pi still
boots, the frame is still reachable, and recovery is a physical Safe-Mode reflash at the device. The
boot-partition and EEPROM writes can produce a device nothing remote can reach, which is the specific
risk §5.5 names. Ordering DFU ahead of the boot writes honours §5.5's intent while resolving a real
dependency: guide 4 states the mixer levels are validated against firmware 2.0.10, so
`audio.mixer.*` genuinely depends on the flash. See open question 2.

| # | Phase | Resources (in order) |
| ---: | --- | --- |
| 1–3 | **Agent roots** | `agent.version` · `agent.keypair` · `agent.adoption` |
| 4–20 | **Package set** | `pkg.labwc` · `pkg.chromium` · `pkg.wireplumber` · `pkg.pipewire-alsa` · `pkg.wlr-randr` · `pkg.xdg-desktop-portal` · `pkg.xdg-desktop-portal-gtk` · `pkg.gstreamer1.0-tools` · `pkg.gstreamer1.0-plugins-base` · `pkg.gstreamer1.0-libcamera` · `pkg.gstreamer1.0-pipewire` · `pkg.libspa-0.2-libcamera.absent` · `pkg.dfu-util` · `pkg.git` · `pkg.grim` · `pkg.unattended-upgrades` · `tool.xvf-host.installed` |
| 21–34 | **System configuration** | `system.timezone` · `system.locale` · `user.framelink.supplementary-groups` · `boot.autologin.getty-tty1` · `mount.tmp.tmpfs` · `journal.storage-persistent` · `swap.zram-active` · `swap.no-file-backed` · `apt.auto-upgrades-enabled` · `apt.unattended-upgrades.allowed-origins` · `audio.modprobe.snd-usb-audio-index` · `unit.cpu-performance.content` · `unit.cpu-performance.enabled` · `cpu.governor.performance` |
| 35–44 | **Session and kiosk stack** (front-loaded per §2.7) | `session.bash-profile-exec-labwc` · `labwc.autostart.content` · `labwc.autostart.executable` · `labwc.rc-xml.touch-map` · `display.dsi2-transform` · `unit.xdg-desktop-portal.dropin-desktop` · `app.http.local-origin` · `unit.chromium-kiosk.content` · `unit.chromium-kiosk.enabled` · `unit.chromium-kiosk.running-matches-content` |
| 45–50 | **Camera chain** | `wireplumber.conf.camera-monitors-disabled` · `unit.framelink-camera.content` · `unit.framelink-camera.enabled` · `portal.permission-store.camera` · `portal.camera-interface-published` · `camera.pipewire-node.framelink-cam` |
| 51 | **Array firmware** (brick-capable, hand-recoverable) | `firmware.xvf3800.version` |
| 52–58 | **Audio state** | `audio.xvf3800.gpo-x0d31-amp-enable` · `audio.mixer.pcm0-playback-switch` · `audio.mixer.pcm1-playback-switch` · `audio.mixer.pcm0-playback-volume` · `audio.mixer.pcm1-playback-volume` · `audio.mixer.headset-capture-volume` · `audio.alsa.stored-state` |
| 59–72 | **Product layer** | `kiosk.binary.pinned-release` · `kiosk.offline-cache.dir` · `kiosk.config.immich-url` · `kiosk.config.immich-api-key` · `kiosk.config.offline-mode-enabled` · `kiosk.config.offline-asset-count` · `kiosk.listen-address` · `kiosk.process.supervised` · `app.config.identity` · `app.config.room` · `app.config.livekit-url` · `app.config.livekit-token` · `app.config.immich-kiosk-url` · `gpio.button.line` |
| 73–79 | **Brick-capable, unbootable risk — last** | `identity.hostname` · `boot.config.camera-auto-detect` · `boot.config.dtoverlay-vc4-kms-v3d-noaudio` · `boot.config.dtoverlay-waveshare-panel` · `boot.cmdline.fbcon-rotate` · `boot.cmdline.wifi-regdom` · `eeprom.config` |

**Reboot cost, stated plainly.** [§2.4](../version2.md) mandates a reboot per resource with no
exceptions. At 79 resources and roughly 40–60 s per boot-and-verify cycle, a bare-metal provision
carries **75–80 minutes of reboot overhead alone**, before apt download time (~350 MB across the two
package steps). That is the deliberate cost of the rule, but it is worth having the number: a first
provision is a one-to-two-hour operation, and the console-stage narration in
[§2.7](../version2.md) is what makes it legible while it happens.

---

## Suspected "silently reverted" settings

The hostname trap is not unique. These are the settings whose write can appear to succeed while a
different owner puts them back — ranked by confidence. Every one of them can report `InSync` while
being wrong if Observe reads what was written instead of what is in force after a reboot.

**Confirmed on hardware**

1. **`identity.hostname` — cloud-init.** Recorded in [Appendix B item 1](../version2.md), observed on
   the mule 2026-08-15. `hostnamectl set-hostname` succeeds and is reverted at next boot; a
   `preserve_hostname: true` drop-in was not enough. Owner is the NoCloud datasource seeded from the
   boot partition (`ds=nocloud;i=rpi-imager-…` in the v1 kernel command line). Act on the seed.
2. **`cpu.governor.performance` — kernel-parameter route is ineffective.** Documented in guide 12
   step 7 and cited in [§2.4](../version2.md): `cpufreq.default_governor=performance` reaches
   `/proc/cmdline` and the governor still comes up `ondemand`. The oneshot unit is the only route
   that works, and the governor value must be read separately from the unit's state.

**Strongly suspected — same owner class, not yet re-tested**

3. **`system.timezone` and `system.locale` — cloud-init.** cloud-init has `timezone` and `locale`
   modules, Imager seeds both, and the same five cloud-init units are enabled. Treat exactly like the
   hostname: act on the seed, verify after a reboot. `console-setup.service` and
   `keyboard-setup.service` are a second boot-time owner of the keyboard half.
4. **`audio.mixer.*` — WirePlumber's device-state restore.** `alsa-restore.service` applies
   `/var/lib/alsa/asound.state` early in boot, then the user session starts and WirePlumber's
   `restore-device` policy applies **its own** stored per-device volume and route from
   `~/.local/state/wireplumber/`. Later writer wins. The mixer resources therefore have two owners,
   only one of which the guides configure — and the symptom (a quiet frame) is the same one the
   hidden `PCM,1` stage already produces, so it will be misattributed. **Recommendation:** observe
   the mixer *after the user session is up*, and treat WirePlumber's stored device state as either a
   resource in its own right or something the agent explicitly clears.
5. **Network configuration — cloud-init plus NetworkManager plus netplan.** The v1 image carries
   `cloud-init`, `rpi-cloud-init-mods`, `netplan.io`, `netplan-generator`, `python3-netplan` and
   NetworkManager, with `NetworkManager.service`, `NetworkManager-wait-online.service` and
   `wpa_supplicant.service` all enabled. cloud-init writes `/etc/netplan/50-cloud-init.yaml` at boot
   and netplan renders it into NetworkManager. Any hand-written network file — including guide 13
   step 9's `nmcli` profile — sits downstream of a generator that reruns every boot.
6. **`/etc/hosts` — raspi-config and cloud-init.** `raspi-config nonint do_hostname` rewrites
   `/etc/hostname` and `/etc/hosts` together; cloud-init's `manage_etc_hosts` can rewrite it again.
   Any hostname resource must own both files or it will half-apply.
7. **`/boot/firmware/config.txt` and `cmdline.txt` — package postinst plus unattended upgrades.**
   `raspi-firmware`, `raspberrypi-sys-mods` and the kernel packages carry hooks that regenerate these
   files. Turning on `apt.auto-upgrades-enabled` (guide 12 step 6) points an unattended writer
   straight at the brick-capable resources. This is a **new interaction between two guide steps that
   neither guide mentions**, and it is the most likely source of surprise drift on a long-running
   fleet.
8. **`eeprom.config` — `rpi-eeprom-update.service` is enabled.** An autonomous owner that can flash a
   newer bootloader at boot and change EEPROM configuration with nobody asking.

**Worth checking, lower confidence**

9. **`boot.autologin.getty-tty1` — raspi-config.** Nothing reverts it today, but any later
   `do_boot_behaviour` call rewrites or removes it, and the whole user-unit layer depends on it.
10. **`unit.chromium-kiosk.running-matches-content` — `/etc/chromium.d/` injection.** `rpi-chromium-mods`
    adds flags at launch, so the running command line is legitimately a superset of `ExecStart`.
    An equality compare here would report permanent false drift.
11. **`mount.tmp.tmpfs` — systemd's own `/tmp` handling versus an `/etc/fstab` line.** Two owners for
    one mount point, with different sizes (see the parity trap under that resource).
12. **`swap.zram-active` — `systemd-zram-generator`.** The generator regenerates the zram device from
    its own configuration at every boot; a `swapon`-level change would not survive.
13. **`portal.permission-store.camera` — portal package upgrades.** Low risk, but the permission lives
    in a binary database owned by another project; a schema migration is the plausible failure.

---

## Guide 13 — what becomes fleet-manager behaviour

Guide 13 is out of catalog scope. Mapping its ten steps:

| Step | v1 behaviour | v2 home |
| ---: | --- | --- |
| 1 | Rollout plan; four-service + container health sweep on the master | Fleet Manager device list with live per-resource status ([§3.5](../version2.md)); the sweep is the reconciler's Status roll-up |
| 2 | Capture `framelink-golden.img` from the master's card | **Deferred to v3** ([§8](../version2.md), SD image generation). v2 provisions from a stock image; there is no golden image and no cloned identity to strip |
| 3 | Flash the clone, boot one at a time to avoid hostname collision | Stock image plus [§4.3](../version2.md) discovery (install flag → boot file → mDNS). The one-at-a-time constraint disappears because identity is the keypair, generated per device |
| 4 | `raspi-config nonint do_hostname` + reboot | **Device resource** `identity.hostname`, value from fleet setting `device.hostname` — and subject to the cloud-init trap, so the guide's command is not the Act |
| 5 | Hand-edit `config.json` identity and token; restart the browser | Adoption issues identity, room, LiveKit token and desired values ([§3.3](../version2.md)); the five fields become `app.config.*` resources |
| 6 | `rpi-connect signout` / `signin` per clone | Fleet Manager reverse shell ([§3.6](../version2.md)) — outbound-initiated, audited, auto-closing. Keypair identity removes the inherited-registration problem entirely |
| 7 | Per-clone cold-boot verification | Checkpoint assertions inside each resource's Verify, surfaced as per-device status |
| 8 | Whole-fleet call plus overnight soak | Virtual agents for fleet behaviour ([§5.3](../version2.md)); real-frame soak stays a bench activity, with telemetry and offline alerting ([§3.5](../version2.md)) providing what its absence made invisible for days in August 2026 |
| 9 | `nmcli` profile for the destination household's WiFi | **Deferred to v3** ([§8](../version2.md), on-device Wi-Fi configuration). Until then it stays a pre-ship human step — and §8 states the consequence plainly: a router or password change strands a frame until someone is on site |
| 10 | Deliver, verify through Raspberry Pi Connect, first cross-household call | Presence is the socket ([§3.5](../version2.md)); verification is telemetry plus the screenshot/journal-tail allowlist ([§3.6](../version2.md)); cross-household media is [Appendix B item 2](../version2.md), deferred within v2 |

One guide-13 observation that is not a fleet-manager mapping: step 2 leaves SSH host keys and
`machine-id` explicitly unresolved for clones. v2's stock-image path dissolves the question — each
device generates its own on first boot (`regenerate_ssh_host_keys.service` is enabled) — but it should
be confirmed rather than assumed, because `machine-id` is the path component of `/var/log/journal/`.

---

## Open questions

Genuine ambiguities in the specification. Each carries the reading this catalog adopted; none is
settled by the guides.

1. **The display overlay is brick-capable but is also the precondition for any visible output.**
   [§5.5](../version2.md) schedules brick-capable resources last; [§2.7](../version2.md) requires
   console narration on `/dev/tty1` "from the first second of the first boot" and forbids blank
   screens. With `boot.config.dtoverlay-waveshare-panel` scheduled 76th, the frame provisions almost
   entirely with a dark panel and the operator sees nothing until the end.
   *Reading adopted:* keep the literal §5.5 ordering in the table above and flag the conflict. The
   recommended resolution is a narrow carve-out — schedule the two display resources first among the
   boot-partition writes, behind config validation, a backup of both files, and boot-count
   self-repair — which is a change to §5.5 and therefore an operator decision, not a catalog one.

2. **DFU firmware ordering versus the mixer values it validates.** Guide 4 states the volume settings
   are validated against firmware 2.0.10 and that 2.0.6-era firmware exposes the DAC volume path
   differently, so `audio.mixer.*` depends on `firmware.xvf3800.version` — which §5.5 wants last.
   *Reading adopted:* split brick-capable by recovery cost (mic-array brick keeps the Pi bootable and
   is hand-recoverable; boot-partition brick is not), and place DFU just ahead of the audio block.

3. **How does the agent obtain `xvf_host` and the firmware images?** Guide 4 gets both from a
   `git clone --depth 1` of a GitHub repository into `~/xvf3800`. [§2.1](../version2.md)'s "no
   supplemental program files, ever" is about the agent's own delivery, and Immich Kiosk is the
   explicit precedent for a pinned, checksum-verified upstream artifact — but `xvf_host` needs three
   sibling `.so` files and a fixed working directory, which is more than a single static binary.
   *Reading adopted:* treat it as the Immich Kiosk shape (pinned upstream fetch, checksum verified,
   under `/var/lib/fl-agent`), which makes `pkg.git` unnecessary. If the operator prefers the clone,
   `pkg.git` stays and `tool.xvf-host.installed` gains a git dependency.

4. **Where does browser and camera supervision live?** [§2.2](../version2.md)'s loop is
   level-triggered convergence of *declared state*. "Chromium's process tree exceeds 1.8 GB", "the
   SPA socket has been silent for 90 seconds", "the camera node wedged after a call" and "restart the
   browser every day at 03:00" are none of them drift of a declared setting, yet all four are
   load-bearing and measured. v2 names no home for them.
   *Reading adopted:* they are **supervision**, a second agent responsibility alongside
   reconciliation, with their constants exposed as fleet settings. They are excluded from the
   resource catalog on that basis. If instead they are meant to be resources, they need a status
   vocabulary that distinguishes "converging" from "supervising", because a browser restart is not
   drift and should not stop the product under [§2.6](../version2.md).

5. **Are agent-internal tunables resources?** Countdown duration, watchdog thresholds, the 03:00
   restart time, the button pin, backoff parameters. [§2.8](../version2.md) makes the applied *version*
   an ordinary resource, which argues yes by analogy; but a value the agent holds in memory has no
   independent drift surface, which argues no.
   *Reading adopted:* fleet settings, not resources — except where they have an observable on-device
   footprint, which is why `gpio.button.line` is a resource (the claimed GPIO line is visible in
   `gpioinfo`) and the watchdog thresholds are not.

6. **Does the frame still need a `framelink` user session at all?** Every kiosk-layer unit in v1 is a
   `--user` unit whose entire lifetime depends on the tty1 autologin — there is no
   `loginctl enable-linger` anywhere in the build. The v2 agent is a root systemd service. Keeping
   user units means the agent must reach into another user's session manager; moving them to system
   units means rewriting every unit and re-solving Wayland socket ownership.
   *Reading adopted:* keep the v1 user-session shape (it is the proven configuration and the parity
   target), with `boot.autologin.getty-tty1` as its root dependency — but note that
   `loginctl enable-linger framelink` would make the session independent of the tty and is a small,
   high-value hardening the guides never applied.

7. **Which stock `config.txt` / `cmdline.txt` lines must the catalog assert?** The v1 reference
   carries `dtparam=audio=on`, `display_auto_detect=1`, `auto_initramfs=1`, `max_framebuffers=2`,
   `disable_fw_kms_setup=1`, `arm_64bit=1`, `disable_overscan=1`, `arm_boost=1`, `otg_mode=1` under
   `[cm4]`, `dtoverlay=dwc2,dr_mode=host` under `[cm5]`, plus the `console=`, `root=`, `fsck.repair`
   and `rootwait` cmdline tokens. None is written by any guide; all are compared by the state-diff
   harness and all are rewritable by package postinst hooks.
   *Reading adopted:* catalog only the lines a guide names — the two overlays, `camera_auto_detect`,
   `fbcon=rotate`, `cfg80211.ieee80211_regdom`. The remainder should be a single
   `boot.config.stock-baseline` resource asserting the stock file's shape, added when the state-diff
   harness first reports on it, rather than fifteen individually reasoned resources.

8. **`Headset,0` versus `Headset,1`.** `PCM,0`/`PCM,1` turned out to be two real gain stages with
   different defaults, worth ~18 dB. Whether the capture side is the same shape is unknown; the
   guides never set either, and the v1 inventory shows both at `60`.
   *Reading adopted:* one resource covering both indices. Split it if measurement shows two stages —
   the mic-side equivalent of the `PCM,1` trap would be a frame nobody can hear, reported as
   "the call is broken".

9. **`unattended-upgrades` is absent from the parity reference.** Guide 12 step 6 was never applied to
   the frame that defines "at parity", so three of this catalog's resources
   (`pkg.unattended-upgrades`, `apt.auto-upgrades-enabled`, `apt.unattended-upgrades.allowed-origins`)
   have nothing to diff against and the state-diff harness will report them as additions.
   Compounding it, the guide's own route is `dpkg-reconfigure -plow`, which is **interactive** and
   unusable by the agent.
   *Reading adopted:* keep all three as resources (the guides are the specification, not the mule's
   current state), write `20auto-upgrades` directly rather than through `dpkg-reconfigure`, and treat
   the v1 absence as a gap in the reference rather than a decision against the feature.

10. **Does `rpi-connect-lite` stay at parity?** It is installed and its two user units are enabled in
    the v1 reference, and guide 13 step 6 maintains it per clone. [§3.6](../version2.md) replaces its
    function with the Fleet Manager's reverse shell and never mentions it.
    *Reading adopted:* superseded — no resource. But it is live parity state that something must
    explicitly decide to remove, and removing a working out-of-band access path before the reverse
    shell is proven would be unwise sequencing.

11. **The bootloader version is not captured in the parity reference.** The v1 inventory records
    EEPROM *configuration* but no bootloader version, while `rpi-eeprom-update.service` is enabled and
    free to change it. [§2.2](../version2.md) lists "one firmware version" as a canonical example of a
    resource, so the boot chain has an unpinned firmware in a catalog that pins the mic array's.
    *Reading adopted:* `eeprom.config` covers configuration only; a `firmware.rpi-bootloader.version`
    resource is probably warranted but cannot be specified without a captured baseline. Capture
    `rpi-eeprom-update -l` / `vcgencmd bootloader_version` before the mule is wiped again.

12. **`app.config.immich-kiosk-url` disagrees with itself in the parity reference.** The running
    frame's `config.json` lacks `use_offline_mode=true` while `app/config.example.json` includes it.
    One of them is the desired value and the other is live drift in the artifact that defines parity.
    *Reading adopted:* the example file is correct — offline serving is a stated product requirement
    ([§2.6](../version2.md): an outage in the operator's house must never blank a frame in someone
    else's) — so the running frame is drifted and the catalog's desired value includes the parameter.
