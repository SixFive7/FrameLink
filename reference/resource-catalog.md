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
**`—` means `agent.version` and nothing else**, and on `agent.version` itself it means nothing
precedes it at all. Value source is either *fixed by the catalog* or a named Fleet Manager setting
per [§3.4](../version2.md) (every setting is a fleet default with a per-device override).

**`—` does not imply an adoption edge.** The rule for when one is required, stated once and applied
to every entry below: a resource declares `agent.adoption` **when its desired value is issued by the
Fleet Manager and the catalog holds no default that is correct without it** — the frame would
otherwise have to guess. A resource whose value is *fixed by the catalog* never declares it, and
neither does one whose fleet setting has a catalog default that is right on an unadopted frame: it
applies the default now, and a later fleet override is ordinary drift that reconciles like any other
change. The test is not "is there a fleet setting" but "**would this resource have to guess**".
`boot.cmdline.fbcon-rotate` is the worked example and states the mechanism in full.

**Why the definition had to be narrowed.** `—` previously read as "depends only on the agent roots",
which was understood to include `agent.adoption`, and the literal consequence was that
`pkg.chromium` and `pkg.labwc` were gated on adoption. [§2.7](../version2.md)'s **browser stage**
needs exactly those two packages to render the repair screen that a **pending, unadopted** frame is
required to be showing — including the short fingerprint and hardware serial
[§3.3](../version2.md) wants so the operator can tell which row is which frame on the bench. The
literal reading therefore withheld the product's primary honesty mechanism precisely when it is most
needed. What §3.3 actually withholds from a pending device is **configuration** — no settings, no
token, no commands — and a catalog-fixed package set is none of those: it is identical on every
frame and contains nothing an operator chose. The package block is implemented with **zero** edges
on exactly this reasoning; the definition above is the catalog agreeing with the code rather than
the other way round.

The display group under Guide 3 and `app.http.local-origin` spell `agent.version` out instead of
writing `—`. Under this definition that is the same thing, and it is kept rather than collapsed
because their whole point is running ahead of adoption; see the carve-out at the head of that
section.

**Counts.** 71 resources come from guides 3–12; 8 more are cross-guide or mandated by v2 itself and
are listed in their own section. Total **79**. It was 80 until decision 90 took
`firmware.xvf3800.version` out of the graph; what it observed is now reported beside the loop and is
recorded under "Does not become a device resource" below.

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

**These two resources are scheduled first, by operator decision (2026-08-15).**
[§5.5](../version2.md) schedules brick-capable resources last; [§2.7](../version2.md) requires the
agent to narrate on its own virtual terminal, `/dev/tty8`, "from the first second of the first
boot" and forbids blank screens. Open question 1 recorded the collision and left it to the
operator. It is now decided in
favour of §2.7: on-screen narration is the product's primary honesty mechanism and it is worth
nothing if there is no screen. The brick risk was raised explicitly and accepted, and the decision
is recorded as [decision 46](../version2.md), with §2.7 and §5.5 both pointing at it.

**This is a carve-out for one resource group, not a repeal of §5.5.** Every other brick-capable
resource keeps its late slot — `boot.config.dtoverlay-vc4-kms-v3d-noaudio`,
`boot.config.camera-auto-detect`, `boot.cmdline.wifi-regdom`, `eeprom.config`, and the DFU flash in
its own hand-recoverable position — and the two display resources keep every mitigation §5.5
attaches to the rule. §5.5's *ordering* clause is narrowed by exactly two resources; its
*mitigations* are not weakened at all.

**Measured on the mule 2026-08-15, which is what makes the carve-out necessary rather than tidy.**
On a stock Trixie image `config.txt` carries only `dtoverlay=vc4-kms-v3d`, both HDMI connectors
report `disconnected`, **there is no DSI connector at all**, `dmesg` repeats
`vc4-drm axi:gpu: [drm] Cannot find any crtc or sizes`, `/dev/fb0` does not exist and
`/sys/class/backlight/` is empty. `console=tty1` is on the kernel command line and
`/sys/class/tty/console/active` reads `ttyAMA10 tty1`, so **opening `/dev/tty1` and writing a whole
designed frame returns without error and produces no pixels.** A console stage that trusted its own
write would report success while showing nothing — the same shape of failure
[§2.4](../version2.md) exists to catch. That measurement was taken on `/dev/tty1`, and the console
stage has since moved to `/dev/tty8` ([decision 57](../version2.md)); the finding transfers
unchanged, because what is missing on a dark frame is `/dev/fb0` — with no framebuffer, no virtual
terminal produces pixels, whichever one is in front.

**The write discipline this group runs behind.** These are the three mitigations that make an early
slot affordable rather than reckless, and they are part of both entries below rather than advice
attached to them:

1. **Validate before writing.** Parse the whole file, apply the change to the parsed form, and
   re-serialise; reject a result that is not exactly one line (`cmdline.txt`) or that has lost a
   section header or gained a duplicate `dtoverlay` (`config.txt`). Never append blind, and never
   re-serialise from a copy read before another resource's write.
2. **Back up first.** Copy `config.txt` and `cmdline.txt` to a fixed backup path **on the boot
   partition itself** before the provision's first write, and keep them there. A backup on the root
   filesystem is useless to a bootloader that never gets that far.
3. **Boot-count self-repair.** Record the attempt before rebooting and clear it once the agent is
   back up and the resource has verified; a device that does not come back must land on the
   known-good file with nobody touching it. Two mechanisms can deliver that and **neither has been
   tested on this hardware**: an agent-written counter on the boot partition, whose restore runs on
   the *next* successful boot and therefore does nothing for a device that never boots again; or the
   Pi bootloader's own `tryboot` one-shot, where the candidate goes to `tryboot.txt`, the reboot
   requests it once, and any later boot falls back to the untouched `config.txt` — which
   [§5.1](../version2.md)'s smart-plug power-cycle harness can supply unattended. The second is the
   only one that survives a frame that never boots again. **Measured 2026-08-15: `tryboot` is not
   configured on the mule** — no `autoboot.txt`, no tryboot files on the boot partition — so it is
   an unused mechanism rather than a working one waiting to be adopted, and the boot partition's
   430 MB free means neither mechanism is constrained by space. Choosing and proving one is the
   outstanding half of this decision (open question 1).

**`boot.cmdline.fbcon-rotate`**

- **From** — [guide 3 step 1](../docs/3-hardware-configuration.md#1-enable-the-dsi-touch-display-and-rotate-the-console-to-landscape)
- **Sets** — `fbcon=rotate:1` appended to the single line of `/boot/firmware/cmdline.txt`; exactly one `fbcon=rotate:` token.
- **Observe** — `grep -o 'fbcon=rotate:[0-9]*' /boot/firmware/cmdline.txt` **and** `grep -o 'fbcon=rotate:[0-9]*' /proc/cmdline`.
- **Verify** — identical. Both halves are deliberate: the file is the desired state, `/proc/cmdline` proves the bootloader actually handed it to the kernel. [§2.4](../version2.md) records the governor case where `/proc/cmdline` agreed and the effect still did not land, so `/proc/cmdline` alone is not sufficient evidence either — it is necessary, not sufficient.
- **dependsOn** — `agent.version`
- **Value source** — fleet setting `display.consoleRotation` (fixed at `1` today; guide names `3` as the upside-down remedy). Applied from the catalog default when no fleet value has been issued, which is the normal case at position 2 — the frame is not adopted yet. A later fleet override is ordinary drift and reconciles like any other. This is the worked example the `dependsOn` rule at the head of this document points at: a fleet setting with a catalog default that is correct before adoption declares no adoption edge.
- **Risk** — **brick-capable** (`cmdline.txt`; a malformed single line is unbootable). The riskier of the two display writes and the one the write discipline above exists for: `config.txt` tolerates an unknown overlay line, a broken `cmdline.txt` does not boot.
- **Notes** — Scheduled **2nd**, and ahead of the overlay on purpose: nothing is visible until the overlay lands, so applying the rotation first costs nothing and makes the panel's *first* lit frame legible instead of sideways. That is an ordering preference, not a dependency — `boot.config.dtoverlay-waveshare-panel` deliberately does **not** declare this resource, because a failed cosmetic rotation must never leave the panel dark by marking the overlay `Blocked`. `cmdline.txt` must stay one line. The v1 reference line also carries `cfg80211.ieee80211_regdom=NL`, which is its own resource (`boot.cmdline.wifi-regdom`), and `ds=nocloud;i=rpi-imager-…`, which is **not** owned by any resource: it is Raspberry Pi Imager's datasource pin, present because the v1 card was Imager-flashed and absent on the raw-flashed mule. No resource writes it, and it is worth leaving alone rather than normalising, because it is the one piece of evidence distinguishing an Imager-provisioned card from a raw-written one — see the hypothesis under `identity.hostname`. Any writer that appends here competes with kernel-package postinst hooks from `raspberrypi-sys-mods` — and `boot.cmdline.wifi-regdom` now edits the same single line from position 79, so one line-aware editor must serve both ends of the order.

**`boot.config.dtoverlay-waveshare-panel`**

- **From** — [guide 3 step 1](../docs/3-hardware-configuration.md#1-enable-the-dsi-touch-display-and-rotate-the-console-to-landscape)
- **Sets** — the exact line `dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a` present once under `[all]` in `/boot/firmware/config.txt`.
- **Observe** — `grep -cxF 'dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a' /boot/firmware/config.txt` (must be exactly `1`).
- **Verify** — **differs from Observe.** The line check, **plus** the display probe: a connector under `/sys/class/drm/card*-DSI-*/status` reading `connected`, with the presence of `/dev/fb0` and of a `/sys/class/backlight/` entry recorded as supporting evidence. At position 3 that is no longer an optional checkpoint — every resource after this one narrates onto this panel, so "the line is present" cannot be allowed to stand in for "the screen exists". A failing probe with a correct line is a **hardware** diagnosis (ribbon seating, 5 V) and must be reported as `Degraded` carrying the probe's evidence verbatim; it must **not** trigger another `config.txt` write. Repeated boot-partition writes are the risk this carve-out is spending, not a repair.
- **dependsOn** — `agent.version`
- **Value source** — fixed by the catalog (panel model is a hardware fact). The `,dsi0` suffix variant applies only if the ribbon is on the LAN-side DSI port.
- **Risk** — **brick-capable** (`config.txt`), scheduled **3rd** by operator decision — see the carve-out at the head of this section.
- **Notes** — Duplicate-line detection matters: `grep -c` rather than `grep -q`, because a non-idempotent write history is the failure this guards. **This resource is what the console stage costs.** Until it is `InSync` the agent narrates into the dark and the Fleet Manager is the only surface left, which is exactly the state [§1.2](../version2.md) principle 3 says must be named rather than silent. It also gates diagnostics: the screenshot path allowlisted by [§3.6](../version2.md) reads `/dev/fb0`, so a frame that has not reached this resource cannot be looked at remotely either. `boot.config.camera-auto-detect` and `boot.config.dtoverlay-vc4-kms-v3d-noaudio` write the same file from positions 77 and 78 — different lines, same file, opposite ends of the order.

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
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' git`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed.
- **Risk** — —
- **Notes** — Only reason for `git` on the frame was fetching the reSpeaker repository (guide 4) and the FrameLink checkout (guide 10). Guide 10's use is superseded by the embedded app, and guide 4's is superseded by decision 63 — the agent now fetches six pinned raw files, not a clone. **This resource therefore has no remaining consumer.** It is kept for now rather than deleted so that the removal is a decision of its own with the catalog's numbering, the ordering table and the parity facet map moved in one change; nothing in v2 depends on it.

**`tool.xvf-host.installed`**

- **From** — [guide 4 step 2](../docs/4-audio-configuration.md#2-install-and-verify-the-xvf3800-host-control-tool)
- **Sets** — Seeed's aarch64 `xvf_host` and its **five** sidecar files — `libcommand_map.so`, `libdevice_i2c.so`, `libdevice_usb.so`, `dfu_cmds.yaml`, `transport_config.yaml` — present in `/var/lib/fl-agent/xvf3800/host_control/rpi_64bit`, each matching its pinned SHA-256, `xvf_host` executable. v1 path: `~/xvf3800/host_control/rpi_64bit/xvf_host`, still searched second.
- **Observe** — SHA-256 of all six files against `XvfHostReleasePin.Current`, plus the executable bit on `xvf_host`, plus a live `(cd <dir> && ./xvf_host VERSION)` returning the `Found device VID: 10374 PID: 26 interface: 3` banner. The last half proves the HID control interface is reachable, which presence and hashes alone do not.
- **Verify** — identical, and it survives §2.4's reboot by construction: nothing is remembered. Every pass re-hashes the files on disk and re-runs the round trip, so a claim that the install "succeeded" can never outlive the files it describes.
- **dependsOn** — —
- **Value source** — fixed; the six files are pinned at commit `725f3846` by `src/FrameLink.Agent/Resources/XvfHostRelease.cs` and reviewed in `upstream-review.json` under `xvf-host-tool`.
- **Risk** — —
- **Notes** — The binary loads its `.so` files **relative to its own directory**, so the working directory is part of the contract, not an incidental `cd`. Root is required — Seeed ship no udev rule for the HID node. **Six files, not four** (the earlier count of "three sibling `.so` files" was wrong): Seeed's own `host_control/README.md` lists `dfu_cmds.yaml` and `transport_config.yaml` as required members of the same directory. The seventh file there, `xvf_i2c_dfu`, is deliberately not fetched — this build does USB DFU through `dfu-util` and never the I2C path. Delivery is the Immich Kiosk shape (pinned upstream artifact, checksum-verified, fetched never vendored) but pinned at a **commit SHA** rather than a release, because the upstream repository has zero releases and zero tags; see decision 63 and open question 3 below.

**`pkg.dfu-util`**

- **From** — [guide 4 step 3](../docs/4-audio-configuration.md#3-pin-the-array-firmware-to-v2-0-10)
- **Sets** — apt package `dfu-util` installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' dfu-util`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed, under the same presence-plus-floor rule as the kiosk block; the reviewed version is in that block's table.
- **Risk** — —
- **Notes** — **Nothing in the agent runs this program any more** (decision 90): the DFU flash left the resource graph and the binary names `dfu-util` in no code path at all. It is kept, and it is not `pkg.git`'s situation: the flash is now an attended bench operation, so the tool has to be on the card when somebody arrives at the frame to perform one. That is a real consumer, and it is a person.

**`audio.xvf3800.gpo-x0d31-amp-enable`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes)
- **Sets** — GPO pin `X0D31` low (active-low = speaker amplifier enabled).
- **Observe** — `(cd <xvf dir> && sudo ./xvf_host GPO_READ_VALUES)` → five values in the fixed order `X0D11, X0D30, X0D31, X0D33, X0D39`; the **third** must be `0`.
- **Verify** — identical.
- **dependsOn** — `tool.xvf-host.installed`
- **Value source** — fixed.
- **Risk** — —
- **Notes** — **Both firmware levels this project has seen boot the pin low, so the Act does not run on any array we own** — measured 2026-08-20 on two arrays, a factory `2 0 6` board and an upgraded `2 0 10` board, each reading `GPO_READ_VALUES 0 0 0 1 0` on a frame whose agent had been stopped before the array was attached. That closes open question 13. It is still independently verifiable and a future firmware could default differently, which is exactly why it is its own resource, and Observe reads the pin rather than assuming it. **Its `dependsOn` used to run through `firmware.xvf3800.version`** and now names the tool directly, because decision 90 removed that resource.
- **Notes on the one write it can make** — Upstream issue #18 is the only report associating `GPO_WRITE_VALUE` with a damaged array, and it does not isolate it: the reporter used `LED_EFFECT`, three `led_*` commands, `GPO_WRITE_VALUE`, `CLEAR_CONFIGURATION`, `SAVE_CONFIGURATION` and repeated DFU reflashes on firmware 2.0.5–2.0.7, all older than the 2.0.9 in which upstream says issue #8's `SAVE_CONFIGURATION` corruption was fixed; the issue is open with **zero comments** and no maintainer response; his device still enumerates, still answers `VERSION 2 0 7` and still plays audio; and his own `GPO_READ_VALUES` reads `0 0 0 1 0`, the same five values a healthy array reads. The agent sends `VERSION`, `GPO_READ_VALUES` and `GPO_WRITE_VALUE` and nothing else — no `SAVE_CONFIGURATION` anywhere in the repository — so its GPO write is volatile and cannot reach the DataPartition that survives a reflash. The same readback carries two diagnostics worth keeping: the **second** value is `X0D30`, the hardware Mute button (a `1` means someone pressed it, and mic capture is silent), and the **fourth** is `X0D33`, the LED ring rail (active-high). Neither is agent-settable; both belong in telemetry.

**`audio.mixer.pcm0-playback-volume`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes); verified by step 6
- **Sets** — ALSA simple control `PCM,0` on card 0 at `60` (0.00 dB) on both channels.
- **Observe** — `amixer -c 0 sget PCM,0` → `Front Left`/`Front Right: Playback 60 [100%] [0.00dB]`.
- **Verify** — identical. Guide 4 step 6 *is* this Verify, run after a reboot.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`, `audio.mixer.pcm0-playback-switch`, `audio.wireplumber.playback-volume`
- **Value source** — fleet setting `audio.playbackVolume` ([§3.4](../version2.md) names audio volume explicitly), catalog default `60` = 0.00 dB. Do not permit values above 0 dB anywhere in the chain. The default is correct on an unadopted frame, so this resource does **not** declare `agent.adoption` — a fleet override arriving later is ordinary drift, per the `dependsOn` rule at the head of this document. Its two siblings below read the same way.
- **Risk** — —
- **Notes** — **The suspected WirePlumber revert is no longer suspected; this resource is where it was measured.** On the frame 2026-08-16 the resource set `60`, rebooted, verified — and the value observed afterwards was `Front Left=37 -23.00dB on, Front Right=37 -23.00dB on [wireplumber active, 1 stored device files]`. `37` is not noise: this control is **one step per decibel**, which three independent readings agree on (60 = 0.00 dB in the v1 inventory, 40 = the −20 dB `PCM,1` ships at, 37 = −23.00 dB here), so 37 is a *requested gain of −23 dB* by something that is not this resource. Two consequences the entry now carries. First, **Observe is gated on the login session** — the second owner lives inside it, and a reading taken before it starts is a reading of a value still being decided (see the suspected-revert list and [§2.4](../version2.md)). Second, the value is additionally owned at the layer that sets it, by `audio.wireplumber.playback-volume` below. Owning the ALSA control alone is measured to be insufficient. **And this resource is ordered *after* that one, which is the opposite of how the edge was first written.** The rescue was declared depending on the two mixer volumes, so it could not be acted on until this resource was `InSync` — a state this resource cannot reach while WirePlumber is asking for −23 dB. Measured on the frame 2026-08-16: `PCM,0` spent all three attempts, escalated, the escalation stopped the pass ([§2.6](../version2.md), decision 68), and the resource that exists to make it convergeable never executed once. `PCM,1` takes no such edge — it is a second hardware stage no route volume reaches and it was never observed away from `60`.

**`audio.mixer.pcm1-playback-volume`**

- **From** — [guide 4 step 4](../docs/4-audio-configuration.md#4-enable-the-speaker-amplifier-and-set-the-volumes); verified by step 6
- **Sets** — ALSA simple control `PCM,1` (second, mono gain stage) on card 0 at `60` (0.00 dB).
- **Observe** — `amixer -c 0 sget PCM,1` → `Mono: Playback 60 [100%] [0.00dB]`.
- **Verify** — identical.
- **dependsOn** — `audio.modprobe.snd-usb-audio-index`, `audio.mixer.pcm1-playback-switch`
- **Value source** — fleet setting `audio.playbackVolume` (same setting, applied to both stages).
- **Risk** — —
- **Notes** — **The highest-value resource in this guide.** Ships at `40/60` = −20 dB; measured at roughly **+18 dB at the speaker** when corrected. A frame with `PCM,0` correct and `PCM,1` at default is fully functional and merely quiet — the class of fault nobody reports and nobody finds. Separate resource from `PCM,0` because it is a genuinely separate gain stage with its own default, not a second view of the same control. **And it is the reason the mixer cannot simply be handed to WirePlumber**: a PipeWire route volume reaches one mixer element, so whatever happens to `PCM,0` this stage stays the agent's to own. Session-gated like its sibling. **Untested prediction worth checking on the next hardware run:** if the `PCM,0` revert is a route volume, this stage does *not* move with it — the measured delta names `PCM,0` only, which is consistent with that and does not establish it.

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
- **Notes** — Not set by any guide command; recorded here because it is persisted by `alsactl store` and is the mic-side twin of the `PCM,1` trap — a frame that cannot be heard while everything reports healthy. Kept as one resource pending open question 8: whether `Headset,0`/`Headset,1` are two real gain stages (as `PCM,0`/`PCM,1` turned out to be) or two views of one. If two, split it.

**`audio.wireplumber.playback-volume`**

- **From** — no guide; added 2026-08-16 after the `audio.mixer.pcm0-playback-volume` revert was measured on the frame
- **Sets** — WirePlumber's own volume for the default audio sink, at the linear equivalent of the mixer step, unmuted.
- **Observe** — `wpctl get-volume @DEFAULT_AUDIO_SINK@` inside the login user's session → `Volume: 1.00`. Compared in **decibels** with a half-step (0.5 dB) tolerance, because the hardware control is quantised at 1 dB per step and WirePlumber's volume is a continuous fraction — an exact compare would report permanent false drift the moment either side rounded.
- **Verify** — identical, after a reboot, and behind the session gate.
- **dependsOn** — `pkg.wireplumber`, `boot.autologin.getty-tty1`
- **Value source** — fleet setting `audio.playbackVolume`, the same one the mixer resources read, converted with the measured 1 dB-per-step scale. Two owners deriving from one setting is the point: they cannot disagree about what the frame wants.
- **Risk** — —
- **Notes** — **What is measured and what is arithmetic, kept apart.** *Measured:* the ALSA control was set to 60, verified across a reboot, and observed at 37 (−23.00 dB) with WirePlumber running. *Arithmetic:* WirePlumber 0.5's `device.routes.default-sink-volume` default is `0.064` linear = **−23.88 dB**, and the nearest step at or above that request on a 1 dB scale is exactly **37**. That predicts the observed value to within the control's own quantisation and nothing else on offer does — but it is a calculation on a documented constant, **not an observation**, and three of this document's suspected reverts have already been disproved by measurement. It is the leading hypothesis, not the mechanism. **The default-versus-restore question is now closed, by measurement.** `~/.local/state/wireplumber/` on the frame holds exactly one file, `stream-properties` — no `restore-stream`, no `default-routes`, no `default-nodes`, which are what WirePlumber 0.5 writes when it has a stored per-device route volume to restore. So WirePlumber is applying a **default**, not restoring a stored value, and this document's long-standing blocked question is answered. **Why it changes the fix:** owning `~/.local/state/wireplumber/` — this document's original suggestion — repairs nothing, because there is no stored route volume to correct. Setting the volume *through* WirePlumber is correct under both readings: it overrides a default, and it is what makes `restore-device` persist a stored value. That is deliberate, because the mechanism cannot be settled without hardware. **Why `wpctl` and not a config fragment:** a `~/.config/wireplumber/wireplumber.conf.d/` file setting the default to unity, or `api.alsa.soft-mixer` taking the hardware mixer away from WirePlumber, are both plausible and neither is testable from a workstation — and a malformed fragment stops WirePlumber starting, which takes `wireplumber.conf.camera-monitors-disabled` down with it and leaves the frame with no audio at all. `wpctl` is the supported interface, cannot break the daemon, and a refused call is ordinary visible drift. **The mute is folded in** rather than split out, unlike the hardware switches: it is one route property set through one tool, and splitting it would give two halves of a single write two independent escalation ladders. **It runs before the resource it rescues.** The two `audio.mixer.*` edges this entry first carried inverted the ordering and made the rescue unreachable — see `audio.mixer.pcm0-playback-volume` above — and neither survived this document's own dependency test: `wpctl` reads and writes WirePlumber's sink and touches no ALSA control, and the desired level comes from the shared fleet setting rather than from a sibling's converged state, so this resource never has to guess.

**`audio.alsa.stored-state`**

- **From** — [guide 4 step 5](../docs/4-audio-configuration.md#5-persist-the-alsa-mixer-state-across-reboots)
- **Sets** — `/var/lib/alsa/asound.state` containing the desired values for every mixer resource above.
- **Observe** — parse `/var/lib/alsa/asound.state` for card `Array` and compare each control against desired.
- **Verify** — identical. Deliberately **not** the same as reading the live mixer: the running value can be correct while the stored value is wrong (nothing has rebooted yet), and the stored value can be correct while the running value is wrong (something changed it after boot). Those are two different faults and the catalog keeps both observable.
- **dependsOn** — every `audio.mixer.*` resource, **and `audio.wireplumber.playback-volume`**
- **Value source** — derived (it is the persisted form of the mixer settings).
- **Risk** — —
- **Notes** — `alsa-restore.service` is a **static** unit shipped with `alsa-utils`; it is not enabled or installed and has no enablement resource — its post-boot state (`active (exited)`, `status=0/SUCCESS`) is a checkpoint assertion, not a setting. Rewriting the file wholesale is idempotent by construction. **The WirePlumber edge is new and it is load-bearing:** `alsactl store` records whatever is live at the instant it runs, and the mixer has a second owner that writes it once the session is up — so the store has to happen after both owners agree, not after only one of them has written. Ordering it explicitly is what stops this file being a snapshot of whichever owner happened to go last.

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

**`pkg.labwc`** · **`pkg.chromium`** · **`pkg.wireplumber`** · **`pkg.pipewire-alsa`** · **`pkg.wlr-randr`**

- **From** — [guide 5 step 2](../docs/5-kiosk-base.md#2-install-the-kiosk-packages) (five resources, one per package)
- **Sets** — each apt package installed.
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' <pkg>`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed. The catalog asserts **presence**, and records a **reviewed version as a floor** (see the table below): at or above it the package is in sync however far ahead it has moved, below it or absent is drift.
- **Risk** — —
- **Notes** — **The `dpkg-query` format string above is the one every package resource in this document uses**, `pkg.` and `pkg.*.absent` alike. It carries `${Version}` because the version is now compared — **in one direction only**. Decision 55 states the rule and the reason: the frame has no inbound port and is left running Debian's security-only automatic updates, so packages are *expected* to move forward on their own, and a literal pin would read one of those as drift, downgrade the package back, and under [§2.6](../version2.md) stop the product until it had. So forward is in sync and is merely reported; backward, or absent, is ordinary drift, and the Act is the ordinary `apt-get install`, which moves the package *up* to whatever the archive offers rather than down to the recorded level. Every package's version — all ~930 on the frame, not the fifteen here — is additionally reported to the Fleet Manager, which computes drift against this baseline and across the fleet. The catalog previously gave two spellings, with and without `${Version}`, and left an implementer to pick — they are now one. One package = one resource is explicit in [§2.2](../version2.md). ~215 dependencies, ~256 MB download, ~750 MB on disk; apt resolves the transitive set, which is not enumerated here. On Trixie the browser package is `chromium`, not `chromium-browser`, and the binary is `/usr/bin/chromium`. `pkg.chromium` also drags in `rpi-chromium-mods`, which injects flags from `/etc/chromium.d/` — relevant to `unit.chromium-kiosk.running-matches-content`. The order above is the dependency ordering's, which is the order the reconciler converges them in; guide 5's own single `apt install` line names them `labwc chromium pipewire-alsa wireplumber wlr-randr`, and the two do not have to agree because apt installs its five arguments as one transaction while the reconciler applies five resources one reboot at a time. The heading used to carry the guide's order and the ordering table the reconciler's, which read as a contradiction rather than as two different things.

**Reviewed versions for the whole package block.** Transcribed from the `PACKAGES` block of
[the v1 state inventory](v1-state-inventory.txt), the frozen v1 reference of version2.md's Precondition zero, and
*Verified 2026-08-15* against it by a test that reads that file rather than by anybody's memory ([§7.1](../version2.md)).
These are floors, not pins — see the rule above. The implementation carries them in `AptPackageSpec.ReviewedVersion`.

| Resource | Reviewed version |
| --- | --- |
| `pkg.labwc` | `0.9.2-1+rpt4` |
| `pkg.chromium` | `1:146.0.7680.164-1~deb13u1+rpt1` |
| `pkg.wireplumber` | `0.5.8-2` |
| `pkg.pipewire-alsa` | `1.4.2-1+rpt3` |
| `pkg.wlr-randr` | `0.4.1-1` |
| `pkg.xdg-desktop-portal` | `1.20.3+ds-1` |
| `pkg.xdg-desktop-portal-gtk` | `1.15.3-1` |
| `pkg.gstreamer1.0-tools` | `1.26.2-2` |
| `pkg.gstreamer1.0-plugins-base` | `1.26.2-1+rpt3+deb13u1` |
| `pkg.gstreamer1.0-libcamera` | `0.7.0+rpt20260205-1` |
| `pkg.gstreamer1.0-pipewire` | `1.4.2-1+rpt3` |
| `pkg.libspa-0.2-libcamera.absent` | — (asserts absence; there is no version to record) |
| `pkg.dfu-util` | `0.11-3` |
| `pkg.grim` | `1.4.0+ds-2+b2` |
| `pkg.unattended-upgrades` | — (**not installed on the v1 frame at all**, open question 9, so its floor is genuinely unknown rather than merely unwritten; the resource asserts presence alone) |

**`boot.autologin.getty-tty1`**

- **From** — [guide 5 step 3](../docs/5-kiosk-base.md#3-enable-console-autologin)
- **Sets** — `/etc/systemd/system/getty@tty1.service.d/autologin.conf` containing exactly `[Service]` / `ExecStart=` / `ExecStart=-/sbin/agetty --autologin framelink --noclear %I $TERM`.
- **Observe** — `cat /etc/systemd/system/getty@tty1.service.d/autologin.conf` **and** `systemctl show getty@tty1.service -p ExecStart -p LoadState -p ActiveState` **and**, once that `ActiveState` is settled, `loginctl list-sessions` showing a session for `framelink` on `tty1`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fleet setting `device.user` (the username; `framelink` in the running example), defaulting to the account the image was flashed with, which the frame can read off itself. **It therefore does not gate on adoption**, and that is load-bearing rather than a technicality: this file is the root of the whole user-unit layer, so an adoption edge here would block the session, labwc and the browser — and [§2.7](../version2.md)'s browser stage would then be unavailable to exactly the pending frame that is supposed to be rendering its own fingerprint on it.
- **Risk** — not brick-capable, but a wrong username means no user session, and every user unit below is then `Blocked`.
- **Notes** — Written by `raspi-config nonint do_boot_behaviour B2`, which is a **competing owner**: any later `raspi-config` boot-behaviour call rewrites or removes this file. The empty first `ExecStart=` is required by systemd to clear the inherited value; a drop-in missing that line does not override. **The whole user-unit layer hangs off this one file** — there is no `loginctl enable-linger` anywhere in the v1 build (see open question 6).
  **The third clause was `who` until the first full provision, and the failure that replaced it is not explained.** On that provision this resource burned all five attempts, rebooted the frame five times, `Escalated`, and left twelve resources `Blocked` — with a byte-correct drop-in that systemd had loaded, and `nobody is logged in as framelink on tty1` as the only failing clause. Measured on the frame afterwards: `/run/utmp` is **absent** on Debian 13, and `who` **answers anyway**, exits 0, and prints a correct `framelink tty1` line, so Debian's `who` has another source — evidently logind. The tidy explanation, that `who` reads a file this OS no longer has and therefore cannot answer, is **false**; it was written into this entry once and is corrected here rather than replaced with a second guess.
  What the evidence supports: the verifies followed genuine reboots (the loop reaches that message only on a changed boot id); the configuration in place for the last two verifies is byte-identical to the one serving a working console session now; and the environment is not the difference, because `systemctl` runs through the same launcher and `PATH` in the same process and *its* clause passed on the passes this one failed. The agent also starts alongside the console getty rather than after it — `agetty` pid 918 against `fl-agent` pid 930 — while this verify is the first thing the loop does on a new boot and the retry backoffs are spent rebooting for other resources, so every sample ever taken landed seconds after a boot. That leaves two candidates and no way to choose: the sample was taken before the login happened, or the console genuinely was not logging anybody in and the check was truthful. A third is not excluded — a command that fails to launch returns empty output, so the old code could report "nobody is logged in" for a `who` that never ran. **The honest position is that the observable was fragile, it has been replaced, and the original failure is not fully explained.**
  `systemd-logind` is the right observable independent of all that: it is the authority that owns session state, and a session carrying both the user and `tty1` distinguishes the console autologin from an administrator's SSH session, which `user@1000.service` being active does not. The `ActiveState` gate covers the part of the timing that *is* certain: `getty@.service` is `Type=idle`, so for up to five seconds of every boot the unit is correct and has not run `agetty` yet, and a session sampled in that window is absent for a reason that says nothing about the setting. While it is `activating` the session clause is not counted; in every settled state it is, and a getty that is `inactive` or `failed` is reported as itself. Beyond the gate the delta now carries **how long the getty had been active** when no session was found, from the unit's own `ActiveEnterTimestampMonotonic` against `/proc/uptime` — "active for 1s" and "active for 47s" separate the two surviving candidates on sight. It is reported and never acted on, because a threshold chosen now would be a behaviour justified by a cause nobody has established.

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
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' <pkg>`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed, under the same presence-plus-floor rule as the kiosk block above; the reviewed versions are in that block's table.
- **Risk** — —
- **Notes** — `xdg-desktop-portal-gtk` is not cosmetic: the portal frontend only registers the Camera interface when a backend implementing the `Access` permission service is present.

**`pkg.libspa-0.2-libcamera.absent`**

- **From** — [guide 6 step 1](../docs/6-camera.md#1-install-the-camera-packages) ("just as important is what is *not* installed") and [step 4](../docs/6-camera.md#4-route-the-camera-through-a-dedicated-pipewire-node)
- **Sets** — apt package `libspa-0.2-libcamera` **not installed**.
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' libspa-0.2-libcamera` → absent/not-installed.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed. No reviewed version and none possible: this resource asserts the package's *absence*, so there is nothing to record a floor for.
- **Risk** — —
- **Notes** — Confirmed absent in the v1 reference (the inventory carries `libspa-0.2-modules`, which is a different package). The measured failure it causes: a camera node hard-capped near 30 fps that advertises no framerates, rejects sizes outside its own menu, and that Chromium cannot acquire above 720p. Absence is a real, actionable, independently verifiable state — hence a resource, not a note. `wireplumber.conf.camera-monitors-disabled` is the belt to this braces, for the case where a future dependency drags the plugin back in.
  **Its slot in the package block is decided rather than incidental: 12th, immediately after the camera chain, not last.** The question is real — an install ordered *after* this one that pulled the plugin back in would leave the block converging on a state it had already asserted, and the repair would cost an extra reboot. It is decided on what actually follows it. The three installs after it are `dfu-util` (libusb), `grim` (wayland, pixman) and `unattended-upgrades` (python3-apt); none of them can pull a PipeWire SPA plugin, so the claim "after everything in this block that might drag it in" holds as the block stands. Against that, keeping it beside the camera chain it exists for is worth something to a reader, and if Debian ever re-cuts a dependency so that one of those three *can* pull it in, the result is ordinary drift and level-triggered convergence repairs it — for the price of one reboot, through the same mechanism the whole design already leans on rather than through a special case. Moving it last was the alternative and would have been free; it was declined because the exposure it removes does not exist today and the grouping it breaks is read by every person who opens this section. This is also the order the implementation declares.

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
- **Notes** — Stock-image default, not written by any guide, but the guide names it as the thing to check when the camera is missing — so it is a real resource with a real Act (restore the line). See open question 7 on the wider set of stock `config.txt` lines.

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
- **Observe** — `systemctl --user show chromium-kiosk.service -p MainPID`, then that pid's `/proc/<pid>/cmdline`, compared against the unit's `ExecStart` arguments.
- **Verify** — identical.
- **dependsOn** — `unit.chromium-kiosk.content`, `unit.chromium-kiosk.enabled`
- **Value source** — derived.
- **Risk** — —
- **Notes** — Its own resource because "unit file correct, running process stale" is the single most common post-edit drift, and the guide states the principle outright: *the command line is the authoritative truth — if the flag is not here, it is not in effect, whatever a config file says.* Act is `systemctl --user daemon-reload && systemctl --user restart chromium-kiosk.service`. **Comparison caveat:** `rpi-chromium-mods` injects flags from `/etc/chromium.d/`, so the running command line is a legitimate **superset** of `ExecStart`; the compare must be containment, not equality. **Identification caveat (measured 2026-08-16):** `pgrep -a chromium` cannot find this unit's browser and the Observe above is written the way it is because of it. On Trixie `/usr/bin/chromium` is a 5,920-byte shell script whose last line is `exec $LIBDIR/$APPNAME $CHROMIUM_FLAGS "$@"`, so the declared path is on no running command line at all — on the mule, `pgrep -a chromium | grep -c '/usr/bin/chromium'` is 0 against 12 for `/usr/lib/chromium/chromium` — and an Observe that greps for it reports "no browser process is running" on a healthy frame, forever, then restarts a working browser. The unit's `MainPID` is the only identification that both survives the wrapper and is specific to *this* unit; a hand-started Chromium carries the same binary and can carry the same flags. For the same reason the declared side of the compare must drop the binary path, or the failure merely moves to "running without `/usr/bin/chromium`".

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
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' grim`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed, under the same presence-plus-floor rule as the kiosk block; the reviewed version is in that block's table.
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
- **dependsOn** — `kiosk.binary.pinned-release`, `agent.adoption`
- **Value source** — fleet setting `immich.serverUrl` (collected at Fleet Manager first run, [§3.2](../version2.md)).
- **Risk** — —
- **Notes** — Adoption is declared because there is no catalog default and none is possible: the address of somebody's photo server is not a value this document can hold, so an unadopted frame would have to guess. That is the `dependsOn` rule at the head of this document doing its work — the `pkg.*` block above needs nothing from the Fleet Manager and declares nothing, while this one cannot be applied at all until the frame has been adopted.

**`kiosk.config.immich-api-key`**

- **From** — [guide 9 step 2](../docs/9-immich-kiosk.md#2-create-the-immich-kiosk-configuration) (`KIOSK_IMMICH_API_KEY`)
- **Sets** — the Immich read-only API key passed to the kiosk child process.
- **Observe** — presence and fingerprint (never the value) in the agent's root-only store; liveness confirmed by the kiosk answering `200` rather than `401`/`403`.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`, `agent.adoption`
- **Value source** — fleet setting `immich.apiKey`. **Secret** — root-only file per [§2.9](../version2.md), never in logs, never in telemetry.
- **Risk** — —
- **Notes** — Its own resource because a wrong key and a wrong URL produce the *same* visible symptom (no photos) with different fixes — the textbook case for the granularity rule. Adoption is declared for the same reason as the URL above, and here it is also what [§3.3](../version2.md) means literally: a pending device receives no token, and this is one.

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
- **Sets** — the kiosk child listening on port `3000` and answering on `127.0.0.1`, where `app.config.immich-kiosk-url` sends the browser. **Not the bind address** — see the notes.
- **Observe** — `ss -tlnp` shows a LISTEN socket on port `3000` owned by the kiosk process, and `http://127.0.0.1:3000/` answers `200`. **The addresses that socket is actually bound to are written into the observation verbatim on every pass, in sync or not**, so a wildcard binding is reported rather than passed over in silence.
- **Verify** — identical.
- **dependsOn** — `kiosk.binary.pinned-release`
- **Value source** — fixed (must agree with `app.config.immich-kiosk-url`).
- **Risk** — not brick-capable. The slideshow endpoint is reachable from the LAN and this resource cannot change that; the exposure is accepted by [decision 56](../version2.md).
- **Notes** — **This entry used to say "on `127.0.0.1:3000` and nowhere else", and that is a property the software cannot provide.** Immich Kiosk v0.42.0 starts its server with `Address: fmt.Sprintf(":%v", baseConfig.Kiosk.Port)` and its configuration struct carries a `port` field with no host or bind field at all, so the process binds every interface. The loopback restriction v1 had was **Docker's port publishing performing it from outside the process**, and [decisions 40 and 41](../version2.md) took Docker off the frame. What is inside this resource's reach is the port and the reachability, and both are asserted; the bind is not, and the Act — restart the child with `KIOSK_PORT=3000` — cannot narrow it. Reporting the observed bind set on every pass is what stands in for the assertion it cannot make: a wildcard binding reaches the screen and the Fleet Manager's history as a fact rather than being assumed away. Closing it needs something outside Kiosk — a packet filter, or an upstream bind setting — and [decision 56](../version2.md) chose acceptance over the filter, pending [Appendix B item 6](../version2.md).

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
- **Observe** — `dpkg-query -W -f='${db:Status-Status} ${Version}\n' unattended-upgrades`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — **fixed: this package is installed on every frame, always.** The on/off fleet setting that [Appendix B item 4](../version2.md) calls for is `updates.osSecurityAuto`, and it is attached to `apt.auto-upgrades-enabled`, not to this resource. It records **no reviewed version**, and that is a fact rather than an omission: the v1 frame does not have this package at all (open question 9), so there is no reviewed level to hold it above and the resource asserts presence alone.
- **Risk** — —
- **Notes** — **Turning the feature off therefore leaves the package installed with the two `APT::Periodic` switches at `0`.** That is the intended shape and it is stated here because it was previously only implicit: the package is inert with the switches off, `unattended-upgrades` is a dependency of nothing else the frame needs, and making the *package* the on/off resource would mean a purge-and-reinstall cycle every time an operator toggled the setting — an apt transaction, and under [§2.4](../version2.md) a reboot, to change two characters in a config file. It also keeps one diagnosis per resource: "the machinery is missing" and "the machinery is switched off" are different faults with different fixes, and the second is the one an operator chose. **Not present in the v1 reference.** Guide 12 step 6 was never applied to the frame that defines parity, so this resource has no v1 counterpart to diff against. See open question 9.

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

- **From** — set at flash time in [guide 2](../docs/2-sd-flash-first-boot.md); changed per unit in [guide 13 step 4](../docs/13-multi-device-deploy.md#4-give-the-clone-its-own-hostname)
- **Sets** — the system hostname (for example `framelink-douwe`) **and** the `127.0.1.1` line of `/etc/hosts` that maps that name back to loopback.
- **Observe** — `hostnamectl --static` **and** `getent hosts $(hostname)`, which must answer a loopback address (`127.0.1.1`). Both halves are required, and the second is the one that catches the real fault: the name check passes on its own while the frame is resolving itself off-machine.
- **Verify** — identical, after a reboot, like every other resource ([§2.4](../version2.md)).
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `device.hostname` (per-device override; the guide-2 pattern is `framelink-<recipient>`).
- **Risk** — **not brick-capable, and not brick-adjacent either.** The Act is `hostnamectl set-hostname` plus an idempotent `/etc/hosts` rewrite; nothing under `/boot/firmware` is touched, so none of the boot-partition write discipline applies.
- **Notes** — **Why this resource matters is `/etc/hosts`, not the name.** `hostnamectl` does not maintain that file. Measured on the mule 2026-08-15: after renaming to `framelink-mule`, `127.0.1.1` still read `raspberrypi`, no local entry existed for the new name, and resolution fell through to DNS — `getent hosts framelink-mule` answered `217.61.253.65   framelink-mule.huisman.io`. **The frame resolved its own name to a public internet address.** Anything resolving its own name — a service binding to it, a certificate, LiveKit's advertised media address — is pointed at a machine that is not this one, and the only warning is `sudo`'s `unable to resolve host`, which reads as cosmetic noise. Writing `127.0.1.1<TAB>framelink-mule` fixes it (`getent` then returns `127.0.1.1`) and it survived a second reboot. Half-applied is the dangerous state here; a merely wrong name is the mild one. One resource covering both files by sub-rule 1 — they are written together, cannot be acted on independently, and a content compare already names which half drifted.
  **The cloud-init trap this entry used to carry does not reproduce.** [Appendix B item 1](../version2.md) recorded the hostname as cloud-init managed and silently reverted at the next boot, and this catalog acted on that: the Act wrote cloud-init's NoCloud seed and the risk was called brick-adjacent because that write lands on `/boot/firmware`. Measured on the mule 2026-08-15, **the hostname survives a reboot** — `raspberrypi` → `hostnamectl set-hostname framelink-mule` → `framelink-mule` after a real reboot, with `boot_id` moving from `4b668b4f-…` to `fdb32d94-…` — and cloud-init logged nothing about hostnames on that boot. Two independent reasons why. First, **the shipped seed supplies no hostname**: `/boot/firmware/user-data` carries `#hostname: raspberrypi`, commented out, and `/boot/firmware/meta-data` holds only `dsmode: local` and `instance_id: rpios-image` with no `local-hostname`, so there is nothing to re-apply. Second, **cloud-init's `update_hostname` stands down once a human has taken over**: `/var/lib/cloud/data/previous-hostname` still reads `raspberrypi`, so the running hostname differs from the one cloud-init last recorded, and the module treats it as user-maintained and returns without acting. `preserve_hostname: false` in `/etc/cloud/cloud.cfg` does not override that.
  **A difference still to verify — hypothesis, not fact.** The mule was flashed with a raw image write, so its kernel command line carries **no `ds=nocloud;i=rpi-imager-…` parameter**; cloud-init found its seed through `seedfrom: file:///boot/firmware` instead. The v1 frame *was* Imager-flashed and does carry that parameter with an Imager-generated instance id (v1 inventory `KERNEL_CMDLINE`), and an Imager-written `user-data` contains a real, uncommented `hostname:` key. An Imager-flashed card may therefore genuinely re-apply the hostname where a raw-flashed one does not. **That is untested and must not be restated as established.** The honest position: the trap was observed once on a differently-provisioned system and does not hold on the stock image v2 bootstraps from — which is the image that counts.
  **What survives the disproof is [decision 26](../version2.md).** A write-only check would still have been wrong here, only about a different thing: `hostnamectl` returns success while the resource is half-applied at that instant. The reboot proves the whole state, not just the half the tool owns. [Guide 13 step 4](../docs/13-multi-device-deploy.md#4-give-the-clone-its-own-hostname)'s `raspi-config nonint do_hostname` writes `/etc/hostname` and `/etc/hosts` together and is therefore *more* correct than `hostnamectl` alone; it still is not the Act, because it is a competing owner (suspected-revert item 6) rather than because anything reverts it.
  **Scheduled 37th**, last in the system-configuration phase and ahead of the session and kiosk stack, because everything that binds to the frame's own name comes after it. It sat at 75 while it was believed to be a boot-partition write.

**`system.timezone`**

- **From** — [guide 2 step 9](../docs/2-sd-flash-first-boot.md) (Imager localisation); required as a fleet setting by [§3.4](../version2.md)
- **Sets** — the system time zone.
- **Observe** — `timedatectl show -p Timezone --value` **and** the cloud-init seed's `timezone:` directive if present.
- **Verify** — identical, after a reboot.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `locale.timeZone`.
- **Risk** — none established. Assume the Act is `timedatectl set-timezone`; it becomes a boot-partition write only if the seed turns out to own the value, and there is now no evidence that it does.
- **Notes** — **The "same owner as the hostname" argument has lost the case it rested on.** It was written when the hostname was believed to be silently reverted by cloud-init; that trap did not reproduce (see `identity.hostname`), so "cloud-init has a `timezone` module and Imager seeds it" is now an untested inference rather than a suspicion with a confirmed sibling. What is still true and still worth acting on: cloud-init does ship a `timezone` module, and nobody has read this image's seed for a `timezone:` key — the mule's `user-data` was read for `hostname` only, and that key was present *as a comment*. Read the seed for `timezone:` before designing an Act around it, and keep Verify after a reboot regardless, because [§2.4](../version2.md) requires that of every resource and not only of suspected reverts. Directly visible to the user — the 3 AM restart window and the slideshow both depend on local time.

**`system.locale`**

- **From** — [guide 2 step 9](../docs/2-sd-flash-first-boot.md); required as a fleet setting by [§3.4](../version2.md)
- **Sets** — system locale and keyboard layout.
- **Observe** — `localectl status`; `/etc/default/keyboard`; cloud-init seed.
- **Verify** — identical, after a reboot.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `locale.language` / `locale.keyboard`.
- **Risk** — —
- **Notes** — The cloud-init half of this is the same weakened inference as the time zone's, and for the same reason. The **keyboard** half is on firmer ground and does not depend on it at all: `console-setup.service` and `keyboard-setup.service` are enabled in the v1 reference and re-apply keyboard configuration at boot from `/etc/default/keyboard`, which is a competing owner evidenced by the inventory rather than by analogy — still untested as a revert, but reasoned from something measured.

**`boot.cmdline.wifi-regdom`**

- **From** — v1 inventory `KERNEL_CMDLINE` (`cfg80211.ieee80211_regdom=NL`); seeded at flash time
- **Sets** — the 802.11 regulatory domain kernel parameter in `/boot/firmware/cmdline.txt`.
- **Observe** — `grep -o 'cfg80211.ieee80211_regdom=[A-Z]*' /boot/firmware/cmdline.txt` **and** `/proc/cmdline`; `iw reg get`.
- **Verify** — identical.
- **dependsOn** — `agent.adoption`
- **Value source** — fleet setting `locale.wifiCountry`. No catalog default: a regulatory domain is a property of the country the frame is standing in, `NL` is the operator's own and not a universal, and the only value that would be safe everywhere (`00`) is the most restrictive one rather than a correct one.
- **Risk** — **brick-capable** (`cmdline.txt`).
- **Notes** — Low operational importance on a wired frame, high parity importance: it is part of the single `cmdline.txt` line that `boot.cmdline.fbcon-rotate` also edits, so both resources write the same file and must not fight. Any `cmdline.txt` writer must be a single line-aware editor, not two independent appenders. **The adoption edge is new**, and it is the `locale.*` family being made consistent: `system.timezone` and `system.locale` both declare it, this is the third member and the only one with legal consequences. It costs nothing in the ordering — adoption is position 5 and this resource is 79th — and the two writers of that one line are unaffected, since the early one is the display carve-out and needs no fleet value at all.

**`eeprom.config`**

- **From** — v1 inventory `EEPROM_CONFIG`; not set by any guide
- **Sets** — the Pi 5 bootloader EEPROM configuration: `BOOT_UART=1`, `POWER_OFF_ON_HALT=1`, `BOOT_ORDER=0xf461`.
- **Observe** — `rpi-eeprom-config`.
- **Verify** — identical.
- **dependsOn** — —
- **Value source** — fixed by the catalog.
- **Risk** — **brick-capable** (EEPROM). Recovery is a card swap at best, a recovery-image flash at worst.
- **Notes** — Included because it is parity state that the state-diff harness will compare and because `rpi-eeprom-update.service` is **enabled** in the v1 reference — an autonomous owner that can flash a newer bootloader and change this configuration without anyone asking. **Confirmed on the stock mule 2026-08-15:** `POWER_OFF_ON_HALT=1` and `BOOT_ORDER=0xf461`, matching the v1 reference, so these are stock-image values rather than anything a guide set. `POWER_OFF_ON_HALT=1` matters for the smart-plug power-cycle harness in [§5.1](../version2.md) in a specific way worth stating: `halt` genuinely cuts power, so a silent frame on a live relay has three explanations and not two — booting, hung, or halted and drawing nothing. The v1 inventory captures the EEPROM *config* but not the bootloader *version*; that gap is now closed by measurement — see open question 11, including the standing instruction **not** to update it.

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

- **Guide 11 step 1** (`python3-gpiozero`, `python3-lgpio`, `python3-websockets`) and **step 3** (`framelink-gpio.service`, `framelink-gpio.py`) — superseded by agent-internal GPIO handling and the agent's own local channel. The WebSocket server on `127.0.0.1:8889` is an internal detail of the v1 split between daemon and SPA; with both inside one binary there is no port. Three behaviours from the daemon must be **reimplemented, not dropped**: camera-service restart after every call-end, the 90 s/300 s/15 s kiosk-liveness watchdog, and `SIGUSR1`-equivalent simulated press for testing. The first two are supervision, specified in [§2.10](../version2.md).
- **Guide 12 step 2** (`~/chromium-watchdog.sh`) and **steps 3–4** (`chromium-watchdog.service`/`.timer`, `chromium-restart.service`/`.timer`) — superseded by agent supervision. The measured constants survive as fleet settings: tree RSS ceiling `1843200` kB, `MemAvailable` floor `358400` kB, five-minute interval, `OnCalendar=*-*-* 03:00:00` with `Persistent=true`. Their home is [§2.10](../version2.md), which names them `supervision.browserTreeRssCeilingKb`, `supervision.memAvailableFloorKb`, `supervision.memoryCheckInterval` and `supervision.dailyRestartTime`; see open question 4.
- **v1 inventory items that follow:** user units `chromium-watchdog.service`/`.timer`, `chromium-restart.service`/`.timer`, `framelink-gpio.service`, `framelink-spa.service`; `~/chromium-watchdog.sh`.

**Hardware facts nothing on the frame converges**

- **The array's firmware version, formerly `firmware.xvf3800.version`** — removed by decision 90 and
  reported instead by `ArrayFirmwareReporter`, beside the loop, on the same shape and for the same
  reason as the package inventory: it observes and reports and never acts. §2.3's contract is
  *Observe → Compare → Act (only on drift) → Verify*, and the only Act that could converge a firmware
  version is a DFU write. A resource that cannot act does not quietly report success — it spends its
  attempt budget, its reboots and an escalation, and by [decision 68](../version2.md) stops the whole
  pass, so a frame carrying a factory 2.0.6 array would never converge its screen, its camera or its
  speaker over a number nobody was going to let it write. The reading is taken twice: `bcdDevice`
  from the USB descriptor, which needs no control tool, no root and no control transfer, and
  `xvf_host VERSION` when the tool is installed. **Board revision is not readable at all** — it is in
  neither the USB descriptors nor any of the 177 commands in the pinned `libcommand_map.so`, all of
  whose identity commands (`VERSION`, `BLD_MSG`, `BLD_HOST`, `BLD_REPO_HASH`, `BLD_MODIFIED`,
  `BOOT_STATUS`, `SERIAL_NUMBER`, `DFU_GETVERSION`) describe the firmware or the unit rather than the
  board. It is silkscreen, so a fleet can never know it.
- **v1 parity is unaffected and stays where it was.** `reference/v1-state-inventory.txt` records
  `XVF3800_FIRMWARE` at `2 0 10`, and the parity harness's `audio.xvf3800.firmware` facet still
  compares it. What changed is that a difference there is now a parity *finding* for a person to
  read, rather than a desired value a frame will reboot three times trying to reach.

**Verification-only steps (they become Observe/Verify implementations, not resources)**

- Guide 4 step 6 (mixer readback after reboot) and step 7 (round-trip mic recording); guide 5 step 1 (`swapon --show`) and step 9 (three kiosk checks); guide 6 step 6 and step 7; guide 9 step 4; guide 10 step 5; guide 11 steps 4 and 5; guide 12 step 1 and step 9. Every `sudo reboot` is the reboot discipline itself, not a resource.

**v1 artifacts present in the parity reference that are not in any guide and must not be replicated**

- `sshd-mute-monitor.service` and `/usr/local/sbin/sshd-mute-monitor.sh` — the unit's own description says *"FrameLink testbed: sshd mute monitor (diagnostics, remove at fresh-flash)"*. It is **enabled** in the frozen v1 reference, so a naive state-diff will demand it on every v2 frame. Exclude explicitly.
- Home-directory debris: `audio-test.sh`, `noise-test.sh`, `speech-test.sh`, `gen-noise.py`, `fw-v2.0.10.bin`, `xvf-commands.txt`, `testaudio/`, `~/xvf3800/`, and the `*.png` screenshots. Diagnostic residue from the build sessions.
- `~/.bash_history` and the `.lgd-nfy0` FIFO.

---

## Proposed dependency ordering

Topological, with brick-capable resources scheduled last per [§5.5](../version2.md) — subject to the
two named exceptions below. Phase boundaries are for reading; the DAG is what the loop actually
orders.

**Exception 1 — the display group is scheduled first (operator decision, 2026-08-15).**
`boot.cmdline.fbcon-rotate` and `boot.config.dtoverlay-waveshare-panel` sit at positions **2 and 3**,
immediately after `agent.version` and **ahead of `agent.keypair` and `agent.adoption`**.
[§2.7](../version2.md)'s console narration and its ban on blank screens are the product's primary
honesty mechanism, and they are worth nothing without a lit panel: on a stock image there is no DSI
connector, no `/dev/fb0` and no backlight, and a write to the console succeeds while producing no
pixels — measured on `/dev/tty1`, and equally true of the `/dev/tty8` the stage renders on since
[decision 57](../version2.md), because it is the framebuffer that is missing and not any particular
terminal. Placing them ahead of adoption is deliberate rather than incidental —
[§2.6](../version2.md) renders `NotAdopted`, `ControlNotConfigured` and `NoContact` *on the frame*,
and an unadopted or unreachable frame is precisely the one whose screen has to work. **This narrows
§5.5's ordering clause by two resources; it does not repeal the rule.** Every other brick-capable
resource keeps its last slot, and the display group keeps every mitigation §5.5 attaches to the
rule — validate before writing, back up both boot files, boot-count self-repair — spelled out as the
write discipline at the head of the Guide 3 section. See open question 1 for the decision and for
the one part of it still outstanding.

**Exception 2 is withdrawn, and the resource it existed for is gone.** It used to place
`firmware.xvf3800.version` at position 54, ahead of the boot writes, on the reasoning that a DFU
flash bricks only the **mic array** while a boot-partition or EEPROM write can produce a device
nothing remote can reach. Decision 90 removed the resource: the only Act that could converge a
firmware version is a DFU write, this product will never perform one unattended, and a resource whose
Act cannot succeed does not report — it spends three attempts, three reboots and an escalation, and
by decision 68 stops the whole pass. **There is now no brick-capable resource anywhere in the array
chain**, and §5.5's ordering clause applies to the boot and EEPROM writes alone. Open question 2,
which asked where to schedule the flash against the mixer values it was said to validate, is answered
by there being nothing to schedule.

| # | Phase | Resources (in order) |
| ---: | --- | --- |
| 1 | **Agent root** | `agent.version` |
| 2–3 | **Display — §5.5 carve-out, earliest possible slot** | `boot.cmdline.fbcon-rotate` · `boot.config.dtoverlay-waveshare-panel` |
| 4–5 | **Agent roots — identity and adoption** | `agent.keypair` · `agent.adoption` |
| 6–22 | **Package set** | `pkg.labwc` · `pkg.chromium` · `pkg.wireplumber` · `pkg.pipewire-alsa` · `pkg.wlr-randr` · `pkg.xdg-desktop-portal` · `pkg.xdg-desktop-portal-gtk` · `pkg.gstreamer1.0-tools` · `pkg.gstreamer1.0-plugins-base` · `pkg.gstreamer1.0-libcamera` · `pkg.gstreamer1.0-pipewire` · `pkg.libspa-0.2-libcamera.absent` · `pkg.dfu-util` · `pkg.git` · `pkg.grim` · `pkg.unattended-upgrades` · `tool.xvf-host.installed` |
| 23–37 | **System configuration** | `system.timezone` · `system.locale` · `user.framelink.supplementary-groups` · `boot.autologin.getty-tty1` · `mount.tmp.tmpfs` · `journal.storage-persistent` · `swap.zram-active` · `swap.no-file-backed` · `apt.auto-upgrades-enabled` · `apt.unattended-upgrades.allowed-origins` · `audio.modprobe.snd-usb-audio-index` · `unit.cpu-performance.content` · `unit.cpu-performance.enabled` · `cpu.governor.performance` · `identity.hostname` |
| 38–47 | **Session and kiosk stack** (front-loaded per §2.7) | `session.bash-profile-exec-labwc` · `labwc.autostart.content` · `labwc.autostart.executable` · `labwc.rc-xml.touch-map` · `display.dsi2-transform` · `unit.xdg-desktop-portal.dropin-desktop` · `app.http.local-origin` · `unit.chromium-kiosk.content` · `unit.chromium-kiosk.enabled` · `unit.chromium-kiosk.running-matches-content` |
| 48–53 | **Camera chain** | `wireplumber.conf.camera-monitors-disabled` · `unit.framelink-camera.content` · `unit.framelink-camera.enabled` · `portal.permission-store.camera` · `portal.camera-interface-published` · `camera.pipewire-node.framelink-cam` |
| 54–61 | **Audio state** | `audio.xvf3800.gpo-x0d31-amp-enable` · `audio.mixer.pcm0-playback-switch` · `audio.mixer.pcm1-playback-switch` · `audio.wireplumber.playback-volume` · `audio.mixer.pcm0-playback-volume` · `audio.mixer.pcm1-playback-volume` · `audio.mixer.headset-capture-volume` · `audio.alsa.stored-state` |
| 62–75 | **Product layer** | `kiosk.binary.pinned-release` · `kiosk.offline-cache.dir` · `kiosk.config.immich-url` · `kiosk.config.immich-api-key` · `kiosk.config.offline-mode-enabled` · `kiosk.config.offline-asset-count` · `kiosk.listen-address` · `kiosk.process.supervised` · `app.config.identity` · `app.config.room` · `app.config.livekit-url` · `app.config.livekit-token` · `app.config.immich-kiosk-url` · `gpio.button.line` |
| 76–79 | **Brick-capable, unbootable risk — last** | `boot.config.camera-auto-detect` · `boot.config.dtoverlay-vc4-kms-v3d-noaudio` · `boot.cmdline.wifi-regdom` · `eeprom.config` |

**`identity.hostname` moved out of the last phase, from 75 to 37.** It was scheduled there because
the trap made its Act a `/boot/firmware` write; with the trap disproved the Act is `hostnamectl`
plus an `/etc/hosts` rewrite and the resource is not brick-capable at all. Its new slot is the end
of system configuration, immediately before the session and kiosk stack, so the frame is answering
to its own name — and resolving that name to loopback — before anything binds to it. The shift
cancels out at the tail: removing one resource from the final phase and adding one ahead of it left
`boot.config.camera-auto-detect`, `boot.config.dtoverlay-vc4-kms-v3d-noaudio` and
`boot.cmdline.wifi-regdom` where they already were, and the last phase is four resources rather than
five.

**`audio.wireplumber.playback-volume` then moved everything after the audio phase by one, and the
row labels above did not follow it.** It was added when the mixer revert was measured (decision 80),
and it lands inside the audio phase — so the two rows below it each start one later than they used
to. **Decision 90 then removed `firmware.xvf3800.version` from position 54 and moved everything after
it back by one again**, which is why the audio phase now begins there. The labels say what the
membership says: the product layer is 62–75 and the final phase 76–79, which is where
`boot.config.camera-auto-detect`, `boot.config.dtoverlay-vc4-kms-v3d-noaudio`,
`boot.cmdline.wifi-regdom` and `eeprom.config` actually sit, and the positions prose elsewhere in this
document cites have been moved with them. The arithmetic is checkable rather than asserted: the rows'
memberships sum to 79 with no gap and no overlap, and 79 is what `CatalogDocument.Parse` counts in
this file — a test compares the two, so a row label that drifts from its membership fails the
suite rather than sitting here being wrong.

**What moving the display early costs elsewhere.** Three things get worse, and they are worth naming
rather than smoothing over.

1. **Both boot files now have writers at opposite ends of the order.** `boot.cmdline.fbcon-rotate`
   runs 2nd and `boot.cmdline.wifi-regdom` 79th, editing the *same single line* of `cmdline.txt`;
   `boot.config.dtoverlay-waveshare-panel` runs 3rd while `boot.config.camera-auto-detect` and
   `boot.config.dtoverlay-vc4-kms-v3d-noaudio` run 77th and 78th in `config.txt`. The catalog already
   asked for one line-aware editor shared by every boot-partition resource; that is now a hard
   requirement rather than good practice, because the late writer must merge into a file the early
   writer has already changed and neither may re-serialise from a stale read.
2. **The display lines are now upstream of every apt operation in the provision.** They land at
   positions 2–3, while `pkg.*` runs 6–22 and `apt.auto-upgrades-enabled` at 31. `raspi-firmware`,
   `raspberrypi-sys-mods` and the kernel packages carry postinst hooks that regenerate `config.txt`
   and `cmdline.txt`, so the display group is exposed to a clobber for the whole remaining provision
   instead of being written after everything that could rewrite it. Level-triggered convergence
   handles that correctly — it is drift, and the loop repairs it — but the display resources can
   therefore reconcile more than once per provision, each repair costing another reboot, and this is
   the one respect in which the literal §5.5 ordering was genuinely safer. It is the same interaction
   already listed as suspected-revert item 7, now with a much longer exposure window.
3. **The riskiest write happens before persistent logging exists.** `journal.storage-persistent` sits
   at position 28, so the boot-partition writes at 2–3 run while the journal is still volatile and a
   failed boot takes its own evidence with it. What makes that acceptable is the agent's own progress
   journal under `/var/lib/fl-agent` ([§2.1](../version2.md)), which is on disk and survives — but for
   these two resources it is the *only* post-mortem, so it has to be flushed before the reboot rather
   than at the end of the cycle.

**Reboot cost, re-derived from measurement.** [§2.4](../version2.md) mandates a reboot per resource
with no exceptions. The earlier figure here — 40–60 s per cycle, **75–80 minutes** across 79
resources — was an estimate, and it was roughly **2.5× too pessimistic**. Measured on the mule
2026-08-15 and recorded in [§6.1](../version2.md): **22.3 s from `systemctl reboot` to SSH accepting
again**, taken twice (22.3 s and ~20 s) with loss of port 22 confirmed in between, so it is a real
round trip and not a connection that never dropped. At ~22 s a cycle, 80 resources cost **about 30
minutes of reboot overhead** — call the budget **30 minutes** to leave room for each resource's
Observe pass, which is a handful of cheap reads and not a second boot. Before apt download time
(~350 MB across the two package steps) a first bare-metal provision is therefore well under an hour
rather than the one-to-two-hour operation this section previously described.

**This is the number the reboot rule is argued against, so the correction matters beyond
arithmetic.** Cost was the strongest case for per-resource cleverness about which settings "really"
need a reboot — the exact reasoning [§2.4](../version2.md) blames for v1's governor bug — and the
real cost is under half what was assumed. [Decision 26](../version2.md) gets cheaper without
changing.

**The countdown is not a term in a provision's budget at all, and that is a decision rather than an
omission.** [§2.7](../version2.md) puts a countdown bar before each verifying reboot, defaulting to
60 s under [decision 48](../version2.md). Across 80 resources that is **80 minutes** — it would
dominate everything above, and roughly three quarters of a bare provision would be spent holding a
screen still for a reader who does not exist yet, since a frame being provisioned has never
displayed anything and nobody is standing in front of it.
[Decision 51](../version2.md) therefore scopes the pause to **drift repair**: a frame that has never
reached `InSync` reboots as soon as a resource is applied, so **a bare provision is the 30 minutes
above plus apply time, and nothing else**. The pause returns the moment the frame has been green
once — a drift repair on a live frame pays its full duration per reboot, because then there is
somebody watching and something being taken away. Two budgets, quoted separately: provisioning is
machine time; repair is machine time plus deliberate pacing, skippable per reboot and settable per
fleet or device. `--development` still forces zero, which is what keeps the pacing off the mule
after its first convergence.

**The carve-out does not change that total — it changes who can watch it.** The resource count and
the per-cycle cost are untouched, so the 30 minutes stands. What moves is the dark window: under
the literal §5.5 ordering the panel lit near position 77, so roughly 76 of the 80 cycles — around
half an hour — ran with nothing on screen and §2.7's narration became real only in the last minutes.
Under the carve-out the dark window is **three cycles** — `agent.version` plus the two display
resources themselves, the panel lighting on the overlay's own verifying reboot. That is the floor:
nothing can be shown before the overlay lands. The one thing the figure still does not include is
item 2 above —
a mid-provision clobber of the boot files adds reboots that were not possible when the display lines
were written last. Second-order and worth having: **an early brick is cheaper than a late one.** The
card swap §5.5 falls back on now costs three cycles of work rather than seventy-five.

---

## Suspected "silently reverted" settings

These are the settings whose write can appear to succeed while a different owner puts them back.
Every one of them can report `InSync` while being wrong if Observe reads what was written instead of
what is in force after a reboot.

**Read the tier headings literally.** This list was written around one confirmed archetype — the
hostname — and most of the entries below it were reasoned *by analogy* from that archetype rather
than measured. **The archetype has since been disproved** (see `identity.hostname`: the hostname
survives a reboot on the stock image, and cloud-init logs nothing about it), which does not make the
inferences false but does remove the thing that made them look safe to assume. The tiers are
therefore re-cut by **what kind of evidence exists**, not by how confident the entry felt when it
was written, so the next person can see at a glance what still needs testing. Nothing has been
silently demoted or dropped: every original entry is still here, at its original number.

**Measured on hardware — a revert or an ineffective route was actually observed**

1. **`identity.hostname` — DISPROVED as a revert; the real fault is a half-apply.** ~~cloud-init
   reverts the hostname at next boot.~~ Measured on the mule 2026-08-15: the hostname **survives**
   a real reboot (`boot_id` changed), cloud-init logged nothing about hostnames, the shipped seed
   carries no hostname at all, and `update_hostname` stands down because
   `/var/lib/cloud/data/previous-hostname` shows a human took over. What *is* measured and is a
   genuine fault is different in kind: `hostnamectl` does not maintain `/etc/hosts`, so the frame
   resolved its own name through DNS to a public internet address. **Not a competing owner putting
   something back — a second file the writer never wrote.** Full detail, including the untested
   Imager-versus-raw-flash hypothesis, is under the resource. Kept in this list at number 1 rather
   than deleted, because everything below was ranked against it.
2. **`cpu.governor.performance` — kernel-parameter route is ineffective.** Documented in
   [guide 12 step 7](../docs/12-systemd-and-reliability.md#7-pin-the-cpu-governor-to-performance)
   and cited in [§2.4](../version2.md): `cpufreq.default_governor=performance` reaches
   `/proc/cmdline` and the governor still comes up `ondemand`. The guide records this as *verified on
   hardware*, independently of the hostname and before it, so **this entry is untouched by the
   disproof and is now the list's only measured archetype.** The oneshot unit is the only route that
   works, and the governor value must be read separately from the unit's state.

**Item 4 belongs in this tier now, and is deliberately left at its own number.** `audio.mixer.*` was
confirmed on the frame 2026-08-16: the mixer really does have a second owner in the login session,
and it really does win. It is the list's second confirmed revert and the first entry whose own
reasoning turned out to be right about *that* there is one — though not about *which* mechanism, and
the difference changes the fix. The tiers are not re-cut around it, because the numbers are what
everything else in this document cites and item 1's whole lesson is that renumbering hides history.

**Inferred from the hostname case — the reasoning that supported these is now much weaker**

3. **`system.timezone` and `system.locale` — cloud-init.** ⚠ **This entry was pure analogy** —
   "same owner class" as a hostname trap that no longer exists. cloud-init does have `timezone` and
   `locale` modules and the five cloud-init units are enabled, but nobody has read this image's seed
   for either key, nobody has observed either value reverting, and the one cloud-init module that was
   actually watched declined to act. Do not treat "act on the seed" as settled; **read the seed
   first, then decide.** The keyboard half has a separate and better-evidenced owner that does not
   depend on this argument at all: `console-setup.service` and `keyboard-setup.service` are enabled
   in the v1 reference and re-apply from `/etc/default/keyboard` at boot. Untested as a revert, but
   reasoned from something measured rather than from the hostname.
**Reasoned from an owner evidenced independently of the hostname — untouched by the disproof, but
none has been tested either**

4. **`audio.mixer.*` — CONFIRMED 2026-08-16. Two owners, and the second one wins.** ~~What the
   evidence actually is: documented upstream WirePlumber behaviour plus a boot ordering, not an
   observation.~~ It is an observation now. `audio.mixer.pcm0-playback-volume` set `PCM,0=60`,
   rebooted, verified, and the value read afterwards was `Front Left=37 -23.00dB on, Front Right=37
   -23.00dB on [wireplumber active, 1 stored device files]`. The frame then repaired it and lost it
   again, repeatedly — see [§2.6](../version2.md)'s conflict drift and decision 78.

   **What the entry got right:** there are two owners, the guides configure only one, the second one
   is WirePlumber, it acts once the login session is up, and the symptom is the same quiet frame the
   hidden `PCM,1` stage produces. **What it got wrong is the part that decides the fix.** The entry
   assumed a *restore* from `~/.local/state/wireplumber/` and recommended owning or clearing that
   state. The observed number argues against it: this control is one step per decibel (60 = 0.00 dB
   in the v1 inventory, 40 = the −20 dB `PCM,1` ships at, 37 = −23.00 dB here), so 37 is a requested
   gain of −23 dB — and WirePlumber 0.5's *default* sink volume, `device.routes.default-sink-volume`
   = `0.064` linear, is −23.88 dB, whose nearest step at or above is exactly 37. **That is arithmetic
   on a documented constant, not a measurement**, and it is offered as the leading hypothesis only.
   If it is right, the value was never stored and writing the state file would have repaired
   nothing. The frame's own report of "1 stored device file" is consistent with a directory holding
   a profile or default-node record and no route volume, and it is now reported **by name** in every
   mixer observation, which is the one read-only reading that settles it and which nobody has been
   able to take by hand.

   **What was done about it, both halves.** *Ownership:* `audio.wireplumber.playback-volume` sets the
   volume through `wpctl`, which is correct whether the mechanism is a default or a restore — it
   overrides the first and causes the second to be written. *Timing:* every `audio.mixer.*` Observe is
   now behind the session gate, because the frame's post-boot verify runs at boot+10.0–10.6 s and the
   user manager comes up 0.03–0.7 s later (decision 65), so an ungated verify was a coin flip that
   passed on a value about to be wrong. **The original recommendation — "observe the mixer after the
   user session is up" — was right and is now implemented.**
5. **Network configuration — cloud-init plus NetworkManager plus netplan.** The v1 image carries
   `cloud-init`, `rpi-cloud-init-mods`, `netplan.io`, `netplan-generator`, `python3-netplan` and
   NetworkManager, with `NetworkManager.service`, `NetworkManager-wait-online.service` and
   `wpa_supplicant.service` all enabled. cloud-init writes `/etc/netplan/50-cloud-init.yaml` at boot
   and netplan renders it into NetworkManager. Any hand-written network file — including guide 13
   step 9's `nmcli` profile — sits downstream of a generator that reruns every boot. **What the
   evidence actually is:** the package set and the enabled units are inventory facts, so the
   *machinery* is certainly present; that it rewrites anything on this image is documented
   behaviour, not an observation. `/etc/netplan/50-cloud-init.yaml` is not captured in the v1
   inventory. One caution carried over from the hostname: the raw-flashed mule's NoCloud seed turned
   out to be nearly empty, so what cloud-init would render here from a seed that says almost nothing
   is genuinely unknown.
6. **`/etc/hosts` — raspi-config and cloud-init.** ✅ **The half-apply is now measured; the revert
   is not.** `hostnamectl` maintains `/etc/hostname` and **not** `/etc/hosts`, observed on the mule
   2026-08-15, with the consequence recorded under `identity.hostname`: `127.0.1.1` kept naming the
   old host, the frame resolved its own name through DNS, and the answer was a public internet
   address. So "any hostname resource must own both files or it will half-apply" is confirmed —
   and it is confirmed as a *gap in the writer*, not as a competing owner putting something back,
   which is a different failure and needs a different fix. The original claim that
   `raspi-config nonint do_hostname` and cloud-init's `manage_etc_hosts` can each rewrite the file
   remains true of both tools and untested on this image. **This entry is the one thing the old
   number-1 trap was pointing at that turned out to be real.**
7. **`/boot/firmware/config.txt` and `cmdline.txt` — package postinst plus unattended upgrades.**
   `raspi-firmware`, `raspberrypi-sys-mods` and the kernel packages carry hooks that regenerate these
   files. Turning on `apt.auto-upgrades-enabled` (guide 12 step 6) points an unattended writer
   straight at the brick-capable resources. This is a **new interaction between two guide steps that
   neither guide mentions**, and it is the most likely source of surprise drift on a long-running
   fleet. **Sharpened by the display carve-out:** the two lines that make the screen work are now
   written at positions 2–3, ahead of the package set and ahead of auto-upgrades being enabled, so
   they are exposed to both writers for the rest of the provision and for the life of the fleet.
   A clobber here is not cosmetic drift — it is the frame going dark, and the reconciler notices with
   no screen left to say so. Treat this as the highest-consequence entry in this list. **What the
   evidence actually is:** Debian and Raspberry Pi packaging behaviour — real, and independent of
   anything to do with the hostname — but no clobber has been observed on this hardware. It is the
   entry most worth spending a deliberate test on, because it is the one whose failure is invisible
   by construction.
8. **`eeprom.config` — `rpi-eeprom-update.service` is enabled.** An autonomous owner that can flash a
   newer bootloader at boot and change EEPROM configuration with nobody asking. **Measured on the
   mule 2026-08-15, and it complicates the entry rather than confirming it:** the running bootloader
   is dated **2025-12-08** while **2026-05-26** is available, so the enabled updater has had months
   and has demonstrably *not* fired. The capability is real; the claim that it acts unprompted on
   this image is not supported by the only frame anyone has looked at. Treat it as an owner that
   could act, whose actual trigger conditions (release channel, staged-firmware presence) nobody has
   established. See open question 11 — and do not update it to find out.

**Worth checking, lower confidence — none of these ever rested on the hostname case**

These five were reasoned from their own named owners and are unaffected by the disproof. They stay
exactly as written.

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
| 4 | `raspi-config nonint do_hostname` + reboot | **Device resource** `identity.hostname`, value from fleet setting `device.hostname`. The guide's command is not the Act, but the reason has changed: it is a competing owner the agent should not shell out to, **not** a command something reverts. What it gets right and `hostnamectl` alone does not is writing `/etc/hosts` alongside `/etc/hostname` — the half the resource now owns explicitly |
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
settled by the guides. Items 1 and 4 have since been settled by operator decisions and are kept here
with their resolutions rather than removed, so the reasoning is not re-derived later.

1. **The display overlay is brick-capable but is also the precondition for any visible output.**
   **— DECIDED 2026-08-15, in favour of [§2.7](../version2.md).**
   [§5.5](../version2.md) schedules brick-capable resources last; [§2.7](../version2.md) requires
   console narration — on `/dev/tty8` since [decision 57](../version2.md), on `/dev/tty1` when this
   was decided — "from the first second of the first boot" and forbids blank
   screens. With `boot.config.dtoverlay-waveshare-panel` scheduled 76th, the frame provisioned almost
   entirely with a dark panel and the operator saw nothing until the end.
   *Reading previously adopted:* keep the literal §5.5 ordering in the table above and flag the
   conflict, because changing it is an operator decision rather than a catalog one.
   *Decided:* the operator took the recommended carve-out. On-screen narration is the product's
   primary honesty mechanism and is worth nothing if there is no screen; the brick risk was raised
   explicitly and accepted. Hardware measurement on the stock mule settled it — no DSI connector, no
   `/dev/fb0`, no backlight device, `vc4-drm: [drm] Cannot find any crtc or sizes`, and writes to
   `/dev/tty1` that succeed while producing no pixels, so a naive console stage would have reported
   success while showing nothing (the stage renders on `/dev/tty8` since
   [decision 57](../version2.md), where the same holds — the missing device is `/dev/fb0`).
   `boot.cmdline.fbcon-rotate` and
   `boot.config.dtoverlay-waveshare-panel` now sit at positions 2 and 3, depend on `agent.version`
   alone, run ahead of adoption, and keep validate-before-write, a boot-partition backup of both
   files, and boot-count self-repair. Every other brick-capable resource keeps its last slot: the
   ordering clause of §5.5 is narrowed by two resources and none of its mitigations is weakened.
   Recorded as [decision 46](../version2.md), with §2.7 and §5.5 both pointing at the resolution so
   the conflict is not re-derived.
   *Still open, and it is the half the carve-out rests on:* **which boot-count self-repair
   mechanism.** An agent-written counter on the boot partition only repairs on a subsequent
   successful boot, which is no help when none happens; the bootloader's `tryboot` one-shot survives
   a frame that never boots again but has not been tested on this hardware. See the write discipline
   at the head of the Guide 3 section, and prove one before the first unattended bare-metal
   provision.
   *Measured on the mule 2026-08-15, and it leaves this exactly as open as it was:* **`tryboot` is
   not configured.** There is no `autoboot.txt` and no `tryboot.txt` / `tryboot.img` on the boot
   partition — the mechanism is simply not in use on this image. A Pi 5 bootloader almost certainly
   *supports* it, but that is an inference from the platform rather than something confirmed on this
   unit, and confirming it means writing to the boot partition and rebooting, which is the very act
   the mechanism exists to make safe. So decision 46's carve-out still leans on an untested
   self-repair path, and this remains the outstanding half. Space is not the obstacle: the boot
   partition has **430 MB free**, which comfortably holds a backup pair of `config.txt` /
   `cmdline.txt`, a boot counter, and a `tryboot` candidate at once.

2. **DFU firmware ordering versus the mixer values it validates.**
   **— ANSWERED 2026-08-23 by there being nothing left to order (decision 90).** The question was
   where to schedule a DFU flash that §5.5 wants last and that `audio.mixer.*` was said to depend on;
   the reading previously adopted split brick-capable resources by recovery cost and placed the flash
   just ahead of the audio block. The resource is now gone, so the ordering problem is gone with it,
   and the mixer resources have dropped the firmware edge. **The dependency it encoded was never
   measured in this repository.** Guide 4 asserts that 2.0.6-era and 2.0.10 firmware expose and
   default the DAC volume path differently; nothing here recorded a mixer reading on 2.0.6, and guide
   4 step 3's own EXPECTED OUTPUT is still a `[Pending fresh-flash capture]` placeholder. The claim
   may well be true — it is not evidence, and it cost a frame its whole pass. **What replaces it is
   a measurement:** the mixer resources read the controls they own, on whatever firmware answers, and
   a stage that is at the wrong level is drift like any other.

3. **How does the agent obtain `xvf_host` and the firmware images?**
   **— DECIDED 2026-08-16, as a commit-pinned fetch of six files (decision 63).**
   Guide 4 got both from an unpinned `git clone --depth 1` into `~/xvf3800`. The reading previously
   adopted here — the Immich Kiosk shape, a pinned checksum-verified upstream artifact under
   `/var/lib/fl-agent` — is what shipped, with **two corrections the investigation forced**.
   *First, the count was wrong:* the entry above said the binary and three sibling `.so` files, and
   Seeed's own `host_control/README.md` lists `dfu_cmds.yaml` and `transport_config.yaml` in the same
   directory, so the verified set is **six files**. *Second, there is nothing to pin in the ordinary
   sense:* measured 2026-08-16, the upstream repository has **zero releases and zero tags**, and
   `GET /releases/latest` answers **404** — the artifact is loose files on a moving default branch.
   So the pin is a **commit SHA**, `725f38464e73477a30aba9f5c220f1cfdc66d682`, and every download is
   a `raw.githubusercontent.com` URL built from it, which is content-addressed and therefore
   immutable; the six SHA-256 digests in `XvfHostReleasePin.Current` are the second lock and were
   measured, not published, because this publisher publishes none. `pkg.git` loses its last consumer
   (see its entry above). The ledger gained a `github-path-commit` probe kind to watch this upstream
   at all, since `github-release` cannot; the entry is `xvf-host-tool` in `upstream-review.json`.
   **What was rejected, and must stay rejected:** vendoring the six files into this repository
   (unlicensed — upstream carries no licence file at all, and the XMOS terms the tool appears to be
   built under forbid standalone redistribution), and building `xvf_host` from XMOS's
   `host_xvf_control` source, which would make this project an XMOS Licensee bound by
   no-derivative-works terms. [Appendix A decision 63](../version2.md) is where that reasoning lives
   and it is load-bearing, not commentary.

4. **Where does browser and camera supervision live?**
   **— DECIDED 2026-08-15, as supervision.**
   [§2.2](../version2.md)'s loop is level-triggered convergence of *declared state*. "Chromium's
   process tree exceeds 1.8 GB", "the SPA socket has been silent for 90 seconds", "the camera node
   wedged after a call" and "restart the browser every day at 03:00" are none of them drift of a
   declared setting, yet all four are load-bearing and measured. v2 named no home for them.
   *Reading previously adopted:* they are **supervision**, a second agent responsibility alongside
   reconciliation, with their constants exposed as fleet settings — excluded from this catalog on
   that basis. If instead they were meant to be resources, they would need a status vocabulary
   distinguishing "converging" from "supervising", because a browser restart is not drift and should
   not stop the product under [§2.6](../version2.md).
   *Decided:* the operator adopted that reading, and it is now [§2.10](../version2.md) and
   [decision 47](../version2.md). Supervision is a second agent responsibility standing beside the
   reconciliation loop, distinguished by one question — *is the desired state wrong, or is a
   correctly-configured thing misbehaving?* The deciding argument is the collision the alternative
   creates: [§2.6](../version2.md) says any drift stops the product, including an active call, and a
   routine browser restart must not, so keeping the two separate lets each rule stay absolute
   instead of forcing one to yield. Consequences the catalog depends on: the four behaviours stay
   **out** of this catalog (they are not resources); the measured constants become `supervision.*`
   fleet settings under [§3.4](../version2.md); the camera recycle is the same responsibility on an
   event trigger rather than a health check; supervision never reboots; a supervised restart while
   `InSync` leaves the device `InSync`, annotating the [§2.6](../version2.md) ladder rather than
   adding a rung; and an unrecovered supervision action becomes ordinary drift once
   `supervision.recoveryDeadline` expires, which is the handoff back to reconciliation.
   *Still open:* the interlock is specified but unbuilt — [§2.10](../version2.md) requires
   supervision to skip anything the reconciler is `Progressing`, `AwaitingReboot` or `Blocked` on,
   and requires the reconciler to treat an open supervision window as expected rather than drift.
   Both need the M2 engine to expose per-resource state to the supervisor; verify it when the kiosk
   and camera resources first come under supervision.

5. **Are agent-internal tunables resources?** Countdown duration, watchdog thresholds, the 03:00
   restart time, the button pin, backoff parameters. [§2.8](../version2.md) makes the applied *version*
   an ordinary resource, which argues yes by analogy; but a value the agent holds in memory has no
   independent drift surface, which argues no.
   *Reading adopted:* fleet settings, not resources — except where they have an observable on-device
   footprint, which is why `gpio.button.line` is a resource (the claimed GPIO line is visible in
   `gpioinfo`) and the watchdog thresholds are not. **Confirmed for the watchdog constants and the
   03:00 restart by [decision 47](../version2.md)**, on exactly this reasoning: [§2.10](../version2.md)
   makes them `supervision.*` fleet settings because nothing on disk holds them. The countdown
   duration, backoff parameters and the rest of the tunables are unaffected and stay as read above.

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
    resource is probably warranted but cannot be specified without a captured baseline.
    *Baseline captured 2026-08-15, on the mule:* the running bootloader is dated **2025-12-08** and
    the latest available is **2026-05-26**, so the dev mule is **several releases out of date** and
    the enabled auto-updater has not acted on it in months. That is now a known state of the mule
    rather than a surprise waiting for whoever next reads a boot log, and it also weakens suspected-
    revert item 8, which assumed that updater was an active autonomous owner.
    **Do not update it.** An EEPROM write is brick-capable and its recovery is a card swap at best
    and a recovery-image flash at worst; there is no v2 requirement that the mule run current
    bootloader firmware, and taking that risk to close a documentation gap is a bad trade. When a
    `firmware.rpi-bootloader.version` resource is eventually specified, this date pair is its first
    data point and the frame it was measured on is still available to re-read.

12. **`app.config.immich-kiosk-url` disagrees with itself in the parity reference.** The running
    frame's `config.json` lacks `use_offline_mode=true` while `app/config.example.json` includes it.
    One of them is the desired value and the other is live drift in the artifact that defines parity.
    *Reading adopted:* the example file is correct — offline serving is a stated product requirement
    ([§2.6](../version2.md): an outage in the operator's house must never blank a frame in someone
    else's) — so the running frame is drifted and the catalog's desired value includes the parameter.

13. **Is the speaker amplifier on by default on the *shipping* 2.0.6 array firmware?**
    **— ANSWERED 2026-08-20 on the bench: yes.** A factory 2.0.6 array (serial `…030`) and an
    upgraded 2.0.10 array (serial `…069`) both read `GPO_READ_VALUES 0 0 0 1 0`, three stable
    readings each, on a frame whose agent had been stopped before the array was attached so that the
    pin could not have been written by anything. The third value is `0` and the pin is active-low, so
    **the amplifier is on at boot on both firmware levels**. A factory-fresh array is therefore
    untuned rather than silent, and the severity argument below — that `xvf_host` might be
    load-bearing for a first-boot unit — does not apply. The original question and its reasoning
    are kept below as the record of what was asked. Guide 4 step 4
    measured it on **2.0.10** only — "boots with `X0D31` low, so the amp is effectively enabled out of
    the box" — and the guide's step order means the first speaker test happened *after* the DFU
    flash, so it says nothing about a factory-fresh array. `research/camera-audio.md` asserts the
    opposite for the general case ("must be enabled via `xvf_host` command"), and it is a
    pre-decision note rather than a measurement. Seeed's own `host_control/README.md` shows a stock
    readback of `GPO_READ_VALUES 0 0 0 1 0`, whose third value is `0`, while the prose beside it says
    the opposite of its own data and its own pin table.
    *Why it is not resolved by picking a side:* nothing in v2 depends on the answer, and that is
    deliberate. `audio.xvf3800.gpo-x0d31-amp-enable` **reads the pin and writes it when it is not
    `0`**, so it converges whichever way a fresh array boots; no code anywhere assumes a default.
    What the answer changes is the *severity* of a frame that cannot fetch `xvf_host`: the amp
    resource sat behind `firmware.xvf3800.version`, which sits behind `tool.xvf-host.installed`, so
    on a factory-fresh 2.0.6 array with no tool the pin is never asserted. If 2.0.6 boots with the
    amp enabled, that frame is merely untuned; if it boots with the amp disabled, that frame is
    **silent**, and the tool moves from nice-to-have to load-bearing for a first-boot unit.
    *The experiment:* on a bench array that has never been flashed, run `xvf_host GPO_READ_VALUES`
    and read the third value, then play a tone without writing anything. Five minutes, one unit, and
    it settles the question permanently.
