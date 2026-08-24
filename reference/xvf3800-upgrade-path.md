# FrameLink v2 — Can any published XVF3800 firmware DFU straight to v2.1.0?

**Yes.** Every version upstream has ever published can be written directly to
`respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin` in one `dfu-util` download. No intermediate flash is
required from any starting version, and nothing that could require one exists in this device's DFU
design. The operator does not need to revisit the "flash if the hardware is exactly as expected and
the firmware is older than the target" rule *on upgrade-path grounds*.

That is a stronger answer than "undocumented", and it is worth being precise about why. Upstream
Seeed says nothing at all about upgrade paths — that half genuinely is undocumented. But **XMOS
does**, in its own DFU documentation for this chip and in the source of the DFU library the chip
runs, and what XMOS documents is a mechanism in which an upgrade path cannot arise: the download
erases and rewrites the entire upgrade partition, the boot loader chooses between the factory and
upgrade images on **validity** and never on version, and the device's own documented error surface
for a download has exactly two entries, neither of them about versions. The conclusion rests on that
mechanism plus five direct observations of multi-version jumps working, not on an absence of
warnings.

**Three things this investigation found that *do* deserve the operator's attention, none of them an
upgrade path.** They are in [section 9](#9-real-risks-that-are-not-upgrade-path-risks).

1. **A version-only comparison cannot see the variant builds.** `..._v2.1.0_48k2ch.bin` and
   `..._v2.1.0_16k6ch.bin` report the *same* `VERSION 2 1 0` as the target. A board running the
   48 kHz build — which upstream issue #31 measured as unable to converge its AEC at all — would be
   judged "not older than the target" and never flashed. Same-version-different-image is evidenced
   at 2.0.8 by device readings in issues #22 and #24.
2. **A filename version and the device's reported `VERSION` have already disagreed at this
   publisher.** Upstream issue #29, 2026-08-11: a device flashed with `..._i2s_dfu_..._v1.0.7.bin`
   reports `VERSION 1.0.5`. On the I2S line, not the USB line — but the same maintainer, twelve days
   before v2.1.0 shipped.
3. **The one data point the operator cited is weaker than it reads.** `TODO.md` records
   "validated on hardware: v2.0.6→v2.0.10 clean, ~30 s" from 2026-08-08, but
   [guide 4](../docs/4-audio-configuration.md)'s EXPECTED OUTPUT for that step is still
   `[Pending fresh-flash capture…]`, and `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` says
   in as many words that the write duration "has never been measured in this repository". The jump
   happened; the transcript was not kept. Details in [section 7](#7-multi-version-jumps-observed).

This is reference material, not a build guide: the seven-block step structure of
[CLAUDE.md §2.1](../CLAUDE.md) does not apply here. The link and honesty rules do.

---

## Provenance

**Every finding below was measured on 2026-08-24** unless it says otherwise, and the method is named
beside it. **Nothing was flashed, no array was put into DFU mode, no `dfu-util` was run in any mode,
and no frame was contacted.** The device-side readings quoted are carried forward from
[the upstream reference](upstream-respeaker-xvf3800.md) and
[the board-revisions reference](xvf3800-board-revisions.md) and are labelled where they appear.

Methods used:

- The GitHub REST API through `gh` for repository metadata, the complete commit history, recursive
  trees at every commit, and every issue with every comment.
- `raw.githubusercontent.com` at explicit commit SHAs plus `sha256sum` for binaries.
- Direct reads of **XMOS's own source repositories** — `xmos/lib_dfu` and `xmos/host_xvf_control` —
  which is where this investigation found what Seeed does not publish.
- The XVF3800 User Guide v3.2.1 HTML, retrieved through the **Internet Archive**, because
  `www.xmos.com` answers **HTTP 403 to every direct request** from this workstation (measured six
  times across `curl` with and without browser headers, and through `WebFetch`, which additionally
  gets 406 on the HTML). The archived copy is what this file cites; the live page was unreachable.
- Direct read of the Seeed wiki page's markdown **source** in `Seeed-Studio/wiki-documents` rather
  than the rendered page.

**What could not be checked.** No live reading was taken from any array — no password was available
in-session and the task was research-only. Three reads would close three of the open items in
[section 10](#10-what-nobody-knows); they are named there.

Raw captures are under `.work/xvf-upgrade-path/` (gitignored). That directory is scratch and is not
the evidence of record: everything a conclusion rests on is restated here with the method and the
URL beside it.

---

## 1. The question, and exactly what the target is

**Target, re-verified live 2026-08-24.**
`GET https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/183ef1ca6befd592da6c4c504259335f8bb3d097/xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin`
answered **200**, **933,888 bytes**, sha256
**`60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6`** — matching the pin in
`src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` exactly.

**The complete set of USB firmware versions upstream has ever published, measured by walking the
recursive tree of all 35 commits** and collecting every `.bin` path that has ever existed:

| Version | File | Size | Status at head |
| --- | --- | --- | --- |
| 2.0.2 | `..._v2.0.2.bin` | 929,792 | **deleted** by `c3316562` (2025-06-27) |
| 2.0.3 | `..._v2.0.3.bin` | 929,792 | **deleted** by `c1484a01` (2025-07-01) |
| 2.0.4 | `..._v2.0.4.bin` | 929,792 | **deleted** by `1981d2f5` (2025-07-16) |
| 2.0.5 | `..._v2.0.5.bin` | 929,792 | **deleted** by `183ef1ca` (2026-08-14) — the target commit |
| 2.0.6 | `..._v2.0.6.bin` | 933,888 | present |
| 2.0.7 | `..._v2.0.7.bin` | 933,888 | present |
| 2.0.8 | `..._6chl_v2.0.8.bin` | 933,888 | present (six-channel variant only) |
| 2.0.9 | `..._v2.0.9.bin`, `..._v2.0.9_48k.bin` | 933,888 | present |
| 2.0.10 | `..._v2.0.10.bin` | 933,888 | present — **two different blobs under one name** |
| 2.1.0 | `..._v2.1.0.bin`, `..._v2.1.0_16k6ch.bin`, `..._v2.1.0_48k2ch.bin` | 933,888 | present |

So the operator's "roughly 2.0.2 to 2.1.0" is exact: **2.0.2 is the earliest firmware this product
has ever had published, and 2.1.0 is the newest.** Ten distinct version numbers, thirteen distinct
published files, fourteen published blobs.

**A board can be running a version that is no longer downloadable.** Upstream's own DFU guide shows
a unit with serial `101991441000000001` reporting `ver=0202`, so factory-2.0.2 units existed; the
file has not been on `master` since 2025-06-27. That matters for the gate only in that the *starting*
version is not bounded below by what upstream currently serves.

**Digests of the four withdrawn images, measured 2026-08-24** by downloading each from the last
commit at which it existed. Recorded because nothing else in this repository has them and they are
the only way to identify those bytes later:

| Version | Commit read from | sha256 |
| --- | --- | --- |
| 2.0.2 | `e5ff5fbb466ddf00ad4907296c51a3d2c37641ce` | `008b005c2d0af92199dd4e74efd63251d77eb119d6becda1c6126384663a56f6` |
| 2.0.3 | `c33165622d1065ab382c6bca42f9054d16c84d97` | `fad3d4867163f57fb3e1e817d061a17dc0dfe539f75205a6a8752960737e079b` |
| 2.0.4 | `c1484a017521a4e462bb8a2054bc8d85bfd34d1a` | `1f5fcb0ee8d59740da647f9906dd3e45c699b67edd50c1ced6ec661efe178f05` |
| 2.0.5 | `1981d2f5c66ced7928f029c31f5ba49cc65bc26c` | `b631f77f5b9558563c5afa1fc0a385ec7d24d296b6b84889777a470e472e6125` |

---

## 2. What upstream, Seeed and XMOS say about upgrade paths

### 2.1 Seeed: nothing, exhaustively

**Measured 2026-08-24 by reading every source Seeed publishes for this product in full**, not by
searching it:

- **`xmos_firmwares/dfu_guide.md`** (6,276 bytes, fetched at head). Read end to end. It says to run
  `dfu-util -R -e -a 1 -D /path/to/dfu_firmware.bin` and then re-check with `dfu-util -l`. **No
  minimum version, no ordering, no prerequisite, no mention of the currently installed version at
  all.** The example in it flashes `respeaker_xvf3800_usb_dfu_firmware_v2.0.2.bin`.
- **`xmos_firmwares/usb/changelog.md`** (2,594 bytes, fetched at head). Read end to end. Six version
  headings, v2.0.5 through v2.1.0, each an Added/Changed/Fixed list. **No migration note, no
  compatibility note, no "flash this first", no withdrawal notice, and no mention of any earlier
  version's presence being required.**
- **`host_control/README.md`** (21,813 bytes). Read; grepped for `dfu`, `upgrade`, `version`,
  `factory`, `partition`, `downgrade`, `migrat`, `prior`, `before`. The only DFU hits are the
  filenames `dfu_cmds.yaml` and `xvf_i2c_dfu` in its directory listing.
- **The Seeed wiki page**, read at source as
  `sites/en/docs/Sensor/reSpeaker_XVF3800_USB_4_Mic_Array/respeaker_xvf3800_usb_4_mic_array.md`
  (42,730 bytes) in `Seeed-Studio/wiki-documents`. Its "Update Firmware", "Safe Mode" and "Flash
  Firmware" sections were read in full and the whole file grepped for `intermediate`,
  `must first`, `before flashing`, `older version`, `earlier version`, `downgrade`,
  `sequentially`, `minimum firmware`, `at least version`. **Zero matches.** The `dfu-util -l`
  listings on the wiki are the same `ver=0202` captures as the DFU guide, reproduced verbatim — the
  wiki is not an independent reading.

### 2.2 The upstream issue corpus: nothing, exhaustively

**Measured 2026-08-24.** All **34** issues and pull requests in
`respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY` were downloaded in full with every comment (87,359
bytes of text) and grepped for `intermediate`, `step up`, `step through`, `must first`, `must be`,
`upgrade path`, `downgrade`, `revert`, `rollback`, `migrat`, `incompatib`, `bootloader`,
`boot loader`, `factory image`, `factory partition`, `minimum version`, `older firmware`,
`older than`, `newer than`, `before flashing`, `before upgrading`, `prerequisite`, `sequen`.

**Not one hit describes an upgrade-path constraint.** The hits are: the Safe Mode / factory-partition
recovery discussion in #8, #19 and #32; a request for a `REBOOT`-equivalent in #20; and the XMOS
reference-firmware experiment in #24. All are read in full in
[the upstream reference §9](upstream-respeaker-xvf3800.md) and
[the board-revisions reference §6](xvf3800-board-revisions.md).

A GitHub-wide issue search for `XVF3800 intermediate firmware` returns **0 results**; for
`XVF3800 must flash first`, `XVF3800 downgrade firmware`, `reSpeaker XVF3800 bootloader` and
`XVF3800 revert factory`, the only XVF3800-repository hits are #8 and `xmos/host_xvf_control#69`,
neither about upgrade order.

### 2.3 The one maintainer statement that bears on it, and it points the other way

**Measured 2026-08-24**, read in
[issue #8](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/8). On 2026-08-14 —
the day v2.1.0 was published — Seeed's `jerryyip` answered a user running the **six-channel v2.0.8**
build who asked what to recover with:

> The standard two-channel and 6-channel images are separate firmware builds. Flashing a standard
> v2.0.9 image will recover the device as a two-channel device; it will not preserve six-channel USB
> output. And now we have the `v2.1.0_16k6ch` firmware, so please just use this one.

That is the maintainer directing a **2.0.8 → 2.1.0 direct flash**, three published versions apart,
with no intermediate named and no caveat. It is the closest thing to a vendor statement on the
question that exists.

### 2.4 XMOS: it documents the mechanism, and the mechanism excludes an upgrade path

This is where the answer actually comes from. See [section 3](#3-why-an-upgrade-path-cannot-arise-here).

---

## 3. Why an upgrade path cannot arise here

Four mechanical facts, each from XMOS's own documentation or source. Together they mean the state of
the device before a download cannot influence what a download produces.

### 3.1 A download erases and rewrites the whole upgrade partition

**Measured 2026-08-24** from
[`xmos/lib_dfu` `doc/rst/lib_dfu.rst`](https://github.com/xmos/lib_dfu/blob/develop/doc/rst/lib_dfu.rst)
at `develop`, verbatim:

> During the DFU download process, on receiving the first `DFU_DNLOAD` command, the device starts to
> erase `FLASH_MAX_UPGRADE_SIZE` bytes of the upgrade section of the flash […] This is done by
> repeatedly calling the flash erase function until the entire upgrade section is erased, and can
> take several seconds.

And from the
[XVF3800 User Guide v3.2.1, "DFU operations"](https://www.xmos.com/documentation/XM-014888-PC/html/modules/fwk_xvf/doc/user_guide/07_dfu_operations.html)
(retrieved via the Internet Archive; the live URL 403s), verbatim:

> Only one upgrade image may be transferred to the flash of the XVF3800. The first upgrade image will
> be replaced by a subsequent upgrade process.

**Consequence.** There is no delta, no patch, no layered image and no accumulation. Whatever was in
the upgrade partition is erased before the first byte of the new image lands. A "you must install X
first" requirement has nowhere to live, because X is destroyed by the operation that would depend on
it.

### 3.2 The boot loader chooses on validity, never on version

**Measured 2026-08-24** from `lib_dfu.rst`, verbatim:

> Once a valid upgrade image is loaded in flash, on subsequent reboots, the device will boot from the
> upgrade image. If the upgrade image is invalid, the factory image will be loaded.

and

> The DFU depends on the XCORE boot process, and the role of the flash loader to run the upgrade
> image **when valid**.

and from the XVF3800 User Guide, verbatim:

> If the image downloaded to the device is not correct, for example if any data is corrupted or if
> the download is not completed, the upgrade image will be replaced, but after rebooting the
> bootloader will deem the upgrade image invalid, and the device will load the factory image.

**Consequence.** The selection predicate is *valid / invalid*. No version is compared against
anything at boot. The failure mode of a bad image is "falls back to factory", which is exactly the
`Safe Mode` behaviour Seeed documents and which
[the board-revisions reference §6](xvf3800-board-revisions.md) already records as the reason bricking
is very unlikely.

### 3.3 The device's documented download error surface has two entries, neither about versions

**Measured 2026-08-24** from the XVF3800 User Guide, "Error handling", verbatim and complete — this
is the whole list:

> The XVF3800 device supports the following errors, and they are used only during the download
> operation:
>
> **errWRITE**: this error is returned if the host sends a download request for the wrong partition,
> for example the factory partition
>
> **errADDRESS**: this error is returned if the host sends a data block outside the address range of
> the memory partition, for example if the image is too large.

**Consequence.** If the device were capable of refusing an image because of what version was
previously installed, that refusal would need a status code. The DFU 1.1 status enum has one that
would fit — `errFIRMWARE` — and XMOS's `dfu_status` enum in
[`lib_dfu/api/dfu_types.h`](https://github.com/xmos/lib_dfu/blob/develop/lib_dfu/api/dfu_types.h)
defines it. The XVF3800 documentation says the device uses **only** the two above. This is a
positive statement of absence from the vendor, not merely a gap.

### 3.4 XMOS states outright that no rollback protection exists

**Measured 2026-08-24** from `lib_dfu.rst`, verbatim, in a `warning` block:

> No security is implemented in the DFU implementation in `lib_dfu`. It is the responsibility of the
> user to implement necessary security measures in their application when using DFU functionality.

and, in the list of things a product **should** implement itself:

> **Rollback Protection**: Implement mechanisms to prevent rollback attacks, where an attacker tries
> to install an older, vulnerable version of the firmware.

**Consequence.** Version-gating a DFU download is named by XMOS as a thing the library does *not* do
and the integrator would have to add. **Inferred, and labelled as such:** Seeed would have had to
build such a gate deliberately, and nothing in any Seeed artifact — the changelog, the DFU guide, the
wiki, the issue corpus, or the DFU command table — hints that they did. This is the one link in the
chain that is inference rather than measurement, and it is inference from a complete search of
everything Seeed publishes.

### 3.5 The host tool does not compare versions either

**Measured 2026-08-24** by reading XMOS's own DFU host source,
[`xmos/host_xvf_control` `src/dfu/dfu_operations.cpp`](https://github.com/xmos/host_xvf_control/blob/develop/src/dfu/dfu_operations.cpp).
`download_operation()` opens the file, streams it in fixed blocks, polls `DFU_GETSTATUS` until
`dfuDNLOAD_IDLE`, sends a zero-length terminator, and returns. **It never reads the device's version,
and there is no comparison anywhere in the function.** `get_version()` is a separate function that
prints three bytes and is called only by the `--version` option in `dfu_main.cpp`.

`dfu-util` itself does no version matching on these images either, and the reason is recorded in
every published capture: Seeed's `.bin` files carry no DFU suffix, so `dfu-util` prints
`Warning: Invalid DFU suffix signature` and proceeds. A DFU suffix carries `idVendor`, `idProduct`
and `bcdDevice` — **measured** from XMOS's own generator signature in `lib_dfu.rst`,
`./bin/dfu_suffix_generator <VID> <PID> <bcdDevice> <input_binary> <output_file>` — so a suffixed
image is the one plausible route by which a host-side version check could ever appear here.
**Unverified:** exactly what `dfu-util` does on a suffix/device mismatch was not read from
`dfu-util`'s source or man page in this session; the `dfu-util(1)` man page fetched today does not
mention suffixes at all.

---

## 4. Did the DFU loader change across 2.0.x to 2.1.0?

**No evidence that it did, and — importantly — the field the operator suggested watching would not
have shown it.**

### 4.1 `ver=` is not a loader version

**Measured.** The `ver=` field in `dfu-util -l` output is the USB descriptor's `bcdDevice`, which on
this device encodes the **application firmware version**. Three independent version/field pairs
establish the correspondence:

| Firmware | `bcdDevice` | Source |
| --- | --- | --- |
| 2.0.2 | `0202` | upstream DFU guide, three captures (Windows / macOS / Linux), commit `e5ff5fbb`, 2025-06-25 |
| 2.0.6 | `0206` | this project's bench reading, 2026-08-20, factory spare |
| 2.0.10 | `020a` | this project's bench reading, 2026-08-20, Frame #1 |

So **`ver=` changes on every firmware version by construction**, and looking for "`ver=` differing
from `020a`" would find a difference on every board that is not running 2.0.10. It carries no
information about the loader.

**This also upgrades an existing claim in this repository from inference to documented fact.** The
`0xJJMP` decode implemented in `AudioArrayResources.Version()` — two hex digits of major, one of
minor, one of patch — was previously supported only by the two bench readings. The XVF3800 User Guide
states it directly:

> The UPGRADE_VERSION number is the 16-bit format `0xJJMP` of the executable firmware where: J is
> major, M is minor, P is patch.

### 4.2 What would show a loader change, and what it shows

Three observables, all read from the published captures:

| Observable | Every capture found | Range covered |
| --- | --- | --- |
| Alt-setting names in run-time mode | `alt=0 "reSpeaker DFU Factory"`, `alt=1 "reSpeaker DFU Upgrade"` — and nothing else | upstream DFU guide 2025-06-25 (v2.0.2 era, ×3 platforms); Seeed wiki (same captures); issue #19 prose, 2026-06-04 |
| DFU functional descriptor version | `Run-Time device DFU version 0101` and `DFU mode device DFU version 0101` — DFU 1.1, in every capture | DFU guide 2025-06, issue #3 2025-09, issue #8 2025-11 |
| DFU alternates in **Safe Mode** | three: adds `alt=2 "reSpeaker DFU DataPartition"` | issue #8, 2025-11-14, on v2.0.6 |

**The three-alternate listing is a Safe-Mode property, not a version property.** It is the reason
[decision 91](../version2.md)'s Safe-Mode rehearsal names "a third alt setting" as the proof Safe Mode
was entered. Confirming this from the other side: XMOS's `DFU_SETALTERNATE` command, whose table
Seeed ships **byte-identically** (see §4.4), is documented as *"Sets factory (0) or upgrade (1) DFU
target"* with `value_ranges: value0: [0 .. 1]` — the control-protocol path cannot address alt 2 at
all.

**No capture anywhere shows anything other than the two names in run-time mode, across fourteen
months and five firmware versions.** That is the whole of the available evidence, and it is
consistent with no loader change.

### 4.3 The one measurable discontinuity in the published line, and why it is not a loader change

**Measured 2026-08-24** by downloading all nine current USB images plus the four withdrawn ones and
comparing sizes and headers:

- **v2.0.2, v2.0.3, v2.0.4, v2.0.5 are 929,792 bytes. v2.0.6 onward are 933,888 bytes.** The step is
  exactly **4,096 bytes**, one flash sector, at the 2.0.5 → 2.0.6 boundary.
- Both sizes are whole multiples of 4,096 (227 and 228 sectors) and of 256 (the documented UA DFU
  transfer size). Every image ends in a run of `0xFF` padding, between 320 and 3,928 bytes long.
- **All thirteen images share the same header shape**: the magic `11 af 7a c0`, then eight per-build
  bytes, then `04 00 00 00`, then the repeating `<4 bytes> 10 00 00 00 <same 4 bytes> 20 00 00 00`
  pattern, with the varying field always of the form `?? ?? e0 00`.

**Conclusion: the payload crossed a sector boundary between 2.0.5 and 2.0.6. The container format did
not change.** This is worth recording because a 4 KiB size step at a version boundary is exactly the
shape a format change would take, and it is not one. (v2.0.4 and v2.0.5 additionally have
byte-identical first 32 bytes and differ in only **3.11%** of their bytes, first difference at
`0x5fdbe` — consistent with the changelog's single small v2.0.5 fix, a 100 ms mute-button debounce.)

### 4.4 The DFU command table is XMOS's, unmodified

**Measured 2026-08-24.** Seeed's `host_control/rpi_64bit/dfu_cmds.yaml` and XMOS's
`host_xvf_control/src/dfu/dfu_cmds.yaml` are **byte-identical** — both 2,507 bytes, both sha256
`67f6a982567b8d23da85c5806c40344094d21071c631344a47164cd085dddba3`. Seeed ships XMOS's DFU command
surface without change, which is one more reason to expect XMOS's documented DFU semantics to hold on
this product.

### 4.5 One loose end, recorded rather than resolved

**`Device returned transfer size` is not constant across captures**, and nothing explains it. The
XVF3800 User Guide says *"The DFU procedures of XVF3800 only support a transfer block size of 256
bytes over USB"*. Yet:

| Capture | Date | Context | Transfer size |
| --- | --- | --- | --- |
| Upstream DFU guide | 2025-06-25 | run-time detach, flashing USB v2.0.2 | **256** |
| Issue #3 | 2025-09-08 | factory-fresh board, flashing an I2S image | **4096** |
| Issue #8, maintainer | 2025-11-18 | Safe Mode, `4mb_all_ff.bin` | **4096** |

The two 4096 captures are of unknown running firmware and, in one case, of Safe Mode; the 256 capture
is the oldest. **This does not bear on the upgrade path** — `dfu-util` reads the value from the device
on every run and its own man page says the optimal value "is usually determined automatically". It is
recorded so nobody re-derives it and mistakes it for a version signal without the context. **No
capture of `dfu-util -l` or of a DFU download exists anywhere in this repository**, on any array, at
any version; every number above is somebody else's.

---

## 5. `DFU_GETVERSION`, and what the Factory partition really constrains

### 5.1 What `DFU_GETVERSION` is, stated at the strength the evidence supports

**Measured 2026-08-24**, from `dfu_cmds.yaml` — Seeed's copy and XMOS's, identical — this is the
command's **complete** definition:

```yaml
    - cmd: DFU_GETVERSION
      index: 88
      type: CMD_READ_ONLY
      value_type: TYPE_UINT8
      number_of_values: 3
      help: "DFU Servicer-specific version command. Returns device version."
      hidden: true
```

It lives in `DFU_CONTROLLER_SERVICER_RESID (0xF0)`, is read-only, returns three `uint8`s, and is
marked `hidden`.

**That one help string is the entirety of its documentation anywhere.** Measured by exhaustion:

- The XVF3800 User Guide's **Control Commands appendix** was fetched and extracted in full (21,976
  characters). Its "Device Metadata Commands" table lists `VERSION`, `BLD_MSG`, `BLD_HOST`,
  `BLD_REPO_HASH`, `BLD_MODIFIED`, `BOOT_STATUS` and `REBOOT`. **The string "DFU" appears zero times
  in the entire appendix.**
- `xmos/lib_dfu`'s `dfu_cmd_request` enum contains no `DFU_GETVERSION` at all — the standard DFU
  requests are 0–6, XMOS's own extensions are 9, 10, 40 and `0xF1`. **Index 88 is an XVF3800-specific
  extension in `sw_xvf3800`, which is not public** (confirmed again today: `api.github.com/repos/xmos/sw_xvf3800` → 404).
- `xmos/host_xvf_control`'s `get_version()` prints the three bytes and does nothing else with them.

**So the honest position: `DFU_GETVERSION` returns three version-shaped bytes described only as "device
version", and nobody — not in this repository, not in any upstream issue, not in XMOS's published
documentation — has ever recorded a reading of it.**

**A correction this forces.** [The board-revisions reference §7.3](xvf3800-board-revisions.md)
records the hypothesis that `DFU_GETVERSION` *"reports the DFU/Factory image's version, which is
written at manufacture and is not touched by an application upgrade"*, and offers it as a candidate
signal for board revision. That hypothesis has **no documentary support in either direction** — the
only text that exists says "device version", which if anything reads as the running firmware's
version and would then duplicate `VERSION`. It remains worth reading precisely because two readings
would settle it in a minute (see [section 10](#10-what-nobody-knows)), but it should not be repeated
as though the Factory-image reading were the documented one.

### 5.2 The Factory partition *does* constrain the Upgrade partition — in exactly one documented way

This is the substantive answer to the question, and it is not the version.

**Measured 2026-08-24** from the XVF3800 User Guide, "Generation of Binary Upgrade Image", verbatim:

> ```
> xflash --noinq --factory-version 15.2 --upgrade [UPGRADE_VERSION] [UPGRADE_EXECUTABLE] -o [OUTPUT_BINARY]
> ```
>
> Specify `--factory-version` value of 15.2 for all 15.3.x releases of the XTC tools. (The 15.2 value
> refers to boot loader API for the XTC tool chain).
>
> **Note**: Should a different version of the XTC tools be used in a future firmware release, the
> tools version number should be noted such that an update image of compatible format can be created.
> **The `--factory-version` must match the tools version used to build the factory image.**

**What this means, precisely.**

- The upgrade image's format is tied to the **boot loader API version of the factory image already on
  the board**. The factory image is written at manufacture and is never touched by a DFU upgrade —
  the User Guide says *"The factory image cannot be overwritten"* and Seeed's Safe Mode depends on
  that.
- **This is a build-time property of the image, and it is entirely on Seeed's side.** It is invisible
  to any host: no `dfu-util` option, no `xvf_host` command, and nothing in the USB descriptors
  reports the factory image's boot loader API version.
- **It cannot produce a need for an intermediate flash.** A mismatch produces an image the loader
  deems invalid, and the device boots the factory image — [§3.2](#32-the-boot-loader-chooses-on-validity-never-on-version)'s
  failure mode, recoverable by writing a correct image, not by writing an older one first.
- **It is, however, the one real mechanism by which a future Seeed image could fail to boot on an
  older board** — if Seeed ever moves to XTC tools with a different boot loader API and boards
  manufactured earlier carry the old factory image. **Inferred**, from the note above; no instance of
  this has ever been reported for this product, and every published image is 15.2-compatible as far
  as anyone can tell from the outside.

**This is worth carrying forward as the best-supported explanation on offer for upstream
[issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32)'s claim that
2.0.10 and 2.1.0 do not boot on one V1.1 board while 2.0.6, 2.0.7 and 2.0.9 do** — and it is
**speculation, labelled as such**, because that board is broken independently of firmware by its own
reporter's conclusion, its reset button is physically detached, and Frame #1 is a V1.1 board running
2.0.10 successfully. Recorded because it is a testable hypothesis nobody has proposed, not because
the evidence supports it.

### 5.3 What else lives in the flash, and why it survives everything

**Measured**, from `lib_dfu/api/dfu_types.h`:

```c
#define DFU_BLOCK_NUM_DATA_IMAGE_MARKER 0x8000
```

with the comment that the 16-bit block number space is *"Divide[d] in half. Top bit cleared is for
boot partition. Top bit set […] is for data partition."* This is the DataPartition that appears as
`alt=2` in Safe Mode, that holds `SAVE_CONFIGURATION`'s output, and that a firmware reflash does not
touch — which is the root cause in issues #8 and #32 and the reason this repository sends
`SAVE_CONFIGURATION` nowhere. Nothing here changes that rule.

---

## 6. The commit history and changelog, walked

**Measured 2026-08-24.** All **35** commits in the repository's history were listed with dates and
first-line messages, and the file-level history of the changelog, the DFU guide and the host README
was queried by path.

**No commit message in the entire history contains migration, compatibility, ordering or withdrawal
wording.** Every firmware commit is of the form *"feat: add/update USB firmware to vX.Y.Z, which
\<what changed\>"*. The target commit's own message is *"feat: Add USB firmware version v2.1.0; see
xmos_firmwares/usb/changelog.md for update details"*.

Three findings from the history that matter more than the absence:

1. **The USB changelog has exactly one commit in its life** — `183ef1ca`, 2026-08-14, the target
   commit. Its six version headings (v2.0.5 through v2.1.0) were **written retrospectively in one
   sitting**, thirteen months after v2.0.5 shipped. There is no changelog history to walk, and the
   changelog has never been amended. Anything it says about a version is a 2026-08-14 recollection.
2. **The DFU guide has exactly one commit in its life** — `e5ff5fbb`, 2025-06-25. It has not been
   touched in fourteen months, which is why it still shows `ver=0202` and a v2.0.2 filename. **It is
   a 2025-06-25 snapshot, not a statement about current behaviour**, and citing it as evidence of
   today's alt-setting layout would overstate it.
3. **Versions have been silently withdrawn, four times.** Each of v2.0.2, v2.0.3 and v2.0.4 was
   *deleted by the commit that added its successor*. v2.0.5 survived until `183ef1ca` deleted it —
   **the same commit that added the three v2.1.0 files.** GitHub's rename detection even reports
   `v2.0.5.bin` as *renamed* to `v2.1.0_48k2ch.bin`, which is a heuristic artifact rather than a real
   rename, but the effect is real: **the target commit removed a version from the published set and
   said nothing about it in its message or in the changelog it introduced.** No file anywhere marks
   any version as withdrawn or superseded.

This last point is the same failure mode as the twice-published v2.0.10 recorded in
[the upstream reference §2](upstream-respeaker-xvf3800.md): **this publisher changes the published
set without narrating it.** It is an argument for pinning by commit and digest, which this project
already does, and it is not an upgrade-path constraint.

---

## 7. Multi-version jumps, observed

Five observations. Each says whose it is and how far the jump was.

1. **This project, 2.0.6 → 2.0.10, three published versions skipped (2.0.7, 2.0.8, 2.0.9).** Frame
   #1's array shipped at 2.0.6 and was flashed on 2026-08-08; it has since answered `VERSION 2 0 10`
   on two readings nine minutes apart and carried a real call with 1,811 decoded video frames.
   **The event is attested. The transcript is not.** `TODO.md` records *"validated on hardware:
   v2.0.6→v2.0.10 clean, ~30 s"*; [guide 4](../docs/4-audio-configuration.md) step 3's EXPECTED
   OUTPUT is still `[Pending fresh-flash capture. …]`; and
   `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` states that the write duration *"is reported
   upstream at about thirty seconds and has never been measured in this repository"*. Those last two
   cannot both be read as authoritative alongside the first. **The honest form: the jump happened and
   the array works; the ~30 s figure is a recollection in `TODO.md` with no capture behind it.**
2. **Upstream issue #29, I2S line, v1.0.4 → v1.0.7, two published versions skipped.** `Gfermoto`,
   2026-08-11: *"After a successful DFU upgrade from v1.0.4 using that exact file (SHA256 matches the
   blob in this repo)…"*. Different product mode, same chip, same DFU implementation, same publisher.
3. **Upstream issue #31, cross-variant at the same version.** `swarajban`, 2026-08-16/20, on a Pi 5:
   flashed `v2.1.0_48k2ch`, then *"Flashing standard `v2.1.0` (16k) with the identical DSP
   profile"*, and also flashed `v2.0.9_48k` in the same investigation — several images written back
   to back with no ordering and no failures attributable to it.
4. **Upstream issue #8, 2.0.8-6ch → v2.1.0_16k6ch, recommended by the maintainer.** Quoted in full in
   [§2.3](#23-the-one-maintainer-statement-that-bears-on-it-and-it-points-the-other-way).
5. **Upstream issue #8's recovery procedure itself.** The documented repair is: erase with
   `4mb_all_ff.bin` (which terminates at ~96% with `dfuERROR status(8) … out of range` — expected),
   power-cycle, then write a full firmware image. That procedure writes a **completely blank upgrade
   partition** and then writes any version on top of it, which is the strongest possible statement
   that the previous contents of the partition are irrelevant to what may be written next.

**What none of these is.** Nobody has published a 2.0.2 → 2.1.0 flash, and nobody has published any
flash *to* 2.1.0 from below 2.0.8. The oldest starting point anyone has reported jumping from on the
USB line is 2.0.6. **The claim "any published version can go straight to 2.1.0" therefore rests on
the mechanism in [section 3](#3-why-an-upgrade-path-cannot-arise-here), corroborated by these five,
and not on any direct observation of the longest jump.**

---

## 8. A defensible version-ordering rule

### 8.1 The two spellings the code can actually see

| Reading | Route | Spelling | Example |
| --- | --- | --- | --- |
| `VERSION` | `xvf_host VERSION` over HID | three space-separated decimal integers | `2 0 10` |
| `bcdDevice` | `/sys/bus/usb/devices/*/bcdDevice`, no tool, no root | four hex digits, `0xJJMP` | `020a` |

Both are already component-wise numeric. **Nothing the code compares is ever a dotted string** — the
dotted form (`2.0.6`) exists only in upstream **filenames**, which the frame never reads.

### 8.2 The rule

> **Parse each version into a tuple of non-negative integers `(major, minor, patch)` and compare the
> tuples element by element. Never compare version text.**
>
> `xvf_host VERSION` → split on whitespace, expect exactly three tokens, each a base-10 integer in
> `0..255`. `bcdDevice` → expect exactly four hex digits, take `major = int(text[0:2], 16)`,
> `minor = int(text[2], 16)`, `patch = int(text[3], 16)`.
>
> **A reading that does not parse to exactly three integers is a refusal, not a zero and not a
> guess.**

Equivalently, and identically ordered for everything this device can report: pack as
`major * 65536 + minor * 256 + patch` and compare integers.

### 8.3 Why this is safe against the published set, checked rather than asserted

**Measured 2026-08-24 by enumerating every `.bin` path that has ever existed across all 35 commits**
in both firmware directories — 22 paths — and extracting every version token:

```
1.0.3  1.0.4  1.0.5  1.0.7  1.0.8
2.0.2  2.0.3  2.0.4  2.0.5  2.0.6  2.0.7  2.0.8  2.0.9  2.0.10  2.1.0
```

- **Fifteen distinct tokens. Every one is exactly three parts.** Zero four-part versions.
- **Zero leading-zero components.** No `2.0.06`, no `02.0.6`.
- **Zero suffixes inside a version token.** Every `_48k`, `_16k6ch`, `_48k2ch`, `_6chl` and `_test5`
  sits in the **filename**, outside the version. `respeaker_xvf3800_i2s_master_dfu_firmware_v1.0.7_48k_test5.bin`
  is the worst case and is the reason to say so: a parser that took "everything after the `v`" would
  read `1.0.7_48k_test5` as the version. **The device never spells a version that way** — but a
  future tool that derives a version from a filename would trip on exactly this file, which upstream
  has actually published.
- **The naive failure is not hypothetical.** Ordinal string comparison places `"2 0 6"` **after**
  `"2 0 10"` (because `'6' > '1'`), and both of those are real versions running on this project's two
  boards *right now*. A string comparison would conclude that the factory 2.0.6 spare is newer than
  Frame #1 and refuse to flash it.

### 8.4 The one representational ceiling, and what to do about it

**`bcdDevice` cannot represent a minor or patch of 16 or more.** The format is `0xJJMP`, one hex
nibble each for minor and patch — **now documented** by XMOS (§4.1), not merely inferred from two
readings. `2.0.10` is already `0x020A`; `2.0.15` would be `0x020F`; **`2.0.16` has no
representation.**

Consequences for the rule:

1. **Within the representable range, comparing raw `bcdDevice` integers gives the correct order**, and
   it does so across the entire published line: `0x0202 < 0x0203 < … < 0x0209 < 0x020A < 0x0210`.
   That is a free second, independent ordering check that needs no control tool at all.
2. **Outside it, `bcdDevice` and `VERSION` will disagree**, and the frame's existing gate already
   refuses on disagreement (`ArrayGateVerdict.ReadingsDisagree`) — which is the right behaviour and
   needs no change.
3. **`xvf_host VERSION` is the authority** when both are available, because its three `uint8`s can
   carry `0..255` per component. `bcdDevice` is the cheap corroborating reading, and its ceiling
   should be stated where it is decoded rather than discovered later.

Upstream is nine patch releases into a line whose patch field saturates at 15. **This is not urgent
and it is not theoretical**: the USB line went from 2.0.2 to 2.1.0 in thirteen and a half months,
nine increments, an average of one release every six or seven weeks — bursty rather than steady, but
enough that `2.0.16` would be inside a year if Seeed did not roll the minor. They did roll, to
`2.1.0`, at patch 10; that is weak evidence they will roll again in time, and it is not a guarantee.

### 8.5 What the rule cannot do, and what has to sit beside it

**Version ordering answers "is this older than the target". It does not answer "is this the target".**
Three published files report `VERSION 2 1 0` and only one of them is the pinned image; two published
files report `VERSION 2 0 9`; two report `VERSION 2 0 8`-shaped readings from different topologies.
The discriminators, in descending order of strength:

| Signal | What it proves | Cost |
| --- | --- | --- |
| `BLD_MSG` = `ua-io16-sqr` | the running build's profile, in plain text | one `xvf_host` call; **already gated on** by `ArrayHardwareGate` |
| `AEC_MIC_ARRAY_TYPE` / `AEC_MIC_ARRAY_GEO` | the geometry the firmware believes it drives | one call each; never run here |
| `dfu-util -a 1 -U` + sha256 | the exact bytes on the board | needs Safe Mode, needs hands |
| `VERSION` / `bcdDevice` | the version number, and nothing else | free |

The existing gate is already built the right way round: `ArrayHardwareGate.KnownFirmware` is an
**allow-list** of three observed-or-pinned versions rather than an ordering, and `BLD_MSG` is a
separate hard gate. **Adding an ordering comparison should extend that, not replace it** — a version
that is numerically older than the target but is not in the allow-list is still a unit this build has
never seen.

---

## 9. Real risks that are not upgrade-path risks

Surfaced because they were found on the way and each is more likely to bite than the thing that was
asked about.

**9.1 A version-only "older than target" test is blind to the 2.1.0 variants — evidenced.**
`v2.1.0_48k2ch` and `v2.1.0_16k6ch` both report version 2.1.0. A frame running either would be
judged not-older and skipped for ever. The 48 kHz build is the bad case: upstream
[issue #31](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/31) measured
`AEC_AECCONVERGED = 0` at every positive `AUDIO_MGR_SYS_DELAY` on it, with near-end speech fully
suppressed during playback — a frame that records, plays back, and cannot do echo cancellation in a
call. The same-version-different-image collision is **measured** one version down: issues #22
and #24 both record `VERSION 2 0 8` read from a device running the six-channel `6chl_v2.0.8` build.
*Mitigation already in place*: `BLD_MSG` is a hard gate. *What to check*: that the ordering test is
**AND**ed with the profile gate and cannot short-circuit it.

**9.2 A filename's version and the device's `VERSION` have already disagreed at this publisher —
evidenced, on the sibling line.** Upstream
[issue #29](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/29), 2026-08-11: a
device flashed with `respeaker_xvf3800_i2s_dfu_firmware_v1.0.7.bin`, SHA-256 verified against the
repository blob, reports `VERSION 1.0.5`. Seeed has not answered. If that ever happens on the USB
line, a frame flashed with the pinned 2.1.0 image would come back reporting something else and the
post-write re-enumeration check would report a failure that is not one. *Mitigation already in
place*: the flash polls `bcdDevice` until the array reports the target and "says so honestly when it
does not", and the authorisation is single-use so it cannot loop. *What this adds*: the honest report
is the **correct** outcome, and whoever reads it should know this precedent exists before concluding
the board is broken.

**9.3 The repository contradicts itself about whether the 2.0.6 → 2.0.10 flash was measured here.**
`TODO.md` says validated on hardware with a duration; `ArrayFirmwareFlash.cs` says the duration has
never been measured in this repository; guide 4's EXPECTED OUTPUT is a placeholder. One of those
should be corrected. *Recommendation*: keep the `ArrayFirmwareFlash.cs` wording, soften `TODO.md` to
say the flash succeeded and no transcript was kept, and leave guide 4's placeholder until the next
attended session produces a real capture — which is exactly what `TODO.md` already asks for.

**9.4 `www.xmos.com` blocks this workstation.** Every direct request returns HTTP 403 (or 406 through
`WebFetch`), including for the PDF that
[the board-revisions reference](xvf3800-board-revisions.md) was built from earlier the same day. The
Internet Archive served the HTML fine and truncates the PDF at 1 MiB. **Anything in this repository
sourced from an XMOS PDF cannot currently be re-verified by the same route**, and a future agent
should reach for `web.archive.org/web/2025id_/<url>` on the HTML pages rather than concluding the
document is gone.

---

## 10. What nobody knows

Stated plainly, because a decision made on acknowledged ignorance is better than one made on invented
certainty.

- **What `DFU_GETVERSION` actually returns on this board.** Never read by anyone, anywhere. **One
  read settles it**: `xvf_host DFU_GETVERSION` on Frame #1 (firmware 2.0.10) and on the factory
  spare (2.0.6). Equal values ⇒ it is independent of the application firmware, and the
  Factory-image hypothesis lives; values equal to each board's own `VERSION` ⇒ it duplicates
  `VERSION` and the hypothesis dies. Read-only, no writes, costs nothing, and it is the same bench
  session as the two `AEC_MIC_ARRAY_*` reads
  [the board-revisions reference §9](xvf3800-board-revisions.md) already asks for.
- **What a `dfu-util -l` on one of this project's arrays actually prints.** No such capture exists
  here. It would confirm the two-alternate run-time layout on 2.0.6 and 2.0.10 from this project's
  own hardware rather than from a fourteen-month-old upstream document, and it would settle the
  transfer-size question in §4.5. `dfu-util -l` is read-only and does not enter DFU mode.
- **Whether anyone has ever flashed 2.1.0 onto a board older than 2.0.8.** Nobody has published one.
  The mechanism says it is fine; no observation covers it.
- **Whether the boot loader API version of the factory image differs between board batches or
  revisions.** Unreadable from the host by any documented means, and undocumented by Seeed. It is the
  one genuine factory-to-upgrade compatibility axis that exists (§5.2), and nothing can measure it
  from outside.
- **Whether Seeed will roll the minor before the patch field saturates at 15** (§8.4).
- **Whether the June or the July v2.0.10 build is on Frame #1.** Unchanged from
  [the upstream reference](upstream-respeaker-xvf3800.md); this investigation adds nothing to it.

---

## Where this lands in the repository

| Concern | Where it lives |
| --- | --- |
| The upstream's structure, licence, probes and issue history | [upstream-respeaker-xvf3800.md](upstream-respeaker-xvf3800.md) |
| Board revisions, firmware profiles, and what a wrong image costs | [xvf3800-board-revisions.md](xvf3800-board-revisions.md) |
| The pinned images, their digests and the flash interlocks | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` |
| The identity gate, `KnownFirmware` and the `BLD_MSG` check | `src/FrameLink.Agent/Firmware/ArrayHardwareGate.cs` |
| The single-use authorisation and the write itself | `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` |
| The `bcdDevice` `0xJJMP` decode this file promotes from inference to documented | `src/FrameLink.Agent/Resources/AudioArrayResources.cs` |
| Why the fleet converges on a pinned image, and the binding sequencing | [decision 91](../version2.md) |
| The by-hand flash step whose EXPECTED OUTPUT is still a placeholder | [guide 4 step 3](../docs/4-audio-configuration.md) |
