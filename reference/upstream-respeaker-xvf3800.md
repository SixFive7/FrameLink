# FrameLink v2 — The reSpeaker XVF3800 upstream

Everything this project takes from [respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY)
comes out of a repository with no releases, no tags, no licence, one filename that has carried two
different binaries, and a prebuilt control tool that is thirteen months older than the firmware it
is used to talk to. None of that is a reason to stop using it — there is no alternative source for
this hardware — but every one of those properties has already cost this project a workstream, and
each of them dictates a specific rule in the code. This file is the record of what was measured and
the rules it forces, so that the next person does not rediscover them.

This is reference material, not a build guide: the seven-block step structure of
[CLAUDE.md §2.1](../CLAUDE.md) does not apply here. The link and honesty rules do.

---

## Provenance

**Every fact below was re-measured against the live upstream on 2026-08-24**, not copied forward
from an earlier session. The method is named beside each finding so it can be re-run. Where a claim
rests on inference rather than measurement it says so in the sentence that makes it, and the two
places in this repository that overstate their evidence are named in
[What has already been corrected](#corrections) rather than quietly repeated.

The re-measurement used the GitHub REST API, `raw.githubusercontent.com` at explicit commit SHAs,
and `sha256sum` on the downloaded bytes. Nothing was flashed, no array was touched, and no hardware
reading in this file is new — the device-side readings are this project's own captures of
2026-08-20 and 2026-08-23, cited as such.

---

## 1. Zero releases, zero tags, and what that costs

**Measured 2026-08-24.** `GET /repos/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/releases/latest`
answers **404**. `GET .../releases` answers `[]`. `GET .../tags` answers `[]`. The repository has
**35 commits** in its entire history and has never cut a release or laid down a tag.

The consequences are structural, not cosmetic:

- **There is no version to compare against**, so nothing upstream can answer the question *is this
  newer than what I have*. A pin can only be a commit SHA plus a content digest.
- **The release checker's `github-release` probe cannot see this upstream at all.** Registering it
  under that kind would have produced a permanently 404-ing probe, and by
  [version2.md §7.1](../version2.md) an unreachable probe blocks a release exactly like a move
  does — so the tool would have blocked every future release on a question it could never answer.
  Leaving it unregistered was worse still, because a ledger silent about a dependency reads
  identically to a ledger that has nothing to say about it. That is why
  `tools/FrameLink.Upstream` grew a fourth probe kind, **`github-path-commit`**, which asks
  `commits?path=<path>&per_page=1` for the newest commit touching one path. It exists for this
  upstream and for nothing else.
- **The artifact moving and the repository moving are different events**, and only the first is
  news. All four XVF entries in `upstream-review.json` therefore watch a path, never a branch.

## 2. One version number, two different binaries

This is the single most important fact about this upstream, and the reason every rule in
[How to deal with it](#dealing) is phrased the way it is.

`xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin` has been published **twice,
under one filename, with different bytes**:

| commit | committed | sha256 of the file at that commit |
| --- | --- | --- |
| `17bac32a1b401bc0e9dcee5e101fe845cb54e3c0` | 2026-06-29 | `237f762a55624dbbd8c2f32d89760140b8cd741dd23027753fb7786141d95fe9` |
| `aeacafab4397088b39d2e3c1b0757cd8d56ad358` | 2026-07-13 | `81593709500cf02ca209fbfb028030ddc5438763ceaf5fe9019a3164705af843` |

**Re-measured 2026-08-24** by downloading both from `raw.githubusercontent.com` at those SHAs and
hashing them. Both files are exactly **933,888 bytes**. **402,246 bytes differ — 43.07%.** The
**first difference is at offset `0x4`**: the first four bytes are identical (`11 af 7a c0`) and the
fifth byte onward diverges, so whatever sits immediately after the magic is per build.

Both commits are real firmware changes, which is why the bytes must differ. Their messages say so:
`17bac32a` is *"restore audio descriptors and buffers on bus reset"*, `aeacafab` is *"decoupling
DOA state from effect"*. Neither is a re-upload of the same build.

**What is known about telling them apart, stated precisely, because the loose version of this
sentence is wrong.** `VERSION` and `bcdDevice` are derived from the version number, which is
identical, so neither can distinguish the two builds. What has actually been *measured* is one
board — Frame #1, 2026-08-23, `fl-agent` stopped, read twice nine minutes apart and byte-identical
both times:

```text
VERSION 2 0 10
BLD_REPO_HASH 3f08f630b41b8bce11cb2f45857ba49f22f9d507
BLD_MSG ua-io16-sqr
BLD_HOST NA
BLD_MODIFIED TRUE
```

Nobody has ever flashed the June build and read it back, so **which build that reading belongs to
is unknown**. Frame #1 was flashed on 2026-08-08 from master, and master has served only the July
build since 2026-07-13, so it is *probably* the July one — that is inference from dates and not a
measurement. `BLD_REPO_HASH` is a stable, reproducible per-build fingerprint, and it very likely
*does* distinguish the two builds; nobody knows, because there is nothing to compare it against
yet. The decisive experiment is one flash away and costs nothing extra: **the next time an array is
flashed, flash it from one named published file and read `BLD_REPO_HASH` afterwards.** Equal to
`3f08f630…` means Frame #1 runs that file's build; unequal means it runs the other one. Either
outcome closes the question, and it makes every future flash self-documenting.

**Neither published `.bin` can be tied to a source commit by inspection**, and the reason is
structural rather than a lack of effort. Neither file contains the hash as ASCII, upper-case ASCII,
20 raw bytes, word-swapped, under any of 256 XOR constants or 255 ADD constants, bit-reversed, or
at strides of 2, 3, 4 or 8; neither contains the string `ua-io16-sqr`. There are only 331 printable
ASCII runs of six or more characters in 933,888 bytes and not one is a firmware string. The layout
explains it: a 98-byte header, then bytes `0x62`–`0x33fa5` identical between the two builds at
1.835 bits/byte, then a body at 5.649 bits/byte whose non-padding bytes run 7.327 bits/byte. The
application payload is compressed — which is simultaneously why no string is findable and why a
small source change flips 43% of the file. Going further would mean decompressing an XMOS boot
image, and [decision 63](../version2.md) forecloses that deliberately: staying outside XMOS
Licensee terms is a precondition of the native-protocol work.

## 3. The changelog presents two publications as one release

**Fetched 2026-08-24** from `xmos_firmwares/usb/changelog.md`. Under the single heading `## v2.0.10`
it lists **three** fixes:

1. USB audio recovery after bus resets and speed changes.
2. A Linux capture failure reporting isochronous `EOVERFLOW` after a full-speed to high-speed change.
3. DOA state tracking decoupled from the active LED effect.

The first two belong to the June commit and the third to the July one, going by the commit
messages. The changelog is the union of both, with **no note anywhere that the version was
published twice**, and no way for a reader to tell which fixes their copy contains. Anyone who
cloned between 29 June and 13 July has a binary missing the third fix while the changelog tells them
they have it. This is the most likely explanation for at least one confused upstream user, and it is
what the comment on [issue #32](#seeed) asks Seeed to resolve.

## 4. No licence, in any commit

**Measured 2026-08-24** by walking the tree of **all 35 commits** in the repository's history and
collecting every distinct blob path that has ever existed — **57 paths**. Filtering them for
`licen[cs]e`, `copying`, `notice` or `copyright`, case-insensitively, returns **zero matches**. The
repository root at head holds `README.md`, `doc/`, `host_control/`, `python_control/` and
`xmos_firmwares/` and nothing else, and the README is 302 bytes and mentions no licence terms.

**Consequences, and this is a legal question rather than a technical one.** There is no grant, so
redistribution of any file from this repository is unlicensed. That is why this project **fetches
and verifies rather than vendoring**: `xvf_host` and its five sidecar files, and the three DFU
images, are downloaded onto a frame at a pinned commit and hash-checked, and not one byte of them is
committed here. The prebuilt tool additionally appears to be built under XMOS terms that forbid
making the software available on a standalone basis while expressly permitting shipping it installed
on devices, which points the same way. Any future proposal to vendor these files is a question for a
lawyer, not a convenience.

## 5. The firmware's own source repository does not exist in public

The firmware reports `BLD_REPO_HASH 3f08f630b41b8bce11cb2f45857ba49f22f9d507`, documented upstream
as *"the GIT hash of the sw_xvf3800 repo used to build the firmware"*.

**Measured 2026-08-24 and previously 2026-08-23:** `api.github.com/repos/xmos/sw_xvf3800` answers
**404**. `github.com/xmos/sw_xvf3800` answers 404. A GitHub repository search for `sw_xvf3800`
returns nothing from any owner; XMOS publishes exactly one XVF repository and it is
`host_xvf_control`. A commit search for `3f08f630b41b8bce11cb2f45857ba49f22f9d507` returns nothing,
and neither does a web search. XMOS's own build documentation describes the firmware as a **source
release package** rather than a repository, which is what makes the field unresolvable rather than
merely unindexed.

**And even a public repository would not help, because the builds are dirty.** The same board reports
`BLD_MODIFIED TRUE`, whose upstream wording is *"whether or not the current firmware repo has been
modified from the official release"*. These images were built from a modified working tree. A git
hash can therefore never be a byte-level identity for them — at best it names a base commit — and no
future access to `sw_xvf3800` would change that. This is a permanent property, not a gap waiting to
close.

`BLD_HOST` is `NA`, so the build host is not recorded either and corroborates nothing.

## 6. Nine firmware images in one directory, and the default is the unnamed one

**Listed 2026-08-24.** `xmos_firmwares/usb/` holds **ten entries: nine `.bin` images and a
changelog.** Every image is exactly 933,888 bytes.

```text
respeaker_xvf3800_usb_dfu_firmware_v2.0.6.bin
respeaker_xvf3800_usb_dfu_firmware_v2.0.7.bin
respeaker_xvf3800_usb_dfu_firmware_6chl_v2.0.8.bin
respeaker_xvf3800_usb_dfu_firmware_v2.0.9.bin
respeaker_xvf3800_usb_dfu_firmware_v2.0.9_48k.bin
respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin
respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin
respeaker_xvf3800_usb_dfu_firmware_v2.1.0_16k6ch.bin
respeaker_xvf3800_usb_dfu_firmware_v2.1.0_48k2ch.bin
```

Versions overlap, variants are interleaved with plain builds, and **the suffix names a departure
from the default profile rather than the default being named**. `_16k6ch`, `_48k2ch`, `_6chl` and
`_48k` each announce a change of audio topology; the unsuffixed file announces nothing, and is the
16 kHz two-channel build. Flashing the wrong one would change the frame's channel count or sample
rate underneath every mixer resource in the catalog, silently, while the version number stayed the
same.

**Never assume the unsuffixed name is the default without corroboration.** For v2.1.0 that
corroboration is four independent readings and is recorded in [decision 91](../version2.md): Seeed's
wiki states the 2-channel/6-channel split for the 2.0.x line; upstream's own 2.0.8 changelog *adds*
the six-channel `ua-io16-6ch-sqr` profile against the base profile Frame #1 reports as
`BLD_MSG ua-io16-sqr`; the 2.1.0 filenames spell both departures out; and byte-wise `v2.1.0` and
`v2.1.0_48k2ch` are the closest pair in the directory at 30.03% differing, against 46.17% between
`v2.1.0` and `v2.1.0_16k6ch`, which is what two builds sharing a channel topology and differing only
in sample rate look like. The frame agrees from its own side —
[the v1 state inventory](v1-state-inventory.txt) records this array's ALSA `Capture Channel Map` with
`count 2`, enumerated by PipeWire as *Analog Stereo*.

There is also a second directory, `xmos_firmwares/i2s/`, holding seven more images on a completely
separate `v1.0.x` version line. Nothing in this project uses them, and a probe pointed at a
directory rather than a file would report movement every time one of them changed.

## 7. The prebuilt control tool is older than the firmware it talks to

**Measured 2026-08-24, and this had not previously been recorded anywhere in this repository.**

The `libcommand_map.so` this project pins was last touched on **2025-07-04** at commit
`725f38464e73477a30aba9f5c220f1cfdc66d682`. The copy upstream serves **today at master head**
(`a652fe79da3a292b25decc0e1e7f267d29bb0284`) is **byte-for-byte identical** to it — same 151,680
bytes, same sha256 `c1b424313e48cfe97c5cfce0530ac05fe47f818cc0fba15a9954198ef105282c`. The binary
control tool's command table has not been rebuilt in thirteen months.

The firmware has moved four times in that window, and each move added commands. Extracting every
uppercase-identifier string from the pinned `libcommand_map.so` yields **177 distinct names**, and
the ones the firmware has since gained are simply **not in it**:

| Command | Added by firmware | Present in `libcommand_map.so` |
| --- | --- | :---: |
| `DOA_VALUE` | v2.0.6 (2025-11) | no |
| `LED_RING_COLOR` | v2.0.7 (2025-12) | no |
| `AIC3104_HP_LEVEL` | v2.1.0 (2026-08) | no |
| `AIC3104_LINEOUT_LEVEL` | v2.1.0 (2026-08) | no |
| `AUDIO_MGR_OP_CH3`–`CH6` | v2.1.0 (2026-08) | no |

**Upstream maintains two control-tool implementations that disagree about which commands exist.**
`python_control/xvf_host.py` carries its own hardcoded table of **117** commands and *does* include
`DOA_VALUE`, `LED_RING_COLOR`, `AIC3104_HP_LEVEL` and `AIC3104_LINEOUT_LEVEL` — the four the binary
lacks. It is missing `AUDIO_MGR_OP_CH3`–`CH6` as well, so it is also behind its own changelog, and
it lacks the 64 DFU, GPI and MUX names the binary carries. Neither table is a superset of the other.

Three consequences worth carrying forward:

1. **Anything added to the firmware after 2025-07-04 is unreachable through the pinned
   `xvf_host`.** Nothing this project does today needs those commands — the agent sends only
   `VERSION`, `GPO_READ_VALUES` and `GPO_WRITE_VALUE` — but any future AEC or DSP tuning work
   should check the command map before assuming a documented command can be issued.
2. **This strengthens the SOMEDAY item in `TODO.md`** about speaking the protocol natively. The
   Python reimplementation is the better specification precisely because it is the one being kept
   current, and implementing the wire protocol removes the dependency on a binary that upstream
   appears to have stopped rebuilding.
3. **The number 177 is a count of command-name-shaped strings, not a curated command list.** A few
   are ELF and linker artefacts (`ELF`, `GNU`, `BDF`, `BPE`, `BXF`) and some are enum members
   rather than commands. The count is reproducible by the same method, and the substantive claim it
   supports is the one in the next section.

## 8. Board revision is not readable in software

**Re-measured 2026-08-24 against both command tables.** Filtering the 177 names in the pinned
`libcommand_map.so` and the 117 in `xvf_host.py` for `BOARD`, `REVIS`, `_REV`, `HW_`, `HARDWARE`,
`PCB`, `VARIANT` or `MODEL` returns nothing that describes the board. The only hits are DSP
noise-model commands — `SPECIAL_CMD_PP_NLMODEL`, `PP_NL_MODEL_CMD_ABORT` and their siblings.

Every identity command in the set describes the **firmware** or the **unit**, never the board:
`VERSION`, `BLD_MSG`, `BLD_HOST`, `BLD_REPO_HASH`, `BLD_MODIFIED`, `BOOT_STATUS`, `SERIAL_NUMBER`,
`DFU_GETVERSION`. Nor is it in the USB descriptors.

**Board revision is silkscreen.** A fleet can never know it, no flash logic can ever be gated on it,
and the largest single risk in [decision 91](../version2.md) — that a firmware might not boot on a
given board revision — therefore has **no software mitigation**. That is why the sequencing in that
decision is binding rather than advisory.

## 9. What the open issues actually say

Read in full on 2026-08-24. Read them yourself before relying on any summary, including this one —
a previous workstream was handed a summary of #18 that overstated it.

**[Issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32)** — open,
opened 2026-08-17, 2 comments. Its **primary** subject is not the firmware boot failure it is
usually cited for. It reports a board revision **V1.1** that **stops enumerating over USB entirely
in normal mode** — no `2886:001a`, no kernel event — while the firmware itself clearly runs (LED
ring lit, DOA active), and Safe Mode enumerates as DFU first try every time. The documented
`4mb_all_ff.bin` recovery does not fix it because **alt 2 (DataPartition) rejects both upload and
download at offset 0**, so the configuration cannot be cleared.

The often-quoted part is a table inside it, and it needs its context: v2.0.6, v2.0.7 and v2.0.9 boot
on that unit but do not enumerate; **v2.0.10 and v2.1.0 do not boot at all, LEDs dark**. The
reporter's own conclusion is that *"the failure is independent of firmware version"* — the board is
broken on every version tried. A follow-up comment from the reporter adds that **the onboard RST
button on that board is physically broken and detached**.

So the evidence that 2.0.10 and 2.1.0 do not boot on V1.1 is: **one unit, already faulty in a way no
firmware fixes, with known physical damage to the reset line.** That is a real datum and it is the
only one anybody has, but it is a long way from a hardware-revision constraint. Frame #1 in this
project is a V1.1 board running 2.0.10 successfully — it enumerates, answers `VERSION 2 0 10`, and
has carried a real call with 1,811 decoded video frames. The honest position is the one
[decision 91](../version2.md) takes: this complicates the report rather than refuting it, the risk
is unquantified, and Safe Mode must be rehearsed before anything is flashed.

Two procedural details from #32 are worth keeping because **one of them is not in the upstream
instructions**: the `all_ff` erase **terminates at about 96%** (4,030,464 bytes) with
`dfuERROR status(8) … out of range` and that is the expected outcome, because the image is 4 MiB and
the partition is smaller; and a **power cycle is required** between the erase and the next write, or
the download fails at 0% with the same status.

**[Issue #18](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/18)** — open,
opened 2026-05-18, **zero comments, no maintainer response in over three months**. Read carefully,
because it is routinely overstated.

What it actually reports: after experimenting with `LED_EFFECT`, LED colour/speed/brightness,
`GPO_WRITE_VALUE`, `CLEAR_CONFIGURATION` and `SAVE_CONFIGURATION`, a unit lost its microphones
(recording is complete silence), its mute LED changed behaviour, its DOA indicator froze, its JST
speaker output went silent, and macOS playback became unstable. The faults **survive multiple DFU
reflashes and `CLEAR_CONFIGURATION`**.

What it does **not** report: a dead device. The unit still enumerates as a USB audio card, and
`xvf_host` still answers `VERSION`, `BOOT_STATUS`, `LED_EFFECT`, `GPO_READ_VALUES` and both
`USB_*_BUFFER_STABLE` reads. "Made unusable" is too strong; **severely and persistently degraded,
with codec and DSP state that a firmware reflash does not reset**, is what the report supports. It
is also a different hardware combination — an XVF3800 array with a XIAO ESP32S3 — on firmware
v2.0.7.

One cross-check this project can contribute: the reporter flags
`GPO_READ_VALUES → [0, 0, 0, 1, 0]` with the guess *"LEDs powered off?"*. Measured on the bench
2026-08-20, **a factory 2.0.6 array and an upgraded 2.0.10 array both read exactly
`GPO_READ_VALUES 0 0 0 1 0`.** That reading is the normal state on a healthy array, so it is not
evidence of anything on that unit.

**[Issue #8](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/8)** — closed
2026-08-14, opened 2025-11-14, 6 comments. Title: *"After running `save_configuration 1`, device no
longer enumerates as USB in normal mode (only Safe Mode works)"*. This is where the
`4mb_all_ff.bin` recovery came from: upstream committed that file on 2025-11-18, four days after the
issue was opened and the same day the maintainer posted the procedure, in the only commit that file
has ever had.

**The pattern across #8, #18 and #32 is worth stating plainly**: the two most severe reports both
follow a `SAVE_CONFIGURATION`, and both describe state that a firmware reflash does not clear. This
repository sends `SAVE_CONFIGURATION` **nowhere at all**, and that is a deliberate standing rule
([decision 91](../version2.md), and the loudness item in `TODO.md`), not an oversight.

## 10. Maintenance signal

**Measured 2026-08-24.** The repository has **28 issues**, of which **20 are open**, and **11 of
those 20 have zero comments** — including #18, open since May. Firmware commits arrive through pull
requests from a contributor fork rather than being pushed directly. There is no release cadence, no
tag, no milestone and no changelog entry for anything but firmware.

This is not an argument for abandoning the dependency; it is the calibration for how long to wait
for an answer. **Assume no reply**, design so that no reply is survivable, and treat any response as
a bonus.

---

<a id="dealing"></a>

## How to deal with it

Seven rules. Each exists because of a numbered finding above, and each is already implemented
somewhere in this repository.

1. **Pin by commit SHA and content digest, never by version string or branch.** A raw URL carrying a
   full commit SHA is content-addressed, which is the only thing that makes a pin possible with no
   release to point at. The digests are measured here rather than published — this publisher
   publishes none, and nobody signs a `checksums.txt` either, so a measured digest is no weaker.
   Implemented in `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` and
   `src/FrameLink.Agent/Resources/XvfHostRelease.cs`.
2. **Probe the file path, never the directory.** For the three firmware images this is the only
   probe that would catch a third republication of one filename, and a directory probe would report
   movement every time upstream adds an image for any product variant — which it did three times in
   2026. The one exception is deliberate and reasoned: `xvf-host-tool` probes the
   `host_control/rpi_64bit` **directory**, because it wants to hear about Seeed rebuilding the tool
   at all, and that directory moves roughly once every thirteen months. Over-sensitive is right
   there and wrong for firmware.
3. **Treat the version string as a label, not an identity.** `v2.0.10` names two different binaries.
   Anywhere a version number is compared — a resource, a parity facet, a report — ask whether the
   comparison would survive that, and say what it actually proves.
4. **Never assume an unsuffixed filename is the default.** Corroborate from at least the vendor
   documentation, the changelog, and a byte comparison against the suffixed siblings before flashing
   anything. A wrong choice here changes audio topology silently.
5. **Fetch and verify; never vendor.** No licence exists, so redistribution is unlicensed. Files are
   downloaded at a pinned commit, hashed against the pin, and re-hashed in the instant before use.
   Nothing from this upstream is committed to this repository.
6. **Send no configuration-persisting command.** `SAVE_CONFIGURATION` writes to the array's flash,
   survives reboots, survives re-imaging the card, and is invisible to both the ALSA mixer and
   WirePlumber. Two of the three worst upstream reports follow one. The agent sends it nowhere, and
   any future need for array-side state has to arrive as a resource that observes and converges it.
7. **Read the primary source, not a summary of it — including this one.** Both issues most often
   cited here have been paraphrased into something stronger than they say. Open the issue.

---

<a id="corrections"></a>

## What has already been corrected once

Recorded so that a later reader does not restore an error that was already caught. Every item was
checked on 2026-08-24 against the live upstream.

- **Issue #18 does not describe a dead device.** An earlier workstream was handed a summary saying
  the unit was made unusable and unrecoverable. The report describes a unit that still enumerates
  and still answers control commands, with microphones, DOA, mute LED and speaker output broken in a
  way reflashing does not fix. The corrected reading is in [section 9](#9-what-the-open-issues-actually-say).
- **Issue #32's subject is a total loss of USB enumeration, not a firmware boot failure.** It is
  routinely cited for the one row of its table saying 2.0.10 and 2.1.0 do not boot on a V1.1 board.
  The reporter's own conclusion is that the enumeration failure is independent of firmware version,
  and a follow-up comment discloses that the board's reset button is physically broken and detached.
  Cite it with that context or not at all.
- **"No observable distinguishes the two 2.0.10 builds" is stronger than the evidence.** The notes on
  the three firmware entries in `upstream-review.json` say it flatly. What is established is that
  `VERSION` and `bcdDevice` cannot distinguish them, since both derive from the identical version
  number, and that only one 2.0.10 array has ever been read. `BLD_REPO_HASH` is a stable per-build
  fingerprint that plausibly *does* distinguish them; nobody has flashed a named build and read it
  back, so it is untested. Prefer the precise form in [section 2](#2-one-version-number-two-different-binaries).
- **Which build Frame #1 runs is inference from dates, not a measurement**, and
  [decision 90](../version2.md) already labels it as such. Do not let it harden into a fact by
  repetition.
- **`bcdDevice 020a` was measured on one array, not on both builds.** The two bcdDevice readings
  behind the version decode are real and were captured from two arrays on 2026-08-20 — one factory
  2.0.6 reading `0206` and one upgraded 2.0.10 reading `020a`. The claim that the June build also
  presents `020a` is a reasonable inference from the version number and has never been observed.

---

## Where this is enforced

| Concern | Where it lives |
| --- | --- |
| The four pinned upstream entries, their probes and their review notes | `upstream-review.json` |
| The probe kinds, including `github-path-commit`, and the release-gate semantics | `tools/FrameLink.Upstream/` |
| The three firmware pins, their digests and the flash interlocks | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` |
| The six control-tool files and their digests | `src/FrameLink.Agent/Resources/XvfHostRelease.cs` |
| Why the tool is fetched rather than vendored | [decision 63](../version2.md) |
| Why a firmware version is not a resource | [decision 90](../version2.md) |
| Why the fleet converges on a pinned image, and the sequencing that binds it | [decision 91](../version2.md) |
| The ledger's role in cutting a release | [version2.md §7.1](../version2.md) |
| Licensing posture for third-party material | [version2.md §7.3](../version2.md) |

---

<a id="seeed"></a>

## The open question with Seeed

A comment was posted to
[issue #32 on 2026-08-23](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32#issuecomment-5388447073)
from this project's GitHub account. It contributes the V1.1-on-2.0.10 counterexample, the two-build
table with both digests, the `BLD_*` readings and the observation that the DFU functional descriptor
advertises Upload Supported, so a board's actual bytes can be read back from Safe Mode without
writing anything.

It ends with two questions for Seeed:

1. **Which of the two v2.0.10 builds is current?**
2. **Should the June build be treated as withdrawn?**

**What an answer would change.** If the June build is withdrawn, anyone who cloned between 29 June
and 13 July is running firmware upstream no longer stands behind, and the changelog's single
`v2.0.10` heading is actively misleading — which would make it worth asking for the changelog to
name both publications. If both are considered current, then the version number genuinely does not
identify the firmware and every consumer of this repository needs a digest, which is what this
project already does and would then be able to point at. Either answer also bears directly on
whether the [issue #32](#9-what-the-open-issues-actually-say) reporter tried the June build — if
they did, the July build is worth trying before writing 2.0.10 off on V1.1, and that would remove
the single largest unquantified risk in [decision 91](../version2.md).

**Assume no answer.** Eleven of twenty open issues upstream have never received a comment. Nothing
in this project waits on a reply, and the point of recording the question is that if one arrives in
three months, somebody knows why it mattered.
