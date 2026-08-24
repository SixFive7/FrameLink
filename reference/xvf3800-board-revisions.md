# FrameLink v2 — XVF3800 board revisions, firmware profiles, and what a mismatch actually costs

This file answers one question that was blocking a hardware decision: **what is different between the
ReSpeaker XVF3800 boards, and what happens if the wrong firmware is flashed onto one?** It was written
because the risk had been carried for weeks as an unquantified "the firmware might not boot on a given
board revision", and because the `sqr` in this project's arrays reporting `BLD_MSG ua-io16-sqr` had
never been decoded.

The short version, before the evidence:

- **`ua-io16-sqr` decodes completely**, from XMOS's own build documentation. `ua` = USB device
  configuration, `io16` = 16 kHz USB in/out sample rate, `sqr` = the square microphone geometry. No
  field is left guessed.
- **The square-geometry hypothesis is true, and its scary half is false.** The mic array on this board
  really is a 66 mm square, and XMOS really does ship a different geometry build. But the alternative
  is **linear**, not circular — XMOS files square *and* circular together under one option it calls
  "squarecular". Seeed's marketing word "circular" and the firmware's `sqr` describe the same physical
  array. There is no circular-variant firmware for this product to flash by mistake.
- **Exactly two board revisions are attested anywhere: V1.0 and V1.1.** Nobody — not Seeed, not XMOS,
  not any issue, forum post or datasheet — has ever published what changed between them.
- **No published firmware for this product has ever targeted a different board.** Every USB image in
  the upstream directory is a `-sqr` build for this board; the suffixes name audio topology, never
  hardware.
- **The realistic failure mode is not a brick.** The measured wrong-firmware outcome on this exact
  board is *boots fine, enumerates fine, microphones silent* — and the Factory partition that makes
  Safe Mode work is never written by an upgrade.

This is reference material, not a build guide: the seven-block step structure of
[CLAUDE.md §2.1](../CLAUDE.md) does not apply here. The link and honesty rules do.

---

## Provenance

**Every finding below was measured on 2026-08-24** unless it says otherwise, and the method is named
beside it. Nothing was flashed, no array was put into DFU mode, and no command was sent to any board
for this file — the two device-side readings quoted are this project's own captures of 2026-08-20 and
2026-08-23, carried forward from [the upstream reference](upstream-respeaker-xvf3800.md) and labelled
where they appear.

Methods used: the GitHub REST API and `gh` for repository trees, commit histories, issues and issue
comments; `raw.githubusercontent.com` at explicit commit SHAs plus `sha256sum` for binaries;
`pdftotext -layout` on the XMOS PDFs; direct reads of the Seeed wiki's markdown **source** in
`Seeed-Studio/wiki-documents` rather than the rendered page; `curl -I` for CDN `Last-Modified`
timestamps; the Internet Archive for the 2025 state of the wiki; and visual inspection of Seeed's own
product photographs.

**One thing could not be checked and it matters.** No live reading was taken from Frame #1 for this
file — no password was available in-session and the rules forbid asking for one to satisfy curiosity.
Three commands would settle three of the open questions below and cost nothing; they are named in
[What one read would settle](#one-read).

---

## 1. `ua-io16-sqr`, field by field

**Measured 2026-08-24** from the
[XMOS XVF3800 User Guide v3.2.1](https://www.xmos.com/documentation/XM-014888-PC/pdf/xvf3800_user_guide_v3.2.1.pdf)
(publication date 2024/10/29), §5.3.1 Table 5.1 and §5.3.2, extracted with `pdftotext -layout`.

The naming scheme, quoted verbatim from §5.3.2:

```text
<device config>-<sample rate>-<mic geometry>-<control protocol>[-extra-options]

where -<control protocol> is not set for UA, as it always uses USB, and -extra-options
can be -extmclk, -io-exp and -spatial.
```

Table 5.1, "Build-time combinable parameters", quoted in the fields that matter:

| Parameter | Options | Abbreviation | XMOS's note |
| --- | --- | --- | --- |
| Device configuration | INT-Device | `-intdev` | |
| | USB | `-ua` | |
| I2S LR clock rate | 16000 / 48000 | `-lr16` / `-lr48` | "ignored for UA configurations" |
| USB IN/OUT sample rate | 16000 / 48000 | `-io16` / `-io48` | "Only valid in UA configuration; ignored for INT configurations" |
| Microphone geometry | Linear | `-lin` | "Selects microphone configuration on XK-VOICE-SQ66 board" |
| | **Square or circular** | `-sqr` | |
| Control protocol | I2C / SPI | `-i2c` / `-spi` | "ignored for UA configurations, as they always use USB as control interface" |
| Audio MCLK | external | `-extmclk` | "Only valid on INT-Device configuration" |

So `ua-io16-sqr` reads, with nothing left over:

- **`ua`** — the USB device configuration. Audio is USB Audio Class 2.0 and control is USB. The
  alternative is `intdev`, where audio is I2S and control is I2C or SPI. This is the *only* field in
  the string that names a genuinely different product mode, and it is the one Seeed exposes as the
  `usb/` versus `i2s/` firmware directories.
- **`io16`** — 16 kHz on both USB directions. Input and output rates must match; §2.2 Table 2.1 says
  so explicitly. The alternative is `io48`.
- **`sqr`** — the square/circular microphone geometry. The alternative is `lin`.
- **no control-protocol field** — correct and expected, because UA builds always use USB for control.
- **no extra options** — so no `-io-exp` (the I2C-to-IO expander fitted to XMOS's eval kit), no
  `-spatial` (a stereo output that encodes speaker direction audibly), no `-extmclk`.

The `6ch` that appears in upstream's `ua-io16-6ch-sqr` is **not** in XMOS's Table 5.1. It is a Seeed
addition, announced in upstream's own changelog for v2.0.8 (**fetched 2026-08-24**): *"Added the
`ua-io16-6ch-sqr` USB Audio Class profile for six-channel, 16 kHz capture."* Its position in the
string — between the rate and the geometry — is where XMOS puts nothing, so it is an extension of the
scheme rather than a use of it.

**Corroborating example names**, measured from three independent places on 2026-08-24: the XMOS user
guide's own examples `-ua-io48-lin`, `-intdev-lr48-sqr-i2c`, `-intdev-lr48-lin-spi-extmclk` and its
release-package listing (`application_xvf3800_ua-io16-lin.xe`, `application_xvf3800_ua-io16-sqr.xe`,
`application_xvf3800_ua-io48-sqr.xe`, and the `-io-exp` and `-spatial` variants); the file
`application_xvf3800_inthost-lr48-sqr-i2c-v1.0.7-release.bin` committed in
[formatBCE/Respeaker-XVF3800-ESPHome-integration](https://github.com/formatBCE/Respeaker-XVF3800-ESPHome-integration);
and Seeed's own maintainer writing the config name out in prose (see §5.1).

---

## 2. The geometry hypothesis: half right, and the dangerous half is wrong

The hypothesis under test was that `sqr` means the array is square, that a **circular** variant of the
same product exists with its own firmware, and that a mismatch would mean wrong beamforming and wrong
direction-of-arrival geometry.

### 2.1 The array really is a 66 mm square — four independent measurements

1. **XMOS defines it.** The
   [XVF3800 datasheet](https://www.xmos.com/documentation/XM-014888-PC/pdf/xvf3800_datasheet_v3.2.1.pdf)
   §5.1.2, verbatim: *"The XVF3800 voice processor has been tested and characterised with microphones
   in a linear array placed with a 33 mm separation and a square array with a 66 mm spacing. Other
   spacings with a maximum spacing of 100 mm are possible, but uncharacterised."* The user guide §3.5
   repeats it: *"The linear configuration (-lin) comprises 4 microphones in a linear array, spaced
   33mm apart"* and *"The square configuration (-sqr) uses a 4 microphone array with a 66mm distance
   along each side."*
2. **The XMOS default config file.** User guide §7 describes `mic_geometries.yaml`, which *"contains
   the coordinates for each of the 4 mics for both the linear and square/rectangular geometries"*, and
   prints the shipped values. The linear set uses ±0.04995 and ±0.01665 on one axis — 33.3 mm spacing.
   The set labelled `SQUARECULAR_GEOMETRY` uses ±0.0333 on two axes — a 66.6 mm square. (The exact
   per-microphone assignment is scrambled by PDF text extraction; the value set is not.)
3. **The board reports it.** Seeed's own wiki publishes the output of `xvf_host.py AEC_MIC_ARRAY_GEO`
   on this product:

   ```text
   AEC_MIC_ARRAY_GEO:
   [0.033, -0.033, 0.000,
    0.033,  0.033, 0.000,
   -0.033,  0.033, 0.000,
   -0.033, -0.033, 0.000]
   ```

   Four microphones at the corners of a 66 mm square, z = 0. This is the XMOS squarecular default,
   possibly rotated in index order.
4. **The mechanical drawing says so.** Seeed's
   [ReSpeaker XVF3800 2D mechanical drawing](https://files.seeedstudio.com/wiki/respeaker_xvf3800_usb/respeaker_xvf3800_2d_mechanical_drawing.pdf),
   downloaded and text-extracted 2026-08-24, dimensions the microphone pattern as **66.00 × 66.00**
   inside a **Ø100.00** board. (The
   [CNX Software launch article](https://www.cnx-software.com/2025/07/29/respeaker-xmos-xvf3800-4-mic-array-board-features-esp32-s3-module-works-over-usb/)
   gives the board as "99 mm (Ø) x 4 mm"; the 1 mm disagreement is unexplained and unimportant.)

### 2.2 "Circular" and "square" are the same thing here — XMOS says so in one word

**Measured 2026-08-24** from the user guide's §2.2 Table 2.1 and §2.3 Table 2.2, which describe the
`-sqr` / `-lin` choice as, verbatim, *"to choose between a `squarecular` or linear microphone array"*.
Table 5.1 lists the `-sqr` option's values as *"Square or circular"*. The control command
`AEC_MIC_ARRAY_TYPE` returns *"1 - linear, 2 - squarecular"*.

That single portmanteau is the whole answer. XMOS's DSP does not distinguish a square from a circle;
it distinguishes a **degenerate one-dimensional array**, which can only resolve 180°, from a
**two-dimensional array**, which resolves the full 360°. The user guide is explicit that the linear
build's azimuth output is *"0 to 180 degrees only"*.

So Seeed's product copy — *"4-mic circular array"*, *"Quad PDM MEMS microphones in circular pattern"* —
and the firmware's `sqr` are not in conflict. The PCB is a circle; the four microphones on it sit on
the corners of a square; XMOS files that under "squarecular".

### 2.3 A geometry-mismatch risk is real — but for a different Seeed product

**This matters, because it is where the hypothesis came from and it is not imaginary.** Seeed ships a
second XVF3800 product, the **reSpeaker Flex**, which does have interchangeable arrays.
[Its wiki](https://wiki.seeedstudio.com/respeaker_flex_introduction/) (read at source 2026-08-24)
describes *"two interchangeable microphone array configurations: a circular 4-mic array for
omnidirectional 360° capture, and a linear 4-mic array for directional front-facing pickup with rear
suppression"*, on 44 mm and 33 mm spacings respectively, both connecting to the same core board over a
24-pin FPC.

And its firmware is named by geometry. The
[reSpeaker_Flex repository](https://github.com/respeaker/reSpeaker_Flex) at head carries
`respeaker_flex_usb_c16k2ch_v1.0.3.bin` beside `respeaker_flex_usb_l16k2ch_v1.0.3.bin` — `c` for
circular, `l` for linear — across USB and I2S, 16 kHz and 48 kHz, 2-channel and 6-channel. (Its wiki
lists a *different* naming — `respeaker_flex_ua-io16-cir.bin` versus `...-lin.bin` — which does not
match the repository; a Seeed documentation-versus-repository mismatch of exactly the kind this
project already budgets for.)

Note also that Flex uses `cir`, a **third** geometry token that does not appear in XMOS's table at
all. Seeed extends the scheme when it needs to.

**None of this reaches the USB 4-Mic Array.** Its four microphones are soldered to the same PCB in one
fixed pattern. There is no array to swap, and upstream has never published a `-lin` or `-cir` build for
it. **Measured 2026-08-24**: all nine USB images in `xmos_firmwares/usb/` and all seven in
`xmos_firmwares/i2s/` are for this one board, and every profile ever named for it — in the changelog,
in a maintainer's comment, or read off a device — ends in `-sqr`.

---

## 3. Board revisions: everything that demonstrably exists

**Two revisions are attested. That is the complete list, and the evidence for each is thin.**

### V1.0 — attested only by Seeed's own photographs

**Measured 2026-08-24 by reading the silkscreen in Seeed's published product images.** Two separate
photographs on the [Seeed wiki page](https://wiki.seeedstudio.com/respeaker_xvf3800_introduction/) show
the reverse of the board carrying, in silkscreen beneath the Seeed Studio logo:

```text
reSpeaker Mic Array XVF3800 V1.0
```

- `no-xiao-xvf.jpg`, the Hardware Overview image. CDN `Last-Modified` **2026-06-12**.
- `mic-outlet.png`, the microphone-port orientation image. CDN `Last-Modified` **2026-07-23**.

The same `no-xiao-xvf.jpg` was already in place in the
[Internet Archive's 2025-08-12 snapshot](http://web.archive.org/web/20250812101609/https://wiki.seeedstudio.com/respeaker_xvf3800_introduction/)
of that page; retrieving the archived copy of the image shows the identical V1.0 silkscreen. Between
2025 and 2026 the image was re-shot only to add a XIAO ESP32S3 label and to correct a mislabelled
DOA:180 arrow to DOA:0.

**So Seeed has photographed a V1.0 board, and has re-published photographs of a V1.0 board as recently
as July 2026.** Whether V1.0 was ever sold, or is only pre-production, is **not established** — no
customer photograph, review, teardown or issue report of a V1.0 board was found.

### V1.1 — attested by three physical units in two unrelated places

- **This project's two boards.** Serials `101991441260500069` (Frame #1, firmware 2.0.10) and
  `101991441260500030` (the spare, factory 2.0.6), recorded in [SESSION-STATE.md](../SESSION-STATE.md)
  and in the USB descriptors captured under `tools/harness/runs/`. Both silkscreened **V1.1**, read by
  the operator's eye — a human observation, not an instrument reading.
- **[Upstream issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32)**,
  opened 2026-08-17 by `fallais`, whose title and body both name the unit as *"board rev V1.1"*.

**Measured 2026-08-24 by exhaustive search:** a GitHub-wide issue search for `XVF3800 V1.1` returns
**exactly one result in all of GitHub** — that issue. Searches for `XVF3800 board rev`,
`respeaker XVF3800 V1.0 board` and `XVF3800 hardware version` return nothing else about revisions. Web
searches against the Seeed wiki, the Seeed forum, the Home Assistant community thread, CNX Software and
the retailer listings turn up no revision discussion at all.

### No other revision exists in any evidence

No V1.2, no V2, no A/B/C suffix, no dated revision. The Seeed wiki carries a dedicated
`ReSpeaker_2_Mics_Pi_HAT-Hardware-Revisions.md` page for a different product, so Seeed does sometimes
document revisions — **and there is no such page for the XVF3800**.

### The serial number encodes a batch, and both this project's boards are from one

**Measured 2026-08-24; the decode is inference and is labelled as such.** The Seeed Bazaar lists the
bare product's SKU as **101991441**. The serials read off this project's boards are `101991441` +
`2605` + `00069` and `101991441` + `2605` + `00030`. Upstream's own DFU guide shows a unit with serial
`101991441000000001` — same SKU, a zeroed middle field, unit 1.

So the structure looks like **SKU(9) + batch(4) + unit(5)**, and the `2605` middle field is *probably*
a date code — 2026, month or week 05. Both of this project's arrays are therefore from **one batch**,
which is consistent with both being V1.1 and is why owning two boards is much weaker evidence than
owning two boards sounds. **Seeed documents no serial format anywhere.**

---

## 4. What differs between V1.0 and V1.1

**Nothing is published. This is the honest answer and it is the most important line in this file.**

**Measured 2026-08-24 by exhaustion, not by finding:**

- **No schematic exists in public.** The wiki's Resources section lists a 2D mechanical drawing, three
  STP files and nothing else. There is no schematic PDF, no PCB source, no Eagle or KiCad project, no
  Seeed hardware repository for this board.
- **No changelog, revision note or errata.** Not on the wiki, not on the Bazaar page, not in the
  upstream firmware repository, not in the wiki page's own 30-commit history.
- **No difference is asserted anywhere** in schematic, components, connectors, microphone geometry, LED
  ring, I2C addresses, audio codec or power.

What is known about the board is known only at the V1.0 level, from
[the wiki's Main Components table](https://wiki.seeedstudio.com/respeaker_xvf3800_introduction/), the
[CNX Software launch article](https://www.cnx-software.com/2025/07/29/respeaker-xmos-xvf3800-4-mic-array-board-features-esp32-s3-module-works-over-usb/)
and the photographs, all read 2026-08-24:

| Item | V1.0, as published | Any V1.1 difference |
| --- | --- | --- |
| Processor | XMOS XVF3800, single order code `XVF3800-QF60B-C`, 60-pin QFN | unknown, undocumented |
| Microphones | 4× PDM MEMS, 66 mm square, sensitivity −26 dBFS, AOP 120 dBL, SNR 64 dBA | unknown, undocumented |
| Codec | TLV320AIC3104 | unknown, undocumented |
| LEDs | 12× WS2812 addressable RGB, plus a separate mute LED | unknown, undocumented |
| Connectors | USB-C (power + UAC 2.0 + DFU), 3.5 mm jack, JST speaker (5 W), 20-pin GPIO header, XTAG debug pads, XIAO footprint | unknown, undocumented |
| I2C | control interface on the header; the community ESPHome integration uses address `0x2C` | unknown, undocumented |
| Power | 5 V via USB or header | unknown, undocumented |
| Board | Ø100 mm drawing / 99 mm quoted, 4 mm thick | unknown, undocumented |

**The chip is not a variable.** The XVF3800 datasheet §8.5 lists **one** order code,
`XVF3800-QF60B-C`, commercial temperature, 3.3 V IO. There is no second silicon variant to get wrong.

**One negative worth stating precisely.** The USB product ID `2886:001a` is the same on both of this
project's V1.1 boards and is what every upstream issue reports, including issue #32's V1.1 unit and
issues #22 and #24. So **the PID did not change across whatever revisions exist** — convenient for the
agent's device matching, and simultaneously the removal of the most obvious channel a revision could
have announced itself through.

---

## 5. Every firmware variant upstream, and how a consumer is supposed to choose

**Listed 2026-08-24** from the tree at head `a652fe79da3a292b25decc0e1e7f267d29bb0284`, with the
digests re-measured by downloading each file and hashing it.

### 5.1 The USB directory — nine images, all `ua-…-sqr`

| File | Profile | Rate / channels | sha256 (measured 2026-08-24) |
| --- | --- | --- | --- |
| `..._v2.0.6.bin` | `ua-io16-sqr` (read off a device, issue #19) | 16 kHz, 2 ch | `c95fd3dec7597c72a24bc7e5212e6db136144956d5569f24b518ecfc1540ef09` |
| `..._v2.0.7.bin` | `ua-io16-sqr` (inferred) | 16 kHz, 2 ch | `57c9557602b57596fc88fbc3e2af99df4a58098cb078f5facbbcb0ff610d602b` |
| `..._6chl_v2.0.8.bin` | `ua-io16-6ch-sqr` (changelog) | 16 kHz, 6 ch | `8dd27762ebd87a28f0b4546f1634ece5e7eae308375d66952f7a9e3fb948266a` |
| `..._v2.0.9.bin` | `ua-io16-sqr` (inferred) | 16 kHz, 2 ch | `fd7f6da9db0bd1b60bd943f849f8283f071dae4e3af301490f342afbe470dd07` |
| `..._v2.0.9_48k.bin` | **`ua-io48-sqr` (maintainer-stated)** | 48 kHz, 2 ch | `8ef2284b20f22158a5a6469d0313deb9c859853ce47f6e31426ee8245ec0a160` |
| `..._v2.0.10.bin` (June, `17bac32a`) | `ua-io16-sqr` (inferred) | 16 kHz, 2 ch | `237f762a55624dbbd8c2f32d89760140b8cd741dd23027753fb7786141d95fe9` |
| `..._v2.0.10.bin` (July, `aeacafab`) | `ua-io16-sqr` (read off Frame #1) | 16 kHz, 2 ch | `81593709500cf02ca209fbfb028030ddc5438763ceaf5fe9019a3164705af843` |
| `..._v2.1.0.bin` | `ua-io16-sqr` (inferred) | 16 kHz, 2 ch | `60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6` |
| `..._v2.1.0_16k6ch.bin` | `ua-io16-6ch-sqr` (inferred) | 16 kHz, 6 ch | `c4857c4fd1d211b54dd2fb92b91a32afbd1baa4db5e2672ef5a3f87083760d15` |
| `..._v2.1.0_48k2ch.bin` | `ua-io48-sqr` (inferred) | 48 kHz, 2 ch | `175b1cd16959d177e2c5605bac7ce31e320dc0243109cb1b102d960c92d2684b` |

Every image is exactly **933,888 bytes**. The `v2.1.0.bin` digest matches this project's pin in
`src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs`, re-verified today.

**The maintainer-stated line is the strongest single piece of evidence in this file about profiles.**
In [issue #19](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/19), on 2026-06-03,
Seeed's `Wkstr` wrote: *"This v2.0.9_48k firmware is indeed built with the ua-io48-sqr configuration,
exposing the XVF3800 as a 48 kHz stereo (2-channel) USB audio device."* That confirms the whole scheme
from the vendor's side: rate and channel count vary, `sqr` does not.

A second independent device reading is in the same issue's opening post, from `mazhewitt` on
2026-05-19, on a **v2.0.6** unit:

```text
VERSION       = 2 0 6
BLD_MSG       = ua-io16-sqr
BLD_REPO_HASH = 4711e91ead2eaf956e1730a524ec8384e38569d3
BLD_MODIFIED  = TRUE
```

Two things follow. **`BLD_MSG` is stable across firmware versions** — the same `ua-io16-sqr` on 2.0.6
in May and on 2.0.10 on this project's board in August. And **`BLD_REPO_HASH` genuinely varies between
builds** — `4711e91e…` on 2.0.6 against `3f08f630…` on 2.0.10. That is new evidence for an open
question in [the upstream reference §2](upstream-respeaker-xvf3800.md): the field does move with the
build, so it very plausibly does distinguish the two 2.0.10 publications. It is still untested on that
specific pair.

### 5.2 The I2S directory — a different product mode on its own version line

Seven images on a `v1.0.x` line. At v1.0.8 the naming changed from `respeaker_xvf3800_i2s_*` to
`application_xvf3800_i2s_*` and became explicit about master/slave:
`application_xvf3800_i2s_master_v1.0.8_48k.bin` and `application_xvf3800_i2s_slave_v1.0.8_16k.bin`.
All are 888,832 bytes — a different size from the USB images, which is itself a coarse "is this the
right kind of firmware" check.

The ESPHome community build names its I2S image
`application_xvf3800_inthost-lr48-sqr-i2c-v1.0.7-release.bin`, which decodes cleanly as INT-device
host, 48 kHz I2S LR clock, square array, I2C control — and confirms that the I2S line is `-sqr` too.

### 5.3 How a consumer is supposed to know which one to use — and why they can't reliably

**There is no mechanism.** Measured 2026-08-24:

- **No file in either firmware directory names a board.** The version number and the suffix are the
  entire signal.
- **The suffix names a departure from an unnamed default.** `_16k6ch`, `_48k2ch`, `_6chl`, `_48k` each
  announce a change; the unsuffixed file announces nothing. **Never assume the unsuffixed name is the
  default without corroboration** — that rule already exists in
  [the upstream reference](upstream-respeaker-xvf3800.md) and this file does not weaken it.
- **The wiki is behind the repository.** Seeed's firmware table still describes only `..._v2.0.x.bin`
  (2 ch) and `..._6chl_v2.0.x.bin` (6 ch), and states *"Both firmware versions operate at a 16 kHz
  sampling rate"* — untrue since `v2.0.9_48k` shipped in June 2026 at that same maintainer's hand.
- **The changelog is the only place profile names appear**, and it names only the one that was added
  (`ua-io16-6ch-sqr` at v2.0.8). The base profile is never written down upstream at all; it is known
  only from devices and from the maintainer's issue comment.
- **The only reliable discriminator is the device itself**, after the fact: `BLD_MSG` names the build
  configuration in plain text. That is a post-flash check, not a pre-flash gate.

### 5.4 Has one filename ever carried firmware for a different board type? No.

**Measured 2026-08-24 by walking the tree of all 35 commits** in the upstream's history and collecting
every `(path, blob sha)` pair that has ever existed — 57 distinct paths.

- **Exactly one binary path has ever had more than one blob**:
  `xmos_firmwares/usb/respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin`, with two, at `17bac32a`
  (2026-06-29) and `aeacafab` (2026-07-13). Both are firmware for this board; both are `ua-io16-sqr` by
  every available indication. This is the known two-builds-one-name problem, not a board-type problem.
- **Six firmware paths existed and were later deleted**, all superseded versions of the same products:
  `usb/…_v2.0.2.bin`, `…_v2.0.3.bin`, `…_v2.0.4.bin`, `…_v2.0.5.bin`, `i2s/…_v1.0.3.bin`,
  `i2s/…_master_dfu_firmware_v1.0.4.bin`.
- **No path has ever been reused for a different product.** The Flex firmware lives in a separate
  repository entirely.

So the answer to the question as asked is **no** — with the caveat that "different board type" and
"different build of the same version" are different hazards, and upstream has demonstrated the second
one exactly once.

---

## 6. Consequences of flashing the wrong firmware, by severity

The categories below are (a) unbootable/bricked, (b) functional but degraded, (c) harmless. Each item
says whether it is **evidenced** or **inferred**, and the evidence is named.

### (a) Unbootable or bricked — one weak datum, and a structural reason to doubt it

**There is no evidenced case of a published Seeed firmware bricking this board.**

The only report that looks like one is
[issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32), whose table says
v2.0.10 and v2.1.0 *"do not boot at all, LEDs stay dark"* on a V1.1 board. Read in full (2026-08-24) it
will not carry that weight:

- The unit is **already broken on every firmware version tried**. v2.0.6, v2.0.7 and v2.0.9 boot but
  produce **no USB enumeration at all** — the issue's actual subject.
- The reporter's own conclusion is that *"the failure is independent of firmware version."*
- A follow-up comment from the same reporter discloses that *"the onboard RST button on this board is
  physically broken and detached."*
- Alt 2 (DataPartition) rejects both upload and download at offset 0, so the saved configuration that
  [issue #8](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/8) identified as the
  cause of exactly this symptom cannot be cleared on this unit.
- **Frame #1 is a V1.1 board running 2.0.10 successfully** — enumerates, answers `VERSION 2 0 10`, and
  has carried a real call.

So: **one unit, already faulty in a way no firmware fixes, with known physical damage to the reset
line.** It is the only datum anybody has and it should be cited with that context or not at all.

**The structural argument that bricking is very unlikely**, evidenced from three places:

1. **A firmware write touches only alt 1.** `dfu-util -l` on this device lists two DFU alternates: alt
   1 `reSpeaker DFU Upgrade` and alt 0 `reSpeaker DFU Factory`. `mazhewitt` states it plainly in issue
   #19 on 2026-06-04: *"The flash only writes alt 1, so the Factory image stays intact and keeps DFU
   reachable even if an upgrade goes wrong — a nice safety net if you only have one unit."*
2. **Seeed documents the Factory partition as the recovery path.** The wiki's Safe Mode section says
   the Safe Mode firmware *"stored in the Factory partition"* supports **both** USB DFU and I2C DFU,
   and lists *"You accidentally flashed something wrong and want to recover"* as a reason to use it.
   Safe Mode is entered by holding Mute while re-applying power.
3. **It works even on the worst-reported unit.** Issue #32's board, which will not enumerate on any
   firmware, still *"enumerates as DFU first try, every time"* in Safe Mode.

**The cost of a bad write is therefore physical access, not a dead board.** Safe Mode needs a human at
the frame to hold a button through a power cycle. For a fleet of remote frames that is the real
exposure, and it is an operational cost rather than a hardware loss.

**The one thing a reflash does not fix — evidenced.** Both issue #8 and issue #32 describe a corrupted
saved configuration that **survives a firmware reflash**, because DFU writes the application partition
and not the data partition. Both followed a `SAVE_CONFIGURATION`. This repository sends
`SAVE_CONFIGURATION` nowhere, deliberately, and nothing in this file changes that.

### (b) Functional but degraded — this is where the real risk lives, and it is well evidenced

**b1. Wrong microphone port map: boots, enumerates, microphones silent. Evidenced, on this exact
board.** In [issue #24](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/24) on
2026-07-17, `ors667` built a DFU image from the **official XMOS binary release**
(`application_xvf3800_ua-io48-sqr.xe`, XTC 15.3.1, `xflash --upgrade`) and flashed it to alt 1 of a
ReSpeaker XVF3800. Result, verbatim: *"The reference firmware boots and runs on this board"* —
enumerating as `20b1:4f00`, S16/2ch/48 kHz, `VERSION 3.2.1` — but *"The four microphones are silent
under reference firmware"*, with the hardware mute line excluded as a cause. Their conclusion: *"The
remaining cause is the PDM board configuration (port map / clock wiring) compiled for the XMOS eval kit
rather than this board."* They asked Seeed to publish the board's PDM port map. **Seeed has not
answered.**

This is the single most informative experiment anyone has run on this question, and its shape is the
answer to the whole "what if it's the wrong firmware" worry: **it boots, it presents as a sound card,
and it hears nothing.** Silent-but-alive, and fully recoverable by reflashing.

**b2. Wrong control interface: the intended host can no longer talk to the board. Evidenced.** The USB
firmware supports USB DFU and **not** I2C DFU; the I2S firmware supports I2C DFU and **not** USB DFU
(Seeed wiki, Safe Mode section). XMOS's own table says UA builds *"always use USB as control
interface"*. So on a XIAO-equipped unit, USB firmware removes the ESP32's control path entirely —
which is exactly
[issue #12](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/12): after flashing
the USB firmwares, the board shows *"a set of blue leds with one cyan"* (running normally) while
ESPHome logs *"Could not find XVF3800 device on any tested address"*. The mirror case is Seeed's own
FAQ: *"The reSpeaker XVF3800 ESP32 version is shipped with I2S firmware by default, so it will not
appear as a USB audio device when connected to a PC."*

**For this project this is the highest-probability mistake by far** — not a geometry error, but taking
a `.bin` from the wrong one of the two directories. The consequence is total loss of USB audio, and the
recovery is Safe Mode, i.e. hands on the frame.

**b3. Wrong sample-rate build: AEC never converges. Evidenced, measured, with a root cause.**
[Issue #31](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/31), `swarajban`,
2026-08-20: on both `v2.0.9_48k` and `v2.1.0_48k2ch`, `AEC_AECCONVERGED` reads **0** at every
`AUDIO_MGR_SYS_DELAY` from 0 to +60, near-end speech is fully suppressed during playback, and barge-in
is impossible. Root cause: *"the 48k builds ship the 16 kHz `AUDIO_MGR_SYS_DELAY` default"* of +12, so
at 48 kHz *"the mic-path echo arrives before the AEC sees the reference"*. Sweeping negative gives
`AEC_AECCONVERGED = 1` from −8 to −64. The same DSP profile on the 16 kHz build converges and barges in
normally.

**This is the sharpest warning in the whole corpus for this project.** Flashing `v2.1.0_48k2ch` instead
of `v2.1.0` would leave a frame that enumerates, records, plays back, and *silently cannot do echo
cancellation during a call* — the one thing the array is on the frame to do. Nothing in ALSA, PipeWire
or the mixer would report it.

**b4. Wrong channel count: mixer topology changes underneath everything. Evidenced.** The 6-channel
builds present a 6-channel capture endpoint.
[Issue #22](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/22) and issue #24 both
report channels 2–5 arriving as digital zeros out of the box; the fix, from Seeed's `Wkstr` and now in
the wiki FAQ, is `amixer -c N cset numid=8 on,on,on,on,on,on` and `numid=10 60,60,60,60,60,60`. On this
project's frames every mixer resource in the catalog is written against a **two-channel** card; a
six-channel build would change the control set under all of them.

**b5. Wrong LED mapping. Inferred, but with a precedent.** The LED ring is 12 WS2812s driven by the
XVF3800 with X0D33 as their power control, so the ring's colour order and timing live in firmware.
Upstream's own v2.0.6 changelog entry — *"Fixed incorrect color output on WS2812-2020 LEDs by
correcting channel ordering and improving signal timing"* — proves the mapping is firmware-encoded and
has been wrong before. A firmware built for a board with a different LED count or order would show
wrong colours or a wrong ring position. **No such case has been reported**, because no such firmware
exists for this board.

**b6. Wrong GPO map: mute stuck on, or the amplifier disabled. Inferred.** The wiki documents the GPO
pins as X0D11 (floating), X0D30 (mute LED + mic mute, high = mute), X0D31 (amplifier enable, **low** =
enabled), X0D33 (WS2812 power, high = on), X0D39 (floating). A firmware that assigned those five pins
differently would mute the microphones or disable the speaker amplifier at boot. **Not evidenced for
any published image**; listed because it is the mechanism by which a genuinely foreign firmware would
break audio without breaking enumeration.

### (c) Harmless

- **Flashing the same image twice.** DFU writes the whole application partition; the result is
  byte-identical. Idempotent by construction.
- **Flashing a *different version* of the same profile.** Every `ua-io16-sqr` build from 2.0.5 to 2.1.0
  presents 16 kHz stereo and the same control surface plus additions. This is the change this project
  actually intends, and it is the low-risk one.
- **Reading anything.** `VERSION`, `BLD_*`, `SERIAL_NUMBER`, `GPI_READ_VALUES`, `GPO_READ_VALUES`,
  `AEC_MIC_ARRAY_TYPE`, `AEC_MIC_ARRAY_GEO`, and DFU **upload** (`dfu-util -a 1 -U`) are all
  non-mutating. The DFU functional descriptor on this project's board advertises Upload Supported.

### The severity summary, in one table

| Mismatch | Severity | Evidenced? | Recovery |
| --- | --- | --- | --- |
| USB image on a USB board, wrong version | (c) harmless | yes — this project, 2.0.6 → 2.0.10 | n/a |
| I2S image on a USB-hosted board | (b) severe: no USB audio at all | yes — Seeed FAQ, issue #12 | Safe Mode, hands on the frame |
| `_48k2ch` instead of the 16 kHz build | (b) severe and **silent**: AEC never converges | yes — issue #31, measured | reflash |
| `_16k6ch` instead of the 2-channel build | (b) mixer topology changes, channels silent by default | yes — issues #22, #24 | reflash + ALSA |
| Foreign firmware with the wrong PDM port map | (b) boots, enumerates, **microphones silent** | yes — issue #24, XMOS reference build on this board | reflash |
| Wrong LED or GPO map | (b) wrong ring / stuck mute / dead amp | no — mechanism only | reflash |
| Any published Seeed image bricking the board | (a) | **no** — one damaged unit, cause disputed by its own reporter | Safe Mode |
| Corrupted saved configuration | (a)-like: no USB enumeration, survives reflash | yes — issues #8, #32 | `all_ff` erase, and it failed on one unit |

---

## 7. What the board can be asked about itself

This section exists to tell `src/FrameLink.Agent/Firmware/ArrayHardwareGate.cs` what is actually
readable. **No code is proposed here.**

### 7.1 Board revision: still not readable, and now checked twice more

**Re-measured 2026-08-24** against both control-tool command tables — the 259 uppercase tokens
extracted from the pinned `libcommand_map.so`
(`c1b424313e48cfe97c5cfce0530ac05fe47f818cc0fba15a9954198ef105282c`, 151,680 bytes, unchanged at
upstream head) and the 117-command table in `python_control/xvf_host.py`. Filtering for `BOARD`,
`REVIS`, `_REV`, `HW_`, `PCB`, `VARIANT` and `MODEL` returns **nothing that describes the board**; the
only `MODEL` hits are the DSP noise-model commands. Nor is the revision in the USB descriptors:
`bcdDevice` tracks the firmware version and `iSerial` is SKU + batch + unit.

**Board revision is silkscreen.** That conclusion in
[the upstream reference §8](upstream-respeaker-xvf3800.md) survives this investigation intact.

### 7.2 But the *geometry* is readable, and this is new

**Measured 2026-08-24.** Two commands report what the running firmware believes the microphone array
is, and **both are present in the `libcommand_map.so` this project already pins**:

| Command | resid / cmd | Access | Returns |
| --- | --- | --- | --- |
| `AEC_MIC_ARRAY_TYPE` | 33 / 73 | read-only, 1 × int32 | *"Microphone array type (1 - linear, 2 - squarecular)"* |
| `AEC_MIC_ARRAY_GEO` | 33 / 74 | read-only, 12 × float | *"Microphone array geometry. Each microphone is represented by 3 XYZ coordinates in m."* |
| `AEC_NUM_MICS` | 33 / 71 | read-only, 1 × int32 | number of microphone inputs into the AEC |

Access and parameter counts are from `xvf_host.py`'s own table and corroborated by the XMOS user
guide's Control Commands appendix; presence in the pinned binary was verified by extracting
identifier-shaped strings from the downloaded `.so`.

**What this is and is not.** These report the **firmware's** configuration, not the board's hardware.
They cannot tell a V1.0 from a V1.1. What they *can* do is prove, after a flash, that the image that
landed believes it is driving a 66 mm squarecular array — `AEC_MIC_ARRAY_TYPE == 2` and
`AEC_MIC_ARRAY_GEO == [±0.033, ±0.033, 0] × 4`. Combined with `BLD_MSG`, that turns the profile from an
inference off a filename into a post-flash measurement. **Nobody in this project has ever run either
command**; the expected values above come from Seeed's published wiki output, not from Frame #1.

### 7.3 Other channels, honestly rated

- **`BLD_MSG`** — the build configuration name in plain text. The best single post-flash check that
  exists. **Read 2026-08-23 on Frame #1: `ua-io16-sqr`.**
- **`SERIAL_NUMBER` / USB `iSerial`** — carries a batch field (§3). A batch is not a revision, but it
  is the only field that plausibly *correlates* with one, and it is free. **Inference, undocumented.**
- **`DFU_GETVERSION`** — present in the pinned command map and readable in normal mode (the ESPHome
  integration calls it at setup over I2C). It reports the DFU/Factory image's version, which is written
  at manufacture and is **not** touched by an application upgrade. If Seeed changed the Factory image
  between board revisions this would show it. **This is a hypothesis and it is untested** — no reading
  of `DFU_GETVERSION` exists anywhere in this repository or in any upstream issue.
- **`GPI_READ_VALUES` / `GPI_VALUE_ALL`** — present in the pinned binary but **absent from
  `xvf_host.py`**. The wiki records X1D13 and X1D34 as **floating** inputs, so today they read nothing
  meaningful. If a future revision strapped one of them, this is where it would appear. Not a signal
  now.
- **DFU upload** — `dfu-util -a 1 -U` from Safe Mode reads the application partition back without
  writing. It answers "what bytes are actually on this board", which is the only way to close the
  two-builds-one-name question by measurement. It requires Safe Mode, so it requires hands.
- **USB descriptors** — `bcdDevice` encodes the firmware version. **Measured on two arrays 2026-08-20**:
  `0206` on the factory 2.0.6 unit, `020a` on the 2.0.10 unit. Independently corroborated 2026-08-24 by
  upstream's own DFU guide, which shows `ver=0202` in a listing captured when v2.0.2 was the current
  published image.

---

## 8. A correction this investigation forces

**The byte-difference argument for which v2.1.0 image is the default does not hold, and should be
dropped.**

[The upstream reference §6](upstream-respeaker-xvf3800.md) and
[the resource catalog](resource-catalog.md) both cite, as the fourth of four corroborations, that
*"byte-wise `v2.1.0` and `v2.1.0_48k2ch` are the closest pair in the directory at 30.03% differing,
against 46.17% between `v2.1.0` and `v2.1.0_16k6ch`, which is what two builds sharing a channel
topology and differing only in sample rate look like."*

**Measured 2026-08-24** by downloading all nine USB images plus the withdrawn June 2.0.10 and computing
every pairwise byte difference (45 pairs):

- `v2.1.0` vs `v2.1.0_48k2ch` — **30.04%**. Reproduced.
- **`v2.0.9` vs `v2.0.9_48k` — 44.10%.** This is the *same kind of pair*: one version, one profile
  change, 16 kHz against 48 kHz, 2 channels both sides, confirmed by the maintainer as `ua-io16-sqr`
  against `ua-io48-sqr`. It is nowhere near 30%.
- `v2.0.7` vs `v2.0.9` — **28.31%**, the closest pair in the whole set, and they are two *different
  versions* of the same profile.
- All 45 pairs fall between 28.31% and 47.52%.

So a rate-only change produced 44% in one case and 30% in another, while a version-only change produced
28%. **The metric does not discriminate**, which is what compressed payloads do: differences saturate
and the residual variation is noise. The 30.03% figure is a coincidence, not evidence.

The other three corroborations for "the unsuffixed `v2.1.0` is the 16 kHz 2-channel build" are
untouched, and this file adds a fourth that is stronger than the one being withdrawn: **the upstream
maintainer wrote the `ua-io48-sqr` config name out for the suffixed 48 kHz build in issue #19**, which
makes the suffix-names-the-departure reading the vendor's own.

Two smaller measurements, recorded so nobody re-derives them:

- **The low-entropy block near the start is not shared across the directory.** All ten images have
  entropy 1.843 bits/byte over `0x62`–`0x33fa5` and ~5.64 bits/byte after it, but the *contents* of
  that block are byte-identical only among **v2.0.9, v2.0.10 (June) and v2.0.10 (July)**. Every other
  pair diverges at offset **`0x68`**, 104 bytes in. The earlier note that the block is "identical
  between the two builds" was true of that pair and does not generalise.
- **The header shape.** All ten images open with the same four bytes `11 af 7a c0`, then eight bytes
  that differ in every image, then `04 00 00 00`, then a repeating `<4 bytes> 10 00 00 00 <same 4
  bytes> 20 00 00 00` pattern. The varying 4-byte field sits in `0x00e0xxxx` in every image. **Not
  decoded** — doing so would mean reverse-engineering an XMOS boot image, which
  [decision 63](../version2.md) forecloses.

---

<a id="one-read"></a>

## 9. What one read would settle

Three commands, all read-only, all through the tool already installed on Frame #1, all costing nothing
and writing nothing. They close four open questions between them.

1. **`xvf_host AEC_MIC_ARRAY_TYPE`** — expected `2` (squarecular). Confirms from the device that the
   running firmware is configured for this board's geometry, rather than from a filename.
2. **`xvf_host AEC_MIC_ARRAY_GEO`** — expected the twelve floats `±0.033 ±0.033 0.000` in some order.
   Confirms the 66 mm square against Seeed's published value and against the XMOS default.
3. **`xvf_host DFU_GETVERSION`** — value unknown, never read by anyone. If it reports the Factory image
   version, it is the first candidate anywhere for a field that could differ between board revisions,
   and reading it on both of this project's arrays — one 2.0.6 factory, one upgraded to 2.0.10 — would
   show whether it is independent of the application firmware.

And one that needs Safe Mode and hands, worth doing the next time a frame is opened anyway:

4. **`dfu-util -a 1 -U readback.bin`**, then hash the first 933,888 bytes. Settles which of the two
   2.0.10 builds Frame #1 is actually running, which is currently inference from dates.

---

## 10. What nobody knows

Stated plainly, because a decision made on acknowledged ignorance is better than one made on invented
certainty.

- **What changed between V1.0 and V1.1.** No schematic, no changelog, no errata, no statement, no
  photograph of a V1.1 board from Seeed. Not "hard to find" — absent.
- **Whether V1.0 ever shipped**, or is a pre-production board that only ever appeared in product
  photography.
- **Whether a V1.2 or later exists.** Nothing suggests one; absence of evidence, at a vendor that
  documents nothing, is weak.
- **Whether any firmware behaves differently across revisions.** The only claim is issue #32's, from a
  unit its own reporter says is broken independently of firmware, with a detached reset button, and it
  is contradicted by Frame #1.
- **This board's PDM port map.** Asked for publicly in issue #24 on 2026-07-17; no answer. It is the
  reason the XMOS reference firmware runs on this board with silent microphones, and the reason nobody
  outside Seeed can build firmware for it.
- **Which of the two v2.0.10 builds Frame #1 runs.** Inference from dates, as
  [decision 90](../version2.md) already labels it.
- **Whether `BLD_REPO_HASH` distinguishes the two v2.0.10 builds.** It now demonstrably varies between
  *versions* (§5.1), which makes it likelier, and it remains untested on that pair.

---

## Where this lands in the repository

| Concern | Where it lives |
| --- | --- |
| The upstream's structure, licence, probes and issue history | [upstream-respeaker-xvf3800.md](upstream-respeaker-xvf3800.md) |
| The pinned images, their digests and the flash interlocks | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` |
| What the agent will and will not write firmware to | `src/FrameLink.Agent/Firmware/ArrayHardwareGate.cs` |
| Why board revision is absent from the firmware telemetry | `src/FrameLink.Agent/Telemetry/ArrayFirmwareReporter.cs` |
| Why the fleet converges on a pinned image, and the sequencing | [decision 91](../version2.md) |
| The 30.03% byte-difference claim that this file withdraws | [upstream-respeaker-xvf3800.md §6](upstream-respeaker-xvf3800.md), [resource-catalog.md](resource-catalog.md) |

---

## 11. Does board revision change the **v2.1.0** decision? — added 2026-08-24, second session

This section answers a narrower question than the rest of the file: not *what are the revisions*, but
whether revision interacts with **v2.1.0 specifically** — the unsuffixed build, upstream commit
`183ef1ca6befd592da6c4c504259335f8bb3d097`, sha256
`60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6`. It is appended after the closing
table rather than woven in, so nothing above is disturbed; §11.8 lists the two places above that it
corrects.

**Everything below was measured on 2026-08-24** unless it says otherwise. Nothing was flashed, no
array was put into DFU mode, no frame was contacted, no command was sent to any board. Methods: `gh`
and the GitHub REST API for issues, comments, issue timelines, per-path commit histories, code search
and issue search; `raw.githubusercontent.com` at explicit commit SHAs plus `sha256sum`; Git LFS
pointer files read directly for the hashes and sizes of binaries stored out of tree; direct reads of
the Seeed wiki's markdown **source** in `Seeed-Studio/wiki-documents` at `docusaurus-version`, plus
that page's 30-commit history; the Internet Archive for XMOS documentation pages that return HTTP 406
to a fetch and 403 to a browser agent; a Python printable-string extraction over the pinned
`libcommand_map.so` with a positive control; and web search against the Seeed forum, the Home
Assistant community, Reddit and `xcore.com`.

### 11.0 The answer

**Flashing v2.1.0 onto a V1.1 board carries no revision-specific risk distinct from the ordinary risk
of any flash.** Six findings carry that, in descending order of strength:

1. **v2.1.0 changes nothing that is board-matched.** Its four changelog entries are an audio-manager
   routing mux, two runtime commands for the AIC3104's analogue output level, flash persistence of
   those two values, and a default gain moved from 6 dB to 8 dB. The PDM microphone port map, the
   GPIO/GPO pin assignment, the LED count and ordering, the power sequence, the microphone geometry
   and the USB descriptors — the surfaces XMOS names as needing to match the hardware — are untouched
   (§11.3).
2. **The one hardware-adjacent change is a value in a register the firmware has written since v2.0.6,
   and Seeed walks that value up *and down* across versions on both firmware lines** — which is what
   a tuning knob looks like, not a board-match parameter (§11.3).
3. **v2.0.10 → v2.1.0 is a smaller delta than v2.0.9 → v2.0.10.** v2.0.10 reworked USB endpoint
   descriptors, packet sizes, FIFOs, buffers, alternate settings and software-PLL state; v2.1.0 does
   not touch USB at all. Frame #1 is a V1.1 board running v2.0.10 successfully (§11.3).
4. **No report anywhere, by anyone, ties a v2.1.0 failure to a board revision** except upstream issue
   #32, whose own reporter attributes his failure to something version-independent (§11.1, §11.2).
5. **v2.1.0 has exactly one published build**, so the two-builds-one-name hazard that genuinely
   applies to v2.0.10 does not apply here (§11.4).
6. **A download erases the whole upgrade partition and the boot loader selects on validity, not
   version**, so there is no version-dependent state carried across a flash for a board difference to
   interact with — established in [the upgrade-path reference §3](xvf3800-upgrade-path.md) and
   load-bearing here.

**What is *not* being claimed.** Not that V1.0 and V1.1 are electrically identical — that is unknown
and stays unknown (§11.5). Not that firmware and board revision never interact on XVF3800 products —
one documented case exists, on a different product, and it is worth reading (§11.6). Not that issue
#32 is explained — it is not, and §11.7 gives the three candidate explanations and the single
question that discriminates between them.

The residual risk of flashing v2.1.0 is the ordinary risk of any flash: an interrupted or invalid
write leaves a board that boots the Factory image, and recovery needs a finger on the Mute button
through a power cycle. That cost is physical access, it is unchanged by revision, and §6 already
prices it.

### 11.1 Issue 32, re-read in full today — verified, with one scoping correction

Both claims this file makes about
[issue #32](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/32) are **verified
verbatim**. The body contains, exactly: *"The failure is independent of firmware version."* The
reporter's follow-up comment of 2026-08-17T12:42:41Z contains, exactly: *"the onboard RST button on
this board is physically broken and detached."*

Two refinements, both of which make the citation more honest rather than less:

- **The reset button is offered as a question, not a conclusion.** The comment continues: *"Could a
  damaged or detached reset button be related to this failure?"* Citing it as the reporter's
  explanation overstates it. It is a disclosed hardware defect on the unit, which is enough.
- **"Independent of firmware version" is scoped to the enumeration failure, not to the boot failure**,
  and the very next sentence says so. In full: *"The failure is independent of firmware version.
  Separately, v2.0.10 and v2.1.0 do not boot at all on this V1.1 board, while v2.0.6, v2.0.7 and
  v2.0.9 do."* So the reporter did **not** conclude that the dark-LED behaviour of 2.0.10 and 2.1.0
  is version-independent — he explicitly separated it, and then asked about it directly. His question
  3 is, verbatim: *"Should v2.0.10 and v2.1.0 be expected to boot on a V1.1 board, or is there a
  hardware revision constraint on those builds?"* That is this section's question, asked upstream and
  unanswered.

  This does not rescue the issue as evidence — the board fails to enumerate on every version, is
  disclosed as physically damaged, and Frame #1 is a V1.1 board running 2.0.10 — but the short form
  *"the reporter says the failure is version-independent"*, which
  [SESSION-STATE.md](../SESSION-STATE.md) records under things corrected, is true of the enumeration
  failure and **not** of the boot failure. §11.7 takes the boot failure seriously on its own terms.

**What has changed since 2026-08-17: exactly one comment, and it is ours.** The issue's complete
timeline is five events — a mention and a subscribe for `Swissola`, a cross-reference from issue #8,
the reporter's own comment, and one comment from `SixFive7` at 2026-08-23T21:03:10Z. That last is
this project's account, so it is **not independent corroboration of anything**; it is us asking the
reporter which of the two v2.0.10 builds he flashed and telling him what Frame #1 reports.

- **No maintainer has responded.** Neither `jerryyip` nor `Wkstr` has posted. The issue is open,
  unlabelled, and 7 days old.
- **Nobody else has reproduced it.** No second reporter, on any board revision.

**The linked issue, read in full.**
[Issue #8](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/8) was closed
2026-08-14 and carries six comments: three independent reproductions of the `save_configuration`
corruption (`iPre-Innovate`, `johens`, `Swissola`), `jerryyip`'s `4mb_all_ff.bin` recovery, `Wkstr`
pointing at v2.0.9, and `fallais` cross-posting to #32. `jerryyip` states, verbatim, that the
corruption *"has been fixed in version 2.0.9 and later versions"* — which, if true, makes 2.1.0
strictly safer than the 2.0.6 the spare array shipped with. **Not one of the three reproductions
mentions a board revision, and not one reports a version-specific boot failure.**

### 11.2 Nobody else, anywhere, reports a version-specific failure that names a board revision

Absence of evidence is the answer, and here is the size of the search.

**Every issue in the upstream tracker, body and comments, scanned for the string `2.1.0`** — all 34
issues, fetched individually. Four hit:

| Issue | What the mention is | Names a board revision? |
| --- | --- | --- |
| #8 | `jerryyip` recommending `v2.1.0_16k6ch` for six-channel recovery | no |
| #31 | `swarajban`'s AEC-convergence defect on **`v2.1.0_48k2ch`** | no |
| #32 | the V1.1 report | yes — the only one in existence |
| #34 | `ramkumarkoppu` citing #31 and #24 while asking for a 16 kHz I2S master build | no |

Issue #31 is worth naming precisely because it is a **real, measured, root-caused v2.1.0 failure** —
and it is a *profile* failure, not a board failure. `swarajban`'s follow-up of 2026-08-20 identifies
the cause as the 48 kHz builds shipping the 16 kHz `AUDIO_MGR_SYS_DELAY` default of +12, sweeps
negative to find `AEC_AECCONVERGED = 1` from −8 to −64, and reports the same defect on `v2.0.9_48k`.
It is therefore evidence *against* a revision reading: the one v2.1.0 bug anyone has characterised
reproduces on a different version and is explained entirely by a DSP default.

**GitHub-wide, four issue searches**: `XVF3800 2.1.0` (1 result, an unrelated Reachy Mini
host-controller thread), `XVF3800 "V1.1"` (1 result — issue #32, alone in all of GitHub),
`XVF3800 board revision` (6 results, five unrelated), `XVF3800 "does not boot"` (2 results, both
upstream and neither about revisions).

**GitHub-wide code search for `"XVF3800" "V1.1"`: 23 files, and every one is a *firmware* version,
not a board revision.** The pattern is consistent — `linuxphile/home-assistant-voice` writes
*"ReSpeaker Lite needs the I2S/DFU firmware `respeaker_lite_i2s_dfu_firmware_48k` v1.1.0"*, and
`Goifal/mindhome` writes *"der Chip muss die I2S-Firmware (≥ v1.1.0, 48kHz-Version) haben"*. This is
a useful negative: the string that would find a V1.1 board discussion is saturated by an unrelated
firmware version number, which is one reason the revision question stays invisible.

**Elsewhere:** the Seeed forum's only XVF3800 firmware thread,
[ReSpeaker XVF3800 hangs after computer reboot](https://forum.seeedstudio.com/t/respeaker-xvf3800-hangs-after-computer-reboot/294782),
names one firmware version (*"I am on firmware 2.0.7"*), no board revision, and describes the
warm-reboot USB re-init defect already tracked as upstream #20 and #26. Web searches against Reddit,
the Home Assistant community's ESPHome thread, `xcore.com` and Seeed's Bazaar listings return nothing
about a version-specific failure on any revision. The
[Seeed wiki page's own history](https://github.com/Seeed-Studio/wiki-documents) — 30 commits on the
XVF3800 page — contains no revision note, and **the wiki has never mentioned v2.1.0 at all**; it
still documents only `v2.0.x` and `6chl_v2.0.x`, which extends this file's existing "the wiki is
behind the repository" finding from one build to the whole 2.1.0 release.

### 11.3 What v2.1.0 changes, and the mechanism by which each change could or could not matter

The complete `xmos_firmwares/usb/changelog.md` entry, quoted in full:

```text
## v2.1.0 (Current)

### Added

- Added configurable USB direct-output routing for channels 3 through 6. The new `AUDIO_MGR_OP_CH3`,
  `AUDIO_MGR_OP_CH4`, `AUDIO_MGR_OP_CH5`, and `AUDIO_MGR_OP_CH6` commands allow each channel's source
  to be selected independently, with support for product defaults and saved configurations.
- Added runtime control of the AIC3104 headphone and line-output levels through the
  `AIC3104_HP_LEVEL` and `AIC3104_LINEOUT_LEVEL` commands. Both outputs support gains from 0 dB to
  9 dB.
- Added flash persistence for the AIC3104 output levels. The values are included in
  `SAVE_CONFIGURATION` and restored during startup.

### Changed

- Changed the default AIC3104 headphone and line-output gain to 8 dB.
```

That is the whole of it, and the commit that carried it —
`183ef1ca6befd592da6c4c504259335f8bb3d097`, 2026-08-14T05:51:10Z — adds the changelog and three
`.bin` files and nothing else. There is no source in this repository to read; `xmos/sw_xvf3800` is
still 404.

**Change 1 — `AUDIO_MGR_OP_CH3`…`CH6`.** The audio manager's output multiplexer, selecting which
internal source feeds each USB capture channel. Its siblings are already in the pinned command map
(`AUDIO_MGR_OP_L`, `AUDIO_MGR_OP_R`, `AUDIO_MGR_OP_PACKED`, `AUDIO_MGR_OP_ALL`, the `_PK0..2`
variants). This is a software routing choice inside the DSP graph, with no pin, bus or clock
implication. **No hardware-difference mechanism exists.** It is also only meaningful on the
six-channel build; on the two-channel target there are no channels 3–6 to route.

**Change 2 — `AIC3104_HP_LEVEL` and `AIC3104_LINEOUT_LEVEL`. This is the only change in v2.1.0 that
reaches hardware outside the XVF3800 die, and it is worth naming precisely rather than gesturing at.**
The commands are `resid 48`, `cmd 11` and `cmd 12`, read/write, one `uint8` each, range `[0 .. 9]` —
read from `python_control/xvf_host.py` at commit `5bb73fec64b8`, which added exactly those two lines
and touched nothing else. `resid 48` is the device/product servicer that also carries `VERSION`,
`BLD_MSG`, `USB_BIT_DEPTH`, `SAVE_CONFIGURATION` and `CLEAR_CONFIGURATION`. The target is the
TLV320AIC3104 codec, which the wiki's Main Components table already names as this board's codec and
which Seeed's own
[XIAO volume-control sample](https://wiki.seeedstudio.com/respeaker_xvf3800_xiao_volume/) addresses
at I2C `0x18` with output-level registers `HPLOUT 0x33`, `HPROUT 0x41`, `LEFT_LOP 0x56`,
`RIGHT_LOP 0x5D` — that sample's own comment gives the level scale as *"range 0–17 (0–8 = DAC, 9–17 =
analog boost)"*, against which the firmware's `[0 .. 9]` reads as the analogue output stage. So the
mechanism, stated concretely: **the firmware writes an analogue output-level value over I2C to a
codec part, at an address and into registers that are properties of the part, not of the board.**

**Change 3 — flash persistence of those levels.** *"included in `SAVE_CONFIGURATION` and restored
during startup"*. This is the only change in v2.1.0 that adds anything to the **startup path**: a read
of two more fields from the data partition, followed by the codec write above. It is worth flagging
because it is the one mechanism by which v2.1.0 could plausibly behave differently from v2.0.10 on a
board whose saved configuration is corrupt — which is issue #32's actual subject. **That is a
data-partition interaction, not a board-revision interaction**, and this project sends
`SAVE_CONFIGURATION` nowhere, so its data partition is whatever the factory left.

**Change 4 — the default gain, 6 dB → 8 dB.** A value change in a register the firmware has already
been writing at startup since v2.0.6, whose changelog reads *"Increased the default AIC3104 headphone
and line-output gain from 0 dB to 6 dB."*

**The strongest de-risking evidence in this section: Seeed moves this exact gain up *and down*, on
both firmware lines, on the same board.** From the two changelogs, measured today:

| Line | Version | AIC3104 headphone / line-out default |
| --- | --- | --- |
| USB | v2.0.6 | 0 dB → **6 dB** |
| I2S | v1.0.5 | 0 dB → **6 dB** |
| I2S | v1.0.7 | 6 dB → **3 dB** (reduced) |
| USB | v2.1.0 | → **8 dB** |

A parameter that is walked 0 → 6 → 3 on one line and 0 → 6 → 8 on the other, on hardware that did not
change between those releases, is an audio-tuning knob. A build-time value matched to a board's
output network does not behave like that. The worst case if 8 dB is wrong for a given board is 2 dB
too much level on the headphone jack and the line-out feeding the speaker amplifier — audible,
adjustable at runtime by the very commands the same release adds, and reversible by reflashing.

**What v2.1.0 does not touch, which is the list that decides the question.** XMOS states the
board-matching requirement itself, verbatim, in
[Building the Application Firmware](https://www.xmos.com/documentation/XM-014888-PC/html/modules/fwk_xvf/doc/user_guide/05_building_the_firmware.html)
(retrieved via the Internet Archive; the live URL returns HTTP 406 to a fetch and 403 to a browser
agent): *"A set of firmware images is provided in the binary release package which are configured to
run correctly on the XK-VOICE-SQ66 development kit. However, when using the XVF3800 in a product
design it is normally necessary to modify the firmware to match the hardware and to configure a
number of settings. This is achieved by modification of the configuration files supplied as source
code and rebuilding the modified code to create a new firmware image."* That sentence is the general
form of [issue #24](https://github.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/issues/24)'s
measured result — the XMOS reference build boots on this board with silent microphones — and it names
the surfaces that matter: the PDM port map and clock wiring, the GPIO/GPO assignment, the LED
configuration, the microphone geometry, the power and clocking setup. **v2.1.0's changelog touches
none of them, and its commit adds no configuration file at all.**

**And the comparison that should settle the intuition.** The release *before* the one under
consideration is the one that reworked hardware-adjacent state: v2.0.10's changelog restores
*"High-speed and full-speed endpoint descriptors, packet sizes, FIFOs, buffers, alternate settings,
and software-PLL state […] for the active bus speed"* and decouples DOA tracking from the LED effect.
If any recent build were going to interact with a board-level timing or USB difference, it is
v2.0.10 — and **Frame #1 is a V1.1 board that has run v2.0.10 through real calls.** v2.1.0 changes
nothing about USB.

### 11.4 v2.1.0 has been published exactly once

**Measured by walking the per-path commit history of all three v2.1.0 binaries.** Each has exactly one
commit in its life: `183ef1ca6befd592da6c4c504259335f8bb3d097`, 2026-08-14. `v2.1.0.bin` downloaded
from that commit and from `master` both hash
`60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6` at 933,888 bytes — identical
files, matching the pin in `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs`.

**So the two-builds-one-name hazard is specific to v2.0.10 and does not apply to v2.1.0.** That
matters for issue #32 in the opposite direction from how it was raised: our own comment there asks
which v2.0.10 build the reporter flashed, and that question is well-founded, but there is no
equivalent ambiguity about which v2.1.0 he flashed. There is only one.

One artifact worth recording so nobody rediscovers it as a finding: GitHub's API reports
`v2.1.0_48k2ch.bin` in that commit as **renamed from `v2.0.5.bin`**. That is rename-detection
similarity heuristics pairing a deletion with an addition in the same commit — v2.0.5 was one of the
six superseded images deleted there. It is not evidence of any relationship between the two builds.

### 11.5 V1.0 versus V1.1, electrically: still nothing, and this is how hard the second look was

**Nothing published. Unchanged from §4, re-searched today with the question narrowed to "a difference
that would matter to firmware".** What was specifically looked for, and where:

- **A different codec part or I2C address.** Seeed's wiki, its XIAO volume sample and the community
  ESPHome integration all name one part (TLV320AIC3104) at one address (`0x18`), with the XVF3800's
  own control interface at `0x2C`. No second part, no alternative address, anywhere.
- **A different microphone or a changed PDM port map.** The port map has been publicly requested in
  issue #24 since 2026-07-17 and Seeed has never answered for *any* revision, so there is no baseline
  against which a change could be described. No microphone part number appears in any document beside
  the sensitivity / AOP / SNR figures, which are quoted once and never revised.
- **A different LED driver or count.** 12× WS2812 in every source, including v2.0.6's changelog fix
  for WS2812-2020 channel ordering. No second configuration is described.
- **A changed power rail.** 5 V via USB or header, in every source. No revision-scoped statement.
- **Any schematic, changelog, errata or engineering-change notice.** Searched the Seeed wiki source
  and its 30-commit page history, the Bazaar and reseller listings, the upstream repository, and the
  web for `hardware revision` / `ECN` / `engineering change` / `errata` against this product. The
  wiki's Resources section still lists a 2D mechanical drawing and three STP files and nothing else.
- **Any third party naming a V1.1 board.** GitHub-wide code search returns 23 files matching
  `"XVF3800" "V1.1"` and **all 23 are firmware version numbers** (§11.2). No teardown, no review, no
  photograph of a V1.1 board from anyone including Seeed.

**Say it plainly: there is no evidence that V1.0 and V1.1 differ electrically, and no evidence that
they do not.** The USB PID `2886:001a` is identical across everything anyone has reported, which
removes the one channel a revision could have announced itself through. This is absence, not
reassurance — but it is absence that has now been looked for twice, along different axes, with the
same result.

### 11.6 The one documented XVF3800 firmware-versus-board-revision interaction anywhere — and it is not this board

**Found today, and it is new to this file.** Pollen Robotics ships the Reachy Mini with an
XVF3800-based audio board and publishes its own firmware in
[the reachy_mini repository](https://github.com/pollen-robotics/reachy_mini) under
`src/reachy_mini/assets/firmware/`. Its changelog for **v2.1.3** says, verbatim:

```text
## 2.1.3

*For Beta units only*

Fixes the initialization issue on Reachy Mini beta hardware. An additional 2-second delay is added
during initialization to prevent the XMOS chip from starting before the other components.

There is no need to apply this firmware to the Lite and Wireless versions, as the issue is fixed at
the hardware level.
```

This is the first concrete instance anybody has of an XVF3800 firmware release scoped to a board
revision, and it deserves to be read carefully in both directions.

- **It proves the category is real**, and it names the mechanism: **startup sequencing** — the XMOS
  chip coming up before the components around it. That is exactly the class of fault that would
  present as "boots on some versions, dark on others", which is issue #32's shape.
- **But the direction is the reassuring one.** The *newer* firmware accommodates the *older* board, by
  waiting longer. The newer boards needed no firmware change at all, because the fix was made in
  hardware. Nothing here describes a newer firmware failing on an older board.
- **And it is a different product, board, vendor and firmware line.** Reachy Mini's images are
  `reachymini_ua_io16_lin_v2.1.x` — note `-lin`, a **linear** array. All four are 933,888 bytes,
  exactly the size of Seeed's USB images, and none of their hashes matches any Seeed image:

  | File | sha256 (from the Git LFS pointer, 2026-08-24) |
  | --- | --- |
  | `reachymini_ua_io16_lin_v2.1.2.bin` | `661429a93b155cdbfed1e6c214820fd42e3372a08c3930f14fcd4e6db5be6796` |
  | `reachymini_ua_io16_lin_v2.1.3.bin` | `0e35fa9a43a4e75890fd6299e62e85310d8081087e6a9702aaef0c81bd3af283` |
  | `reachymini_ua_io16_lin_v2.1.4.bin` | `803c9174f0ddb8bca9bc7fd5418c2051b720391ea6a0c4f77b36267b368446c7` |
  | `reachymini_ua_io16_6ch_lin_v2.1.4.bin` | `213f55c4591624c71965ea7a859eaeceeb547a9680beea83e7101a9d751b6969` |

**Two side findings fall out of this, both corroborating §1 and §2 of this file.** First, a second
vendor uses the same `ua-io16-<geometry>` scheme independently, and Seeed's `6ch` insertion appears
there too (`ua_io16_6ch_lin`) — so that field position is a shared convention, not a Seeed
idiosyncrasy. Second, **this is the first `-lin` XVF3800 UA build found in a shipping product**, which
makes the geometry axis concrete rather than theoretical: linear builds exist, they are published, and
they are for a board with a linear array. None of them is published for the ReSpeaker USB 4-Mic Array,
whose four microphones are soldered in a fixed 66 mm square, and no Reachy Mini image could be reached
by anything this project does.

### 11.7 What could explain issue 32 without a board difference — and the one question that discriminates

Issue #32's boot claim deserves better than dismissal, because §11.1 shows the reporter never claimed
it was version-independent. Three explanations exist that require no V1.0/V1.1 difference at all.

**(a) A boot-loader API mismatch between the Factory and Upgrade images.** The strongest, and it comes
from [the upgrade-path reference §5.2](xvf3800-upgrade-path.md): XMOS's `xflash` documentation states
*"The `--factory-version` must match the tools version used to build the factory image."* The factory
image is written at manufacture, never touched by DFU, and its boot-loader API version is invisible to
every host — no `dfu-util` option, no `xvf_host` command, nothing in the USB descriptors. A mismatch
produces an image the loader deems invalid. **This is a mechanism by which one specific unit refuses
one specific firmware while every other unit accepts it, which is exactly issue #32's shape.** It is
**speculation, labelled as such** — no instance has ever been reported for this product. And note what
it is *not*: a property of the manufacturing batch and the toolchain Seeed built with, **not of the
silkscreen revision**. If it is the answer, then "V1.1" is a coincidence in that report.

**(b) A corrupt download.** Seeed's own wiki carries this warning, verbatim: *"Do **NOT** use "save
as" to download the firmware files from GitHub as they will get corrupt. Clone the repository or use
"Download as ZIP" to download the whole repository (and all included files) as ZIP file."* Added to
the wiki 2026-04-20 under *"Clarify firmware download instructions for GitHub"* — so it is a known,
vendor-acknowledged failure mode. It is weakened here by the reporter's own logs showing a
933,888-byte write, but not excluded: a right-sized file can still be wrong.

**(c) The corrupt data partition reaching further into the newer firmware's startup path.** v2.0.10
reworked USB descriptor, buffer and software-PLL restoration, and v2.1.0 adds two more fields read
from flash at startup (§11.3). A firmware that touches more saved state before lighting the LED ring
would fail earlier and darker on a board whose saved state is garbage — and issue #32's board is, by
its own reporter, a board whose data partition cannot even be read. **Inferred, mechanism only.**

**All three of (a), (b) and (c) predict something the report does not describe, and that is the
discriminator.** Under (a) and (b) the boot loader deems the upgrade image invalid and **boots the
Factory image** — and the Factory image on that very board *"enumerates as DFU first try, every
time"*. So the test is one command, costs nothing, and nobody has run it:

> **With v2.1.0 written and the board powered normally — Mute not held — does `dfu-util -l` find
> `2886:001a`?**

If **yes**, the image was rejected as invalid, the board fell back to Factory, and v2.1.0 is
exonerated for every other board including both of this project's. If **no**, it is not a validity
failure and (c) or something unknown is in play.

**Stated honestly, this is not airtight.** The reporter's table column reads "USB enumerates", which
he may be using for the audio device rather than for any USB device; and "LEDs stay dark" may describe
the 12-LED ring while the Factory image's blinking red mute LED went unmentioned. That ambiguity is
precisely why the question is worth asking rather than assumed away.

### 11.8 Corrections this section makes to the text above it

1. **§7.3's `DFU_GETVERSION` hypothesis is unevidenced in both directions, and should be labelled that
   way rather than repeated as a reading.** §7.3 offers it as reporting the Factory image's version,
   written at manufacture and untouched by an application upgrade, and therefore as the best candidate
   for a field that could differ by board revision. That reading has now been searched for and has no
   documentary support: the command's *entire* documentation is one help string,
   *"DFU Servicer-specific version command. Returns device version."*; the string "DFU" appears **zero
   times** in the XVF3800 User Guide's Control Commands appendix; and the command is absent from
   `xmos/lib_dfu`'s `dfu_cmd_request` enum, making index 88 an `sw_xvf3800` extension whose source is
   still 404. It is neither supported nor refuted. **Keep the recommendation to read it** — it is one
   free command and two readings settle it — but drop the Factory-image gloss. Full evidence is in
   [the upgrade-path reference §5.1](xvf3800-upgrade-path.md).
2. **§6(a)'s citation of issue #32 should carry the scoping from §11.1.** *"The reporter's own
   conclusion is that the failure is independent of firmware version"* is true of the **enumeration**
   failure and not of the **boot** failure, which the reporter separated in the next sentence and then
   asked about. The conclusion §6(a) draws — that the issue will not carry the weight of "2.1.0 fails
   on V1.1" — survives intact for the reasons it already gives, and §11.7 now supplies three
   mechanisms that do not need a board difference.

Nothing else above is contradicted. §1, §2, §3, §4, §5, §8, §9 and §10 stand as written, and §11.6
independently corroborates §1 and §2.

### 11.9 One measurement that bears on the flash but not on the revision

**The pinned `libcommand_map.so` does not know v2.1.0's new commands — and has not known the last four
releases' commands either.** Measured by extracting uppercase identifier-shaped strings from the
pinned binary (`c1b424313e48cfe97c5cfce0530ac05fe47f818cc0fba15a9954198ef105282c`, 151,680 bytes,
still unchanged at upstream head), 259 tokens, with a positive control:

| Token | In the pinned command map? | Added in |
| --- | --- | --- |
| `SAVE_CONFIGURATION`, `CLEAR_CONFIGURATION`, `USB_BIT_DEPTH`, `AEC_MIC_ARRAY_TYPE`, `DFU_GETVERSION` | **present** | pre-2.0.6 |
| `DOA_VALUE` | absent | USB v2.0.6 |
| `LED_RING_COLOR` | absent | USB v2.0.7 |
| `AIC3104_HP_LEVEL`, `AIC3104_LINEOUT_LEVEL` | absent | USB v2.1.0 |
| `AUDIO_MGR_OP_CH3`…`CH6` | absent | USB v2.1.0 |

So the compiled control map has been stale since before v2.0.6, and **v2.1.0 does not make it newly
stale** — it extends a gap [SESSION-STATE.md](../SESSION-STATE.md) already records. The Python table
`python_control/xvf_host.py` *is* current for the two AIC3104 commands (added at commit
`5bb73fec64b8`) but has **no** `AUDIO_MGR_OP_CH3`…`CH6` entries, so four of the commands v2.1.0's
changelog advertises are unreachable from any published host tool, in either implementation.

**And a precedent worth carrying: Seeed does move control-surface resource IDs between firmware
versions.** The I2S changelog for v1.0.8, published in the same week as USB v2.1.0, states: *"Swapped
the resource IDs of `DOA_VALUE` and `LED_RING_COLOR` to match the USB firmware interface:
`DOA_VALUE`: 19 → 18; `LED_RING_COLOR`: 18 → 19."* A third party caught the consequence immediately —
[formatBCE issue #48](https://github.com/formatBCE/Respeaker-XVF3800-ESPHome-integration/issues/48)
notes that a driver hard-coding the old mapping would silently write LED commands into a now-read-only
register. **The USB v2.1.0 changelog declares no such change**, and the changelog is the only source
that exists — which is an argument for reading `BLD_MSG` and a control command or two after any flash,
not an argument against flashing.

### Where this section lands

| Concern | Where it lives |
| --- | --- |
| Whether an intermediate version is needed, and what the Factory partition constrains | [xvf3800-upgrade-path.md](xvf3800-upgrade-path.md) |
| The pinned v2.1.0 image, its digest and the flash interlocks | `src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs` |
| The `DFU_GETVERSION` gloss this section retires | §7.3 above, and [xvf3800-upgrade-path.md §5.1](xvf3800-upgrade-path.md) |
| The issue-32 scoping this section corrects | §6(a) above, and [SESSION-STATE.md](../SESSION-STATE.md) §7 |
| The stale control map and the unreachable v2.1.0 commands | [SESSION-STATE.md](../SESSION-STATE.md), and `XvfHostRelease.cs` |
