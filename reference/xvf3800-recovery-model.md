# FrameLink v2 — What the XVF3800 recovery kit actually is, and what a one-version policy would cost

**Does erasing break the board? No.** Writing `4mb_all_ff.bin` does not damage anything and does not
put the board into a state it cannot leave. What it does is **uninstall the firmware**. Afterwards the
Upgrade partition holds no valid image, so at the next power-up the boot loader falls back to the
**Factory** partition and the board comes up running Seeed's Safe Mode firmware — blinking red LED,
reachable over USB DFU, and *not* a microphone: no audio device, no `xvf_host`, nothing works until a
firmware image is written back. The board is not broken; it is empty, and the emptiness is deliberate
and one `dfu-util` download away from being filled. XMOS says the outcome in its own words:

> The factory image cannot be overwritten and in case of DFU failure, the factory settings will be
> restored after rebooting the device.

The rest of this file establishes five things the operator asked for, and three of the answers weaken
the case for the kit as it is currently pinned:

1. **`4mb_all_ff.bin` is 4,194,304 bytes in which every single byte is `0xFF`.** One commit in its
   life. It is **Seeed's** file, not XMOS's, and it exists for **one specific failure** — a
   configuration corrupted by `SAVE_CONFIGURATION`, upstream issue #8 — which the maintainer says was
   **fixed in firmware from v2.0.9 onward**, and which this repository cannot cause because it sends
   `SAVE_CONFIGURATION` nowhere.
2. **The erase is not required to recover an interrupted or bad write.** A good image can be written
   directly, and both XMOS and Seeed document exactly that with no erase step. XMOS additionally
   documents its *own* revert-to-factory blank image and it is **4,096 bytes of zeroes**, not 4 MiB of
   `0xFF` — one thousand times smaller and creatable on the frame with one `dd`.
3. **"2.0.6 is the fallback" is this repository's own choice**, made by the agent that implemented
   decision 91 on 2026-08-24. It is not upstream's recommendation, not Seeed's, and not in decision 91's
   text. Any known-good image would do, and the target image can serve as its own fallback.
4. **The one-version policy loses very little that is real, and one thing that is real is not about
   firmware at all** — it is the `ArrayHardwareGate` allow-list, which today refuses 2.0.7, 2.0.8 and
   2.0.9 boards outright.
5. **Dropping Safe Mode is a much larger decision than dropping the kit.** Safe Mode is the *only*
   route back from a board that will not enumerate in normal mode, it is the reason a bad write is not
   fatal, and it cannot be dropped in the sense of "removed" — it is in the board's Factory partition
   and it is there whether this project acknowledges it or not. What can be dropped is *support for
   it*: the panel screens, the runbook, the pre-flight refusal. What that costs is named in
   [section 5](#5-what-dropping-safe-mode-support-would-cost).

This is reference material, not a build guide: the seven-block step structure of
[CLAUDE.md §2.1](../CLAUDE.md) does not apply here. The link and honesty rules do.

---

## Provenance

**Every finding below was measured on 2026-08-24 in this session** unless it says otherwise, and the
method is named beside it.

**Nothing was flashed. No `dfu-util` was run in any mode. No array was put into DFU mode. No frame was
contacted, SSH'd to, or rebooted.** Every device-side reading quoted is carried forward from
[the upstream reference](upstream-respeaker-xvf3800.md),
[the board-revisions reference](xvf3800-board-revisions.md) and
[the upgrade-path reference](xvf3800-upgrade-path.md), and is labelled where it appears.

Methods used:

- `raw.githubusercontent.com` at explicit commit SHAs, plus `sha256sum` and a byte-by-byte scan in
  Python, for the binaries.
- The GitHub REST API through `gh` for commit histories, and for upstream issues **#8** and **#32**
  downloaded in full with every comment.
- The Seeed wiki page's markdown **source** in `Seeed-Studio/wiki-documents`, fetched through the
  contents API and decoded, rather than the rendered page.
- **XMOS's own source**, read directly: `xmos/lib_dfu` at `develop` — `dfu_types.h`, `dfu.xc`,
  `dfu_sub_sm.xc`, `dfu_default_conf.h`, `flash/quad/dfu_flash.c`, `host/xmosdfu/src/xmosdfu.cpp`.
- The **XVF3800 User Guide v3.2.1** "DFU operations" page, retrieved through the Internet Archive
  because `www.xmos.com` answers HTTP 403 to this workstation
  ([the upgrade-path reference §9.4](xvf3800-upgrade-path.md) records that block). The archived copy
  is gzip-encoded and had to be decompressed before it could be read; that is the reason an earlier
  attempt in this session returned nothing.
- `git log -S` over this repository's own history for the commit archaeology in
  [section 3](#3-where-206-is-the-fallback-came-from).

Raw captures are under `.work/recovery-model/` (gitignored). That directory is scratch, not the
evidence of record: everything a conclusion rests on is restated here with its method beside it.

---

## 1. What `4mb_all_ff.bin` actually is

### 1.1 The bytes, measured

**Downloaded 2026-08-24** from
`https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/0b73b3ffe908fb262a20fcff9f27f5a126f3c0a9/xmos_firmwares/recover/4mb_all_ff.bin`
— HTTP **200**, **4,194,304 bytes**, sha256
**`cd3517473707d59c3d915b52a3e16213cadce80d9ffb2b4371958fb7acb51a08`**. That matches the pin in
`src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` and in `tools/harness/flh/flash.py` exactly.

**The content was not sampled, it was enumerated.** The set of distinct byte values present in the
whole file is `{0xFF}` — one element. Every one of the 4,194,304 bytes is `0xFF`. It is not firmware,
it has no header, it has no DFU suffix, and it has no structure of any kind. It is 4 MiB of a single
repeated byte, which is why it compresses to nothing and why carrying it costs no meaningful storage.

**Provenance, measured by asking GitHub for every commit that has ever touched that path:** exactly
**one**.

| commit | committed | message |
| --- | --- | --- |
| `0b73b3ffe908fb262a20fcff9f27f5a126f3c0a9` | 2025-11-18T04:34:05Z | `feat: add all_ff.bin` |

It has never been modified, never been moved and never been renamed. It is the only file in
`xmos_firmwares/recover/`.

### 1.2 Where it came from, and it is not XMOS

**Read in full 2026-08-24.**
[Upstream issue #8](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/8) was opened
2025-11-14 by a user whose board stopped enumerating over USB after they ran
`xvf_host save_configuration 1`. Four days later, on **2025-11-18**, Seeed's `jerryyip` posted the
recovery procedure — and committed the file **the same day**, in the only commit it has ever had. The
comment, verbatim in the parts that matter:

> Thanks for reporting this issue. I think the configuration data is corrupted on your device, so
> please try the following steps to repair:
> 1. Enter Safe Mode
> 2. Flash the all_ff.bin … Note that this step will terminate at about 96% due to memory
>    `out of range`, but it is fine.
> 3. Flash the v2.0.6 firmware again, then reboot the device. It should be back to normal after that.

with a full `dfu-util` transcript inline whose command line is `dfu-util -e -a 1 -D 4mb_all_ff.bin`.

**Three things follow that are not obvious from the file's name.**

- **The file exists to clear a corrupted *configuration*, not to clean up a bad *firmware* write.**
  That is what the maintainer says it is for, in the sentence that introduces it.
- **It is written to alt setting 1, the Upgrade partition** — not to alt 2, the DataPartition, which is
  where a saved configuration lives. The maintainer's own command says so.
- **Seeed documents it nowhere else.** Measured by exhaustion 2026-08-24: the string `all_ff` appears
  **zero times** in the 42,730-byte Seeed wiki page source, **zero times** in upstream's
  `xmos_firmwares/dfu_guide.md` (6,276 bytes, read end to end), **zero times** in
  `xmos_firmwares/usb/changelog.md`, and **zero times** in the 302-byte repository README. **The entire
  documentation for this file is one GitHub issue comment.**

### 1.3 What writing it physically does

**The stated cause of the 96% stop is XMOS's, and it is now confirmed rather than inferred.** The
XVF3800 User Guide's "Error handling" section — the complete list, verbatim, retrieved 2026-08-24 —
has two entries:

> **errWRITE**: this error is returned if the host sends a download request for the wrong partition,
> for example the factory partition
>
> **errADDRESS**: this error is returned if the host sends a data block outside the address range of
> the memory partition, for example if the image is too large.

`dfuERROR status(8)` is `errADDRESS`. So the 96% stop is the device refusing a data block past the end
of alt 1's address range, exactly as `tools/harness/flh/flash.py` and
`src/FrameLink.Agent/Firmware/ArrayFlashApproval.cs` already say. **That explanation is now vendor-documented
rather than this project's reading of an error string.**

**The arithmetic is exact and worth recording, because it is the only measurement of this device's flash
layout anybody has.** Every number below divides cleanly by the 4,096-byte flash sector:

| Quantity | Bytes | 4 KiB sectors |
| --- | --- | --- |
| `4mb_all_ff.bin` | 4,194,304 | 1,024 |
| Accepted before `errADDRESS` | 4,030,464 | **984** |
| Refused | 163,840 (`0x28000`) | 40 |
| One firmware image | 933,888 | 228 |

96.09375% — which is the "about 96%" in both upstream reports. **So alt 1's writable window on this
device is exactly 984 sectors, 3.84 MiB, and a firmware image occupies 228 of them.** The erase
therefore blanks **4.3× more flash than a firmware image occupies**, all of it inside alt 1's own
address range. **Measured arithmetic on upstream's two published byte counts; the flash layout itself is
undocumented and this is the only window onto it.**

**Whether that window overlaps the DataPartition is not documented and cannot be read from outside.**
It is the only mechanism by which an alt-1 write could repair a corrupted configuration, and the only
evidence that it does is that the procedure demonstrably worked: `johens`, 2026-03-17, *"I met with
the exact same issue, and was able to fix it following the fix by jerryyip."* **Inference, stated as
such.**

**A detail worth correcting, because the file's name misleads.** `flash.py`'s comment calls the image
*"4,194,304 bytes of `0xFF`, which is the erased state of NOR flash, written as a download because DFU
has no erase command"*. The intent is right; the mechanism is not. **Programming NOR flash can only
clear bits, so writing `0xFF` erases nothing by itself.** What blanks the flash is the device's own
erase: `lib_dfu`'s `dfu_sub_sm.xc` calls `flash_erase_sector_async(FLASH_MAX_UPGRADE_SIZE)` on the
first `DFU_DNLOAD` and repeats it until the upgrade section is clear, and the flash library erases
further sectors as the write advances into them. **The decisive evidence that the byte value is
irrelevant is that XMOS's own blank image is `0x00`, not `0xFF`** — see
[section 2.2](#22-xmos-documents-its-own-blank-image-and-it-is-4-kib-of-zeroes). Both work; neither
works *because* of its byte value.

### 1.4 What state the board is in immediately after, and what has to happen next

**The Factory partition is never at risk, on three independent grounds.** XMOS: *"The factory image
cannot be overwritten."* The device's error surface has `errWRITE` specifically to refuse a download
aimed at the factory partition. And this project's own code refuses any alt setting but `1`, by
number, in both `flash.py`'s argument builder and the agent.

**So immediately after the erase:**

- The Upgrade partition holds no valid image.
- The device is left in `dfuERROR` and **will not accept another download in the same session** — see
  [section 1.5](#15-the-power-cycle-and-who-actually-documented-it).
- On the next power-up the boot loader finds no valid upgrade image and loads the factory image. XMOS:
  *"If the upgrade image is invalid, the factory image will be loaded."*
- The factory image on this product **is** Seeed's Safe Mode firmware — the wiki says it is *"stored in
  the Factory partition"* and that it supports both USB DFU and I2C DFU.

**Therefore the board comes up in Safe Mode with no button held, and stays there until a firmware image
is written.** It has no USB audio interface, answers no `xvf_host` command, and is a DFU target and
nothing else. **This last step is inferred** — from XMOS's boot-loader rule plus Seeed's statement about
what lives in the Factory partition. **Nobody has published an observation of a board left sitting in
that state**, because in both upstream reports the erase was immediately followed by a firmware write.

**What has to happen next is one download**: power-cycle, then
`dfu-util -a 1 -D <a good firmware image>`. That is the whole of it.

### 1.5 The power cycle, and who actually documented it

This repository records the power-cycle requirement in three places
(`ArrayFlashApproval.cs`, `flash.py`, [decision 91](../version2.md)) and each says it **is not in the
upstream instructions**. **That is half right and the half that is wrong matters.**

- **It is not in Seeed's instructions.** Confirmed: `jerryyip`'s step 3 is simply *"Flash the v2.0.6
  firmware again"*, with no power cycle. The wiki does not mention it. The DFU guide does not mention
  it.
- **It was reported by an upstream user**, `fallais` in
  [issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32), verbatim: *"a
  power cycle is required between the `all_ff` erase and the next flash. The erase leaves the device in
  `dfuERROR`, and a download attempted in the same session fails at 0% with the same out of range
  status. After a power cycle back into Safe Mode it writes normally."*
- **And it is documented by XMOS**, which nothing in this repository noticed. From the XVF3800 User
  Guide's "Error handling" section, verbatim:

  > If the following happens during a download or upload phase: the operations are interrupted by the
  > host; the device returns a DFU error — if the device is version 3.1.0 or higher, the user can
  > restart the operation from the beginning of the image. **For older versions, the device must be
  > rebooted before resuming the DFU procedure.** Resuming a download operation midway through an
  > image is not supported in any version.

  **Caveat, and it is real:** "version 3.1.0" is XMOS's numbering for the XVF3800 firmware line, and
  Seeed renumbers its builds as 2.0.x — the XMOS reference build in
  [issue #24](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/24) reported
  `VERSION 3.2.1` on this same board. **Which XMOS base version any Seeed 2.0.x build corresponds to is
  unknown**, so this passage cannot be read as "our firmware needs the reboot"; it can be read as
  "needing a reboot after a DFU error is documented device behaviour", which is what `fallais` observed.

**The correction to make:** this repository should say *not in Seeed's instructions* rather than *not in
the upstream instructions*, and should cite the XMOS passage beside `fallais`'s report. The practical
instruction is unchanged.

### 1.6 The erase's own success rate, stated plainly

**One prescription, one confirmed success, one confirmed failure.** `jerryyip` prescribed it;
`johens` confirmed it fixed an identical failure; `fallais` ran it **twice, cleanly** on a V1.1 board
and it did not fix that board — and on that unit alt 2 rejects both upload and download at offset 0, so
the DataPartition could not be cleared by any means. `Swissola` said they were *"about to run the
recovery"* and never reported back.

**That is the entire evidence base for this file's efficacy: three data points, one of them negative.**

---

## 2. Is the erase step actually required?

**No — not for any failure this project can cause.** Four independent lines of evidence, all measured
2026-08-24.

### 2.1 A DFU download erases the upgrade partition before it writes, so a good image can go straight on

From [`xmos/lib_dfu`'s `lib_dfu.rst`](https://github.com/xmos/lib_dfu/blob/develop/doc/rst/lib_dfu.rst),
verbatim:

> During the DFU download process, on receiving the first `DFU_DNLOAD` command, the device starts to
> erase `FLASH_MAX_UPGRADE_SIZE` bytes of the upgrade section of the flash […] This is done by
> repeatedly calling the flash erase function until the entire upgrade section is erased.

and from the XVF3800 User Guide:

> Only one upgrade image may be transferred to the flash of the XVF3800. The first upgrade image will
> be replaced by a subsequent upgrade process.

**There is nothing for a separate erase to do.** Whatever a previous write left behind is destroyed by
the operation that would have depended on it.

**And XMOS says outright what to do after a failed download**, verbatim:

> Should the download operation fail, both the `xvf_dfu` and `dfu-util` applications will exit with an
> error code. **Another download operation may be reattempted**; should this continue to fail,
> rebooting the device will reset the device into the factory image, as any pre-existing upgrade image
> has been corrupted by the failed DFU operation.

**This directly contradicts a claim this repository makes twice.** `ArrayFlashApproval.cs` and
[decision 91](../version2.md)'s crashed-flash latch both say *"retrying a partial write is the
documented route from a recoverable board to an unrecoverable one."* **No such documentation was found
anywhere in this session**, and the vendor's actual sentence is the opposite: another download may be
reattempted. The claim appears to be a generalisation of a *different*, correct instruction — that
retrying the **`all_ff` erase** after its expected `errADDRESS` is the mistake. Retrying a *firmware*
download is what XMOS tells you to do. See [section 6](#6-corrections-this-file-forces).

### 2.2 XMOS documents its own blank image, and it is 4 KiB of zeroes

This is the finding that most changes the picture, and nothing in this repository had it. The XVF3800
User Guide has a section titled **"Revert the device to factory image"**, verbatim and complete:

> To restore the device to its factory configuration, effectively discarding any upgrades made, the same
> process as outlined above is followed but using a blank upgrade image. This is the only way a restore
> can be initiated, as the device does not have the ability to restore itself.
>
> The blank file can be generated using `dd` on MAC and Linux, and `fsutil` on Windows. **A blank image
> can be created with a file of zeroes the size of one flash sector.** In the normal case of 4KB sectors
> on a UNIX-compatible platform, this can be created as follows:
>
> ```
> dd bs=4096 count=1 </dev/zero 2>/dev/null blank.bin
> ```
>
> and for Windows systems:
>
> ```
> fsutil file createNew blank.bin 4096
> ```

| | XMOS's blank image | Seeed's `4mb_all_ff.bin` |
| --- | --- | --- |
| Size | 4,096 bytes | 4,194,304 bytes — **1,024×** larger |
| Byte value | `0x00` | `0xFF` |
| Sectors touched | 1 | 984 |
| Outcome | completes normally | stops at 96% with `errADDRESS` |
| Purpose | invalidate the upgrade image so the boot loader falls back to factory | scrub as much of alt 1's window as the device will accept |
| Documented where | XMOS User Guide, a numbered section | one GitHub issue comment |
| Obtainable how | one `dd` on the frame, no network | download 4 MiB from GitHub |

**They are not the same operation and they are not for the same problem.** XMOS's blank image reverts to
factory. Seeed's oversized image is a shotgun aimed at whatever holds a corrupted configuration, and its
error at 96% is the point rather than a defect.

**For "undo a bad firmware write", XMOS's 4 KiB of zeroes is sufficient, is vendor-documented, and needs
no file carried on the card at all.** `dd bs=4096 count=1 </dev/zero >blank.bin` runs on the frame.
**Untested on this hardware by anyone**, here or upstream — nobody has published a capture of it on an
XVF3800.

### 2.3 Seeed's own recovery instruction has no erase step

**Read at source 2026-08-24.** The Seeed wiki's Safe Mode section lists three reasons to use Safe Mode:

> - Your firmware isn't working properly (e.g. USB not detected, LED not lighting up as expected).
> - You need to re-flash a new firmware but the current one won't respond.
> - **You accidentally flashed something wrong and want to recover.**

and the procedure it points at is *enter Safe Mode, then flash the firmware*. **There is no erase step
in Seeed's documented recovery**, and `all_ff` is not mentioned on the page at all. The wiki's own
worked example of recovery — the FAQ entry for a board shipped with I2S firmware that must be turned
into a USB board — is two steps: enter Safe Mode, flash the USB firmware.

### 2.4 There is a third route, unreachable here, recorded so nobody re-derives it

`XMOS_DFU_REVERTFACTORY` exists. From
[`lib_dfu/api/dfu_types.h`](https://github.com/xmos/lib_dfu/blob/develop/lib_dfu/api/dfu_types.h),
verbatim:

```c
/** Additional command that erases only the first flash sector of the upgrade image, to revert to the factory image */
XMOS_DFU_REVERTFACTORY = 0xF1, // For lib_device_control access this will be 0x71 due to read bit
```

`dfu.xc`'s `action_revert_factory()` erases exactly one sector, and `lib_dfu.rst` says *"To revert back
to the factory image, send the command `XMOS_DFU_REVERTFACTORY` to erase the upgrade image."* XMOS's own
host tool exposes it: `host/xmosdfu/src/xmosdfu.cpp` has a `--revertfactory` option that issues a
vendor control transfer with `bRequest` `0xF1`.

**It is not reachable with the tooling this project has**, for two measured reasons:

- **`dfu-util` cannot send it.** It is a USB *vendor* request, not a DFU class request.
- **It is not in Seeed's control command table.** `host_control/rpi_64bit/dfu_cmds.yaml` — 2,507 bytes,
  sha256 `67f6a982567b8d23da85c5806c40344094d21071c631344a47164cd085dddba3`, byte-identical to XMOS's
  own copy — contains exactly eleven commands: `DFU_DETACH`, `DFU_DNLOAD`, `DFU_UPLOAD`,
  `DFU_GETSTATUS`, `DFU_CLRSTATUS`, `DFU_GETSTATE`, `DFU_ABORT`, `DFU_SETALTERNATE`,
  `DFU_TRANSFERBLOCK`, `DFU_GETVERSION`, `DFU_REBOOT`. **No `REVERTFACTORY`.**

**And whether the XVF3800's shipped firmware implements it at all is unknown**: `lib_dfu` at `develop`
is 2026 code, the XVF3800 User Guide is dated 2024-10-29 and predates it, and `sw_xvf3800` is not
public. Recorded as a possibility, not as an option.

### 2.5 The failure the erase exists for is fixed in firmware, and this project cannot cause it anyway

**Two measured facts, and together they are the strongest argument in this file.**

- **`jerryyip`, 2026-08-14, in issue #8**, answering whether v2.0.9 fixes the underlying corruption:
  *"This issue has been fixed in version 2.0.9 and later versions."* **The pinned target is v2.1.0.**
- **This repository sends `SAVE_CONFIGURATION` nowhere at all**, as a standing rule from
  [decision 91](../version2.md), because two of the three worst upstream reports follow one.

So the failure `4mb_all_ff.bin` was created to repair is a bug that upstream says it fixed two versions
before the one this fleet converges on, triggered by a command this fleet never sends. **Carrying the
file is carrying a cure for a disease this product is doubly immune to.**

---

## 3. Where "2.0.6 is the fallback" came from

**It is this project's own choice, made by the agent that implemented decision 91, on 2026-08-24.**
Traced by `git log -S` over this repository's whole history.

- `XvfFirmwareRole.Fallback` appears in exactly **one** commit that introduced it:
  **`9908080219d920bec82fd597928434eff4dc9581`**, 2026-08-24 00:09:53 +0200, *"firmware: the fleet
  converges on a pinned image, and one deliberate act writes it"*. That commit's message runs to
  thirty lines and **does not mention 2.0.6, a fallback, or a recovery pair at all**. The three-image
  structure arrives in the code with no justification in the commit message.
- **[Decision 91](../version2.md) does not choose it either.** It names the fallback exactly once, in
  the interlock list: *"the frame refuses to write unless `4mb_all_ff.bin` **and** the v2.0.6 fallback
  are both present and hash to their pins"*. It gives no reason for 2.0.6 over any other version.
- **The only stated reasoning is a doc comment**, written in that same commit, in
  `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` and echoed in `tools/harness/flh/flash.py`:

  > v2.0.6 is the version both of this project's arrays shipped with, and the version upstream issue
  > #32 reports booting on every board revision anyone has tried.

**The first half is true. The second half overstates its source in two ways**, and both are visible in
issue #32's own text, read in full today. That issue tests **one** board, of **one** revision (V1.1) —
"every board revision anyone has tried" is one revision on one unit. And on that unit v2.0.6 **boots but
does not enumerate over USB**, which is the issue's entire subject; the table's "Boots / USB enumerates"
columns read `yes / no`. So v2.0.6 is not evidenced as *working* anywhere in issue #32 — it is evidenced
as *lighting up*.

**Upstream has no fallback recommendation.** `jerryyip`'s issue #8 procedure says *"Flash the v2.0.6
firmware again"* because **v2.0.6 was the version that reporter was running** and was the newest
published image at the time (committed 2025-11-12, six days before the recovery comment). It is not a
designated recovery version. Nine months later the same maintainer answered a 6-channel user in the same
thread with *"now we have the `v2.1.0_16k6ch` firmware, so please just use this one"* — **the
maintainer's own recovery advice tracks the newest image, not an old one.**

### 3.1 Would any known-good image do? Yes. Could the target be its own fallback? Yes.

- **The recovery procedure is version-agnostic by construction.** Every published version can be written
  directly to any board from any starting state — established at length in
  [the upgrade-path reference](xvf3800-upgrade-path.md), on XMOS's documented mechanism plus five
  observed multi-version jumps.
- **The only thing that must not be got wrong is the *profile*, not the version.** Writing
  `_48k2ch` or `_16k6ch` changes the frame's audio topology silently; writing an I2S image removes USB
  audio entirely. Both hazards are identical for a fallback and for a target.
- **The one property a fallback needs that the target may lack is being known to boot on the board in
  front of you.** That is the entire real argument for an *older* fallback, and it rests on issue #32's
  single damaged unit — whose own reporter concludes *"the failure is independent of firmware version"*,
  whose reset button is physically detached, and which is contradicted by Frame #1, a V1.1 board running
  2.0.10 successfully.
- **So a distinct fallback buys exactly one thing: insurance against the pinned target being bad on a
  board this project has not yet met.** That is not nothing — [section 4](#4-the-one-version-policy-what-is-actually-lost)
  prices it — but it is a much narrower claim than "you need a fallback".

**Pin verified 2026-08-24**: the v2.0.6 image at commit `ff421c45e1624f7b27da5e7f723a58cc69b3eb34`
(2025-11-12) is 933,888 bytes, sha256 `c95fd3dec7597c72a24bc7e5212e6db136144956d5569f24b518ecfc1540ef09`
— matching the pin. That commit's message: *"feat: update usb firmware to v2.0.6, which (1) fixes ws2812
led showing wrong colors issue; (2) set dac output volume from 0dB to 6dB; (3) add DOA_VALUE command"*.
**Note the second item** — 2.0.6 is the version that *introduced* the 6 dB DAC default that
[decision 91](../version2.md) discusses; falling back to it from 2.1.0 is a −2 dB change in the array's
own output default, silently.

---

## 4. The one-version policy: what is actually lost

The operator's proposal: embed one tested target, everybody converges on it, a new version becomes the
target only after it is tested. **Below is every scenario the kit is claimed to cover, priced honestly,
with "has this ever happened" answered separately from "could it".**

### 4.1 A write interrupted by mains loss

| | With the full kit | With only the target embedded |
| --- | --- | --- |
| What the board does | Boot loader deems the partial image invalid, loads the Factory image, comes up in Safe Mode | **Identical** — the board does not know what is on the card |
| Recovery | Safe Mode, erase with `all_ff`, power-cycle, write the fallback | Safe Mode, write the target |
| Steps needing hands | 1 (the Mute-hold) | 1 (the Mute-hold) |
| Vendor's own instruction | — | *"Another download operation may be reattempted"* |

**The kit adds nothing here.** The erase has nothing to erase that the next download would not erase
itself, and the fallback is not needed because the target is a valid image. **Never observed in this
project or upstream** — no report anywhere of a mains loss during an XVF3800 DFU write. Theoretical.

**One caveat that survives**: if the interrupted write leaves the frame's *card* holding a truncated
image, the agent re-hashes before the write and refuses, which is the interlock that matters — and that
interlock is unaffected by the recovery kit.

### 4.2 A board that boots but misbehaves after a flash

| | With the full kit | With only the target embedded |
| --- | --- | --- |
| Typical shape | Silent microphones, silent channels, AEC never converges, wrong LED colours | Same |
| Recovery | Write the fallback (2.0.6) and accept 2.0.6's behaviour | Write the target again; if the target is what caused it, this does not help |
| Evidenced? | **Yes**, four times upstream — issues #22, #24, #31, #12 | Same evidence |

**This is the one scenario where a *different* image genuinely helps**, and it helps only if the
misbehaviour is caused by the target image rather than by the board. Every evidenced case upstream was
caused by flashing the **wrong image** — a 48 kHz build, a 6-channel build, an I2S build, an XMOS
reference build — not by the correct image behaving badly. **This project's gate already makes the
wrong-image case unreachable**: only the pinned target is writable, by name, digest and role, in both
the agent and the harness.

**So the residual risk is "the pinned target misbehaves on a board we have not met."** Real, unquantified,
and the argument for keeping *some* second image. **It does not require v2.0.6 specifically**, and it does
not require the erase image at all.

### 4.3 A target image that turns out to be bad after it is pinned and deployed

| | With the full kit | With only the target embedded |
| --- | --- | --- |
| What has to happen | Somebody notices, then visits each frame with hands, holds Mute, writes the fallback | Somebody notices, changes the pin, ships an agent build, then visits each frame with hands and writes the new target |
| Difference | The fallback is already on the card | A pin bump and a release are needed first |
| Time cost of the difference | Minutes at the frame | Hours to a day of release work, **before** anyone can travel |

**This is the strongest concrete argument for the fallback, and it is an argument about *latency*, not
about capability.** A second known-good image already on every card means the fix does not wait on a
build. Whether that matters depends on how bad "bad" is: if the array is silent, the frame still shows a
screen and answers the Fleet Manager, and a day's wait is survivable.

**Has it ever happened?** No. Nobody has ever flashed v2.1.0 to any board, here or in any published
upstream report. **The pinned target has never run on hardware anywhere.** That cuts both ways: it is the
reason to want a fallback, and it is the reason the whole question is currently theoretical.

### 4.4 A board that arrives from the factory on a version our software refuses

**This one has already happened, in a sense, and the recovery kit is irrelevant to it.**

`src/FrameLink.Agent/Firmware/ArrayHardwareGate.cs` carries an allow-list whose firmware set is, verbatim:

```csharp
Firmware = ["2 0 6", "2 0 10", "2 1 0"],
```

with a doc comment saying so deliberately: *"Upstream publishes other versions and they are deliberately
absent: a version nobody here has seen is exactly the case this gate exists to refuse."*

**So a board arriving on 2.0.7, 2.0.8, 2.0.9, or any future 2.1.1, is refused by the gate today.** Not
flashed, not converged, escalated to a person. Seeed has shipped nine USB versions and withdrawn four of
them silently; a board bought next month could plausibly ship on any of them.

| | With the full kit | With only the target embedded |
| --- | --- | --- |
| What happens | `NotOnTheAllowlist`, no write | **Identical** |
| Fix | Edit the allow-list, rebuild, redeploy | Identical |
| Does the recovery kit help? | **No.** The refusal happens before any image is chosen | No |

**This is a live gap and it is worth surfacing on its own** — it is the most likely thing on this list to
actually bite, it is unrelated to the recovery kit, and it would bite equally hard under either policy.

### 4.5 The costs the kit imposes today

Priced because they are as real as the risks it covers:

- **A frame that has never had network cannot flash at all.** The target is vendored and embedded; the
  fallback and the erase image are **fetched**. `ArrayFirmwareFlash`'s pre-flight returns
  `RecoveryNotVerified` unless both are present and hash to their pins. **So the recovery kit is, today,
  the only reason an offline frame cannot flash** — which is the precise opposite of the reason the
  target was vendored in the first place.
- **Two more upstream pins to review.** Three ledger entries instead of one, each of which can go
  `MOVED` or `UNREACHABLE` and block a release under [version2.md §7.1](../version2.md). `TODO.md`
  already records that **nobody has decided what a moved `-fallback` or `-recovery` entry means.**
- **Two more unlicensed redistributions to reason about, if they are ever vendored.** The upstream has
  no licence file in any of its 35 commits; the target's vendoring was an explicit operator decision and
  the other two are an open question the operator has not answered (`SESSION-STATE.md` item D).
- **A procedure with a documented failure mode that reads like success.** The erase stops at 96% with an
  error, and the one thing a person must not do is retry it. That is a trap laid for whoever is holding
  the board at the worst moment.

### 4.6 Summary

| Scenario | Ever happened? | Kit helps? | What actually helps |
| --- | --- | --- | --- |
| Mains loss mid-write | **No**, nowhere | **No** | Safe Mode + rewrite the target |
| Board misbehaves after a correct flash | **No** for the correct image; yes four times for wrong images | **Only the fallback**, and only if the target is at fault | A second known-good image, any version |
| Pinned target turns out bad in the field | **No** — 2.1.0 has never run anywhere | **Only the fallback**, and only as latency insurance | A second known-good image, or a fast release |
| Board ships on an unknown version | Not yet, but four versions are unlisted | **No** | Widening `ArrayHardwareGate.Allowlist` |
| Corrupted saved configuration | **Yes**, three upstream units | **Yes — this is the erase image's only job** | Not reachable here: `SAVE_CONFIGURATION` is sent nowhere, and upstream says it is fixed from v2.0.9 |

---

## 5. What dropping Safe Mode support would cost

**First, a distinction the phrase hides.** Safe Mode cannot be *dropped*. It is firmware in the board's
Factory partition, put there at manufacture, which no DFU write can touch — XMOS: *"The factory image
cannot be overwritten."* It is entered by a physical gesture the boot loader samples at power-on, and
the board will do it whether or not any FrameLink code mentions it. **What is on the table is dropping
*support* for it**: the panel screens, the runbook in `ArrayFlashRecovery`, the pre-flight refusal, the
rehearsal that [decision 91](../version2.md) makes binding.

### 5.1 What the Factory partition actually guarantees

**Evidenced, from three directions:**

1. **A firmware write only touches alt 1.** `mazhewitt`, issue #19, 2026-06-04: *"The flash only writes
   alt 1, so the Factory image stays intact and keeps DFU reachable even if an upgrade goes wrong — a
   nice safety net if you only have one unit."*
2. **XMOS guarantees the fallback in the boot loader.** *"If the upgrade image is invalid, the factory
   image will be loaded"*, and *"in case of DFU failure, the factory settings will be restored after
   rebooting the device."* The selection predicate is validity, never version.
3. **It works on the worst unit anybody has reported.** Issue #32's board will not enumerate on any
   firmware in normal mode, and still *"enumerates as DFU first try, every time"* in Safe Mode.

**What it does not guarantee.** It does not survive a corrupted DataPartition — issues #8 and #32 both
describe boards that reach Safe Mode fine and still will not run in normal mode. And it does not
guarantee the *gesture* works: Safe Mode needs the Mute button and a power cycle, and issue #32's board
has a detached RST button, which is a reminder that buttons are physical.

### 5.2 Can a board be recovered without Safe Mode?

**Sometimes, and the distinction is sharp.**

| Board state | Route back without Safe Mode | Needs Safe Mode? |
| --- | --- | --- |
| Runs the target firmware, enumerates normally | `dfu-util -e` triggers a run-time detach into DFU mode from normal operation — this is what the agent's own flash does, and what upstream's DFU guide documents | **No** |
| Runs an older firmware, enumerates normally | Same | **No** |
| Boots, but **does not enumerate over USB** (issues #8, #32) | **None.** There is no USB device to detach | **Yes** |
| Upgrade partition blank or invalid — the state after a failed or interrupted write | The board comes up on the Factory image, which *is* Safe Mode; DFU is reachable | **Yes, in effect** |
| I2S firmware written to a USB-hosted board | USB DFU is gone; only I2C DFU remains, and there is no I2C host on a FrameLink frame | **Yes** |

**So: yes, Safe Mode is the only route back from an interrupted write, and from every state in which the
board stops presenting a USB device.** The run-time detach path — the one the agent uses — requires a
working, enumerating board, which is exactly the condition that fails in every scenario a recovery
route exists for.

**Stated plainly, because it makes this a larger decision than it sounds: every recovery in this whole
document begins with the Mute-hold.** The erase image is optional. The fallback image is optional. Safe
Mode is not.

### 5.3 What dropping *support* would actually cost

- **The rehearsal.** [Decision 91](../version2.md) makes it binding that nothing is flashed until Safe
  Mode has been rehearsed on one of this project's own arrays. **Nobody here has ever entered Safe
  Mode** — decision 91 says so, `SESSION-STATE.md` line 137 lists the rehearsal as gating every
  firmware decision, and `TODO.md` repeats it. Dropping the rehearsal means the first time anybody in
  this project enters Safe Mode will be on a frame that is already broken, in a household, under time
  pressure. **That is the single largest cost on this list**, and it is free to avoid: the rehearsal
  writes nothing and is a complete outcome on its own.
- **The panel screens and the runbook.** `ArrayFlashRecovery.SafeModeSteps` and `OperatorSteps` are the
  only place in this project that tells a non-technical person at the frame what to do. Removing them
  does not remove the need; it moves it to whoever is on the phone.
- **The pre-flight refusal.** `RecoveryNotVerified` is the interlock that today blocks an offline frame
  from flashing. Removing the recovery pair removes that block, which is a *benefit* of dropping the
  kit rather than a cost.
- **The `dfu-util -a 1 -U` readback.** Upload is advertised by the board's own DFU functional
  descriptor, needs Safe Mode, and is the only way to answer *which bytes are actually on this board* —
  which is the open question with Seeed. Cheap, read-only, and it disappears if Safe Mode is not
  supported.

**What dropping it does not cost:** anything about the normal flash. The agent's write goes through a
run-time detach on a working board and never touches Safe Mode.

---

## 6. Corrections this file forces

Recorded so a later reader does not restore them. Each was checked against a primary source today.

- **"Retrying a partial write is the documented route from a recoverable board to an unrecoverable
  one"** — in `src/FrameLink.Agent/Firmware/ArrayFlashApproval.cs` and in
  [decision 91](../version2.md)'s crashed-flash latch. **No such documentation exists.** XMOS says
  *"Another download operation may be reattempted."* The true and narrower statement is that retrying
  the **`all_ff` erase** after its expected `errADDRESS` is the mistake, which is what the same file
  says correctly two sentences earlier. The crashed-flash latch is still worth having — a marker that
  outlives a killed write is a fact worth a person's attention — but its justification should be *we do
  not know how far it got*, not a vendor warning that was never issued.
- **"A power cycle is required … that is not in the upstream instructions"** — three places. Precise
  form: **not in Seeed's instructions; documented by XMOS**, whose user guide says the device must be
  rebooted before resuming a DFU procedure after an error, and independently reported by `fallais` in
  issue #32.
- **"v2.0.6 … the version upstream issue #32 reports booting on every board revision anyone has
  tried"** — in `XvfFirmwareRelease.cs` and `flash.py`. Issue #32 tests **one unit** of **one
  revision**, and reports that v2.0.6 boots but **does not enumerate over USB** on it. The image is
  evidenced as lighting up, not as working, and "every board revision" is one.
- **"4,194,304 bytes of `0xFF`, which is the erased state of NOR flash"** — `flash.py`. True about NOR
  flash, misleading as an explanation: writing `0xFF` programs nothing. The erase is the device's, not
  the payload's, which is why XMOS's equivalent blank image is `0x00`.
- **"The image is 4 MiB and the partition is smaller, so the write runs off the end"** — `flash.py`.
  **This one is right and can now be upgraded from a reading to a citation**: `dfuERROR status(8)` is
  `errADDRESS`, which XMOS defines as *"a data block outside the address range of the memory partition,
  for example if the image is too large."*

---

## 7. What nobody knows

- **What is at the far end of alt 1's 984-sector window.** Whether the DataPartition falls inside it —
  the only mechanism by which the erase could ever have repaired a corrupted configuration — is
  undocumented and unreadable from the host. The 4,030,464-byte figure is the only measurement of this
  device's flash layout in existence.
- **Whether the XVF3800's firmware implements `XMOS_DFU_REVERTFACTORY`.** It is in `lib_dfu` at
  `develop` in 2026; the XVF3800 User Guide is from 2024 and does not mention it; `sw_xvf3800` is not
  public. It is absent from Seeed's control command table either way.
- **Whether XMOS's 4 KiB blank image works on this board.** Documented by XMOS for the XVF3800
  specifically; never published by anyone as an XVF3800 capture.
- **What a board left blank actually does.** Nobody has published an observation of an XVF3800 sitting
  with an empty Upgrade partition, because in both upstream reports a firmware write followed
  immediately.
- **Whether v2.1.0 boots on anything.** It has never run on any board in this project or in any
  published upstream report. Issue #32's single damaged V1.1 unit is the only unit it has been written
  to, and there it did not boot.
- **What Safe Mode looks like on this project's own hardware.** Never entered. No `dfu-util -l` capture
  exists here, on any array, at any version.

---

## 8. The open questions, and directions for each

Three questions the operator raised are genuinely open. Each gets its own primer, its own directions and
its own recommendation, because a single recommendation at the end would answer one of them.

### 8.1 Should `4mb_all_ff.bin` stay in the kit?

**Primer.** It is a 4 MiB blob of one repeated byte, documented in a single GitHub issue comment, that
exists to clear a corrupted saved configuration. Upstream says that corruption is fixed from v2.0.9; the
target is v2.1.0; and this repository sends the command that causes it nowhere. It is not needed to
recover an interrupted write, and XMOS's own equivalent is 4 KiB creatable with one `dd`. On the one
board where it was most needed it did not work. Against that, it costs almost nothing to carry and it is
the documented answer to the one failure that a firmware reflash provably does *not* fix.

1. **Drop it entirely.** Remove the pin, the ledger entry and the pre-flight requirement. Recovery
   becomes *Safe Mode, write an image*. Loses the ability to clear a corrupted DataPartition — a failure
   this project cannot cause and upstream says is fixed.
2. **Replace it with XMOS's 4 KiB blank, generated on the frame.** No file carried, no pin, no ledger
   entry, no licence question. Vendor-documented for this exact chip. **Untested on this hardware by
   anybody**, and it reverts to factory rather than scrubbing configuration.
3. **Keep it, but demote it out of the pre-flight.** Stop refusing a flash when it is absent; keep it as
   a fetched artifact and a runbook step. Removes the offline-frame block, keeps the capability.
4. **Keep it exactly as is.** Costs the offline-frame block, two ledger entries and an undecided review
   policy; buys the one failure mode nothing else addresses.
5. **Keep it and vendor it.** Answers `SESSION-STATE.md`'s open question D in the affirmative — it
   compresses to nothing — at the price of a second unlicensed redistribution.

**Recommendation: (3), moving to (1) after the Safe Mode rehearsal.** The pre-flight refusal is doing
active harm today — it is the only thing stopping an offline frame from flashing, which defeats the
reason the target was vendored — and the file's own job is unreachable in this product. Demoting it is
reversible and costs nothing; deleting it is better done once somebody has actually stood in front of a
board in Safe Mode and knows what the recovery feels like.

### 8.2 Should there be a fallback image at all, and should it be v2.0.6?

**Primer.** The fallback is a second known-good firmware carried on every card so a person can put the
array back to a state that worked. v2.0.6 was chosen by the implementing agent, not by upstream, not by
Seeed and not by decision 91, and its stated justification overstates issue #32. Its only real job is
insurance against the pinned target being bad on a board nobody has met — a risk that is real because
v2.1.0 has never run anywhere, and unquantified for the same reason.

1. **One version only, as proposed.** The target is its own fallback. Simplest thing that works for
   every evidenced failure. Loses only the case where the target itself is at fault, where the fix then
   waits on a pin bump and a release.
2. **Keep a fallback but make it the previous *tested* target.** When 2.1.0 is proven, the fallback
   becomes 2.0.10 — the version Frame #1 actually runs and this project has actually flashed
   successfully. Better evidence than 2.0.6 by a wide margin, and it moves forward instead of ageing.
3. **Keep v2.0.6 as it is.** The version both boards shipped with, so it is what a factory board is
   known to run — but nobody here has flashed it, only received it, and it silently reverts the DAC
   default by 2 dB.
4. **One version embedded, plus a fallback fetched on demand.** The card carries one image; a second is
   pulled only when somebody is standing at a broken frame. Halves the footprint and the pins; fails on
   a frame with no network, which is the case that motivated vendoring.
5. **One version, and buy the insurance differently** — flash the Frame #2 spare first and keep it as a
   known-good physical spare array. Recovery becomes *swap the board*, which needs hands anyway.

**Recommendation: (2), with (1) acceptable and (5) as the real safety net.** A fallback that is the
previous tested target is strictly better evidenced than v2.0.6 and requires no new machinery — it is
the same pin, bumped one step behind. But the honest reading of this evidence is that (1) is defensible
today, because the only scenario a fallback covers has never happened to anyone, and the physical spare
in (5) covers it better than any file does.

### 8.3 Should Safe Mode support be dropped?

**Primer.** Safe Mode is firmware in the Factory partition; it cannot be removed and it is the only
route back from a board that stops enumerating, including every interrupted-write case. What is on the
table is this project's *support* for it — the rehearsal, the panel screens, the runbook, the pre-flight.

1. **Keep everything, do the rehearsal first.** Decision 91's binding sequencing, unchanged. Costs one
   bench session that writes nothing.
2. **Keep the rehearsal and the runbook; drop the pre-flight refusal.** The knowledge stays, the block
   goes. This is the same move as (3) in [8.1](#81-should-4mb_all_ffbin-stay-in-the-kit).
3. **Drop the panel screens, keep an operator-facing runbook.** The person at the frame gets told to
   call somebody; the somebody has the steps. Reduces UI surface, keeps capability.
4. **Drop support entirely and treat a bad array as a hardware replacement.** Honest and simple if the
   fleet economics allow it: a frame with a dead array is an RMA, not a repair. **This is the only
   option on this list that genuinely removes work**, and it requires accepting that some boards get
   thrown away for a fixable fault.
5. **Drop support and stop flashing altogether**, reverting to decision 90. The array runs whatever it
   shipped with; the fleet reports the version and never converges it.

**Recommendation: (2) now, and (4) is a legitimate product decision the evidence does not argue
against.** The rehearsal is the part that must not be dropped, because it is cheap, it writes nothing,
and it is the difference between a documented gesture and one nobody in this project has ever performed
— and every single recovery route in this document begins with it.

---

## Where this lands in the repository

| Concern | Where it lives |
| --- | --- |
| The upstream's structure, licence, probes and issue history | [upstream-respeaker-xvf3800.md](upstream-respeaker-xvf3800.md) |
| Board revisions, firmware profiles, and what a wrong image costs | [xvf3800-board-revisions.md](xvf3800-board-revisions.md) |
| Why any version can be written directly to any other | [xvf3800-upgrade-path.md](xvf3800-upgrade-path.md) |
| The three pinned images, their digests and the flash interlocks | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` |
| The `RecoveryNotVerified` pre-flight this file argues about | `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` |
| The Safe Mode runbook and the two procedural details | `src/FrameLink.Agent/Firmware/ArrayFlashApproval.cs` |
| The allow-list that refuses 2.0.7, 2.0.8 and 2.0.9 boards | `src/FrameLink.Agent/Firmware/ArrayHardwareGate.cs` |
| The bench-side flash, its image table and its runbook | `tools/harness/flh/flash.py` |
| The three ledger entries and the undecided review policy | `upstream-review.json`, `TODO.md` |
| Why the fleet converges on a pinned image, and the binding sequencing | [decision 91](../version2.md) |
