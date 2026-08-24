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

**A frame must be able to flash with no network.** The intent is that these bytes are compiled into
the agent binary as an embedded resource, so a frame that has an agent has the firmware — no GitHub,
no Fleet Manager, no route to anywhere. The one operation on a frame that cannot be undone remotely
should depend on nothing outside the executable performing it. **That embedding is not built yet**;
see the table below for what exists today. Until it does, this directory is a verified copy in the
repository and nothing more.

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

Two of these exist today and two do not. The table says which, because a notice that describes a
finished design while half of it is unwritten is worse than no notice — it is the exact fault this
repository spent a session correcting elsewhere.

| Concern | Where it lives | Built? |
| --- | --- | --- |
| The pin — commit, digest, length, role | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` | yes |
| The resource that puts it on the card and re-hashes it | `src/FrameLink.Agent/Resources/XvfFirmwareImageResource.cs` | yes |
| The interlocked write that is allowed to use it | `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs` | yes |
| The bench flash | `tools/harness/flh/flash.py` | yes, but it does not read this directory yet |
| The ledger entry a bump goes through | `upstream-review.json` | yes |
| The embedded copy and how it is opened | `src/FrameLink.Agent/Firmware/XvfVendoredFirmware.cs` | **not written** |
| The `<EmbeddedResource>` that compiles it into the agent | `src/FrameLink.Agent/FrameLink.Agent.csproj` | **not added** |

The csproj already embeds `app/**/*.*` with a `LogicalName` transform that collapses path separators,
because a build on Windows and a build in the arm64 container would otherwise embed different resource
names. Whatever adds this file will need the same treatment, and that existing block is the pattern to
copy rather than a second mechanism to invent.

Two other images are pinned by this project and are **not** vendored here: the v2.0.6 fallback and
the `4mb_all_ff.bin` erase image, both still fetched from upstream at their own pinned commits.
Whether they join this directory is an open question and not an oversight.
