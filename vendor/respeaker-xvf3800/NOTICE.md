# Vendored firmware — reSpeaker XVF3800 USB 4-Mic Array

This directory holds a binary that other people built. It is stored here byte for byte, unmodified,
so that a FrameLink frame can update its microphone array without fetching anything from anywhere.

---

## What is here

### `respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin`

A DFU firmware image for the XMOS XVF3800 voice processor on the
[reSpeaker XVF3800 USB 4-Mic Array](https://wiki.seeedstudio.com/respeaker_xvf3800_introduction/).
It is the image the FrameLink fleet converges on, and the only image anything in this repository
will ever write to an array.

| | |
| --- | --- |
| **Upstream repository** | [respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY) |
| **Upstream path** | `xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin` |
| **Commit** | `183ef1ca6befd592da6c4c504259335f8bb3d097` |
| **Commit date** | 2026-08-14 |
| **Commit subject** | `feat: Add USB firmware version v2.1.0; see xmos_firmwares/usb/changelog.md` |
| **sha256** | `60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6` |
| **Length** | 933,888 bytes |
| **Retrieved** | 2026-08-24 |
| **Modified here?** | No. The bytes in this directory are the bytes that URL served. |

The build profile is `ua-io16-sqr` — 16 kHz, two channels, square microphone geometry. Upstream
published three v2.1.0 images on the same day; the two suffixed ones (`_16k6ch`, `_48k2ch`) change
the array's channel count or sample rate, and the unsuffixed one is the profile a FrameLink frame
runs. Which one is which is worked out from measurement rather than from the filename, and the
evidence is in [the board-revision reference](../../reference/xvf3800-board-revisions.md) and
[the upstream reference](../../reference/upstream-respeaker-xvf3800.md).

---

## Why the bytes are here rather than fetched

Three reasons, in the order they matter.

**A frame must be able to flash with no network.** These bytes are compiled into the agent binary as
an embedded resource, so a frame that has an agent has the firmware — no GitHub, no Fleet Manager, no
route to anywhere. The one operation on a frame that cannot be undone remotely should depend on
nothing outside the executable performing it. The agent puts this image on the card from inside
itself, and it is now the whole of what the pin names.

**That is new, and it is worth saying what changed.** This notice used to carry a caveat here: two
more images were pinned — a v2.0.6 fallback and Seeed's 4 MiB all-`0xFF` erase image
`4mb_all_ff.bin` — neither was vendored, and `ArrayFirmwareFlash`'s pre-flight refused to write
anything unless both were on the card. So an offline frame reliably *had* the target image and
reliably could not *use* it, which was the precise opposite of the reason these bytes are here. Both
images and that pre-flight were removed on 2026-08-24. The evidence is in
[the recovery-model reference](../../reference/xvf3800-recovery-model.md) and the decision is
recorded as decision 93 in [version2.md](../../version2.md); the short version is that a DFU
download already erases the upgrade section before it writes, Seeed's own documented recovery has no
erase step, the corruption the erase image was published for was fixed in firmware from v2.0.9 and
is caused by a command this repository sends nowhere, and the fallback version was one commit's
unexplained choice. Offline flashing now works.

**Upstream has already republished one filename with different contents.**
`respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin` exists as two different binaries under one name,
committed two weeks apart, 402,246 of 933,888 bytes differing, both answering `VERSION 2 0 10`. A
version number is therefore not an identity for these files. Only a commit SHA plus a content
digest names one, which is why both are recorded above and why neither is trusted from memory.

**There is no other source for this hardware.** Nobody outside Seeed can build firmware for this
board — its PDM microphone port map has never been published, and the XMOS reference build runs on
it with the microphones silent. If this file stops being served, it stops existing.

Upstream publishes no releases and no tags, so there is no version endpoint to ask and nothing that
could answer *is this newer*. A commit SHA in a `raw.githubusercontent.com` URL is content-addressed
and is the only pin available.

---

## How to check this copy yourself

Both commands below re-derive the two facts this notice rests on: that the commit named above is
the one that put this file upstream, and that the bytes in this directory are the bytes it serves.

```bash
curl -fsSL "https://api.github.com/repos/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/commits?path=xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin&per_page=1"
curl -fsSL "https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/183ef1ca6befd592da6c4c504259335f8bb3d097/xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin" | sha256sum
sha256sum respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin
```

The second and third must print `60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6`.
Both were run on 2026-08-24 and both did.

---

## Where this file is used

All of these exist. The table stays because it is what made the gap legible while two of the rows
read **not written**, and a notice that describes a finished design while half of it is unwritten is
worse than no notice — the exact fault this repository spent a session correcting elsewhere. One row
is still honest about a limit rather than a gap: the bench flash reads its own copy, not this one.

| Concern | Where it lives | Built? |
| --- | --- | --- |
| The pin — commit, digest, length, role | `src/FrameLink.Agent/Firmware/XvfFirmwarePin.cs` | yes |
| The resource that puts it on the card and re-hashes it | `src/FrameLink.Agent/Resources/XvfFirmwareImageResource.cs` | yes |
| The interlocked write that is allowed to use it | `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` | yes |
| The bench flash | `tools/harness/flh/flash.py` | yes, but it does not read this directory yet |
| The ledger entry a bump goes through | `upstream-review.json` | yes |
| The embedded copy and how it is opened | `src/FrameLink.Agent/Firmware/XvfVendoredFirmware.cs` | yes |
| The `<EmbeddedResource>` that compiles it into the agent | `src/FrameLink.Agent/FrameLink.Agent.csproj` | yes |
| The build-time proof that the embedded bytes are the pinned bytes | `tests/FrameLink.Tests/AgentVendoredFirmwareTests.cs` | yes |

The csproj embeds this file the same way it embeds `app/**/*.*`: a `LogicalName` transform that
collapses path separators, because a build on Windows and a build in the arm64 container would
otherwise embed different resource names and the difference would surface only at runtime, on a
frame. The glob is `vendor/respeaker-xvf3800/**/*.bin`, so a second image joining this directory
needs no edit to the csproj and no edit to the accessor.

**What is embedded is gzipped, and the file in this directory is not.** A managed resource is stored
verbatim, so an earlier build of this same change embedded the image raw and put all 933,888 of its
bytes contiguously into the linux-arm64 ELF — measured, at offset 7,139,897, hashing to the digest
above. Every frame in the fleet pulls the agent binary over the hourly update feed on every release,
so that is close to a megabyte of fleet bandwidth per release for a file that gzip takes to 300,074.
The build compresses into `obj/`, the resource is named
`firmware/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin.gz`, and `XvfVendoredFirmware` decompresses
on the way to the card. **The bytes in this directory stay exactly what upstream served**, because
that is the entire claim this notice makes and the only thing the `curl | sha256sum` above can check;
a committed `.gz` would be a second artifact nobody could verify against upstream without first
trusting this project's compressor.

Brotli-11 would reach 234,707 — 65,821 better, about 0.6% of the binary. It is not reachable from an
MSBuild inline task, whose compiler sees a netstandard2.0 reference set with no `BrotliStream` in it,
and every way around that either cannot resolve the type or breaks the factory's own reference set.
Buying those bytes means a real task assembly or a compressor added to the build image, which is a
standing cost in every build on both platforms and an operator's call rather than something to slip
in.

The whole cost, measured on 2026-08-24 by building the same tree three ways for `linux-arm64`
Native AOT with one version string, and verified by finding the payload in each ELF rather than by
trusting the arithmetic:

| Agent binary | Bytes | Delta | Payload in the ELF |
| --- | ---: | ---: | --- |
| Without this image | 10,795,360 | — | absent |
| Image embedded raw | 11,778,808 | +983,448 | 933,888 bytes verbatim at offset 7,139,897 |
| Image embedded gzipped | **11,123,456** | **+328,096** | 300,074 bytes verbatim at offset 7,140,761 |

The gzip blob was extracted from the shipped ELF, decompressed and hashed: 933,888 bytes,
`60fee566…`, the digest at the top of this notice. The compressor is deterministic across
platforms — the Windows build and the arm64 container produced byte-identical `.gz` output — so the
resource does not depend on where the binary was built any more than its name does.

Both deltas exceed their payload, by 49,560 raw and 28,022 gzipped. That difference is the accessor,
the install path it feeds and the resource-table entry, not the image; a resource itself is stored
verbatim, which is what the two "verbatim at offset" readings above establish directly.

**This is the only image this project pins.** Two others were, and are not any more; see the
account above. Nothing in the code names a file — the csproj globs `vendor/respeaker-xvf3800/**/*.bin`,
`XvfVendoredFirmware` keys on the name the pin already carries, and an image the binary does not
carry falls to the download path, which is kept for exactly that case. So a second image joining the
pin one day is: the file, a row in the table above, a pin entry, and a `upstream-review.json` note.
No line of the csproj and no line of the accessor.

**And it is cheap, which is the fact that made the old open question tractable and is recorded here
because it will be wanted again.** Gzipped, this 933,888-byte firmware image is 300,074 bytes — about
300 KB on every agent download, since every frame pulls the binary over the hourly update feed on
every release. A second firmware image would cost roughly the same again. An all-`0xFF` blob of
4,194,304 bytes gzips to about 4,098 bytes and would cost effectively nothing, which is why size was
never the argument against carrying it.
