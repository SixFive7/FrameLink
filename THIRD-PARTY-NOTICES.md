# Third-party notices

Not everything in this repository is ours. A few files were written by other people and are stored here as-is so the project builds and the guides render without reaching out to a CDN. Those files keep the licences their authors gave them — the [FrameLink License](LICENSE) does not apply to them and cannot, because they were never ours to relicense.

This file lists every such file, what it is, where it came from, and under which licence it is included. If you fork or redistribute FrameLink, these notices travel with it.

Versions below were verified against the files themselves and against the upstream package registry on **2026-08-15**. Where a version is embedded in the vendored file, the notice says where to find it, so a future reader can re-check rather than trust this page.

One entry is not about a file in this repository at all. FrameLink's Fleet Manager can *generate* a modified Raspberry Pi OS image, and that is a redistribution performed by whoever runs the Fleet Manager. It has [its own section](#raspberry-pi-os-and-images-generated-from-it) because getting that right matters even though no byte of it is stored here.

## Vendored JavaScript libraries

Both files live in `app/vendor/` and are pre-built, minified distributions taken from the publishers' own CDN releases. Neither has been modified.

### lit-all.min.js — Lit

| | |
|---|---|
| **Path** | `app/vendor/lit-all.min.js` |
| **Project** | [Lit](https://lit.dev/) ([lit/lit on GitHub](https://github.com/lit/lit)) |
| **Version** | The prebuilt `lit-all` bundle. Component versions are embedded in the file: `@lit/reactive-element` 2.1.2, `lit-html` 3.3.3, `lit-element` 4.2.2 — the set shipped by the `lit` 3.3.3 release. Search the file for `reactiveElementVersions`, `litHtmlVersions` and `litElementVersions` to read them back. |
| **Licence** | BSD-3-Clause |
| **Copyright** | Copyright 2017–2022 Google LLC |
| **Used by** | `app/frame-app.js`, `app/frame-grid.js`, `app/frame-setup.js`, `app/frame-tile.js` — the web components that make up the kiosk SPA. |

The bundle carries its own licence headers: 21 `SPDX-License-Identifier: BSD-3-Clause` markers and the accompanying Google LLC copyright lines are preserved verbatim inside the minified file. Do not strip them when copying the file around — the BSD-3-Clause retention clause is exactly what they satisfy.

### livekit-client.umd.js — LiveKit Client SDK for JavaScript

| | |
|---|---|
| **Path** | `app/vendor/livekit-client.umd.js` |
| **Project** | [LiveKit Client SDK for JavaScript](https://github.com/livekit/client-sdk-js) |
| **Version** | 2.19.2 — the version constant is embedded in the bundle (search for `"2.19.2"`). |
| **Licence** | Apache-2.0 |
| **Copyright** | Copyright LiveKit, Inc. |
| **Used by** | `app/index.html` (loaded as a plain script) and `app/livekit.js` — the WebRTC client that joins and leaves the call room. |
| **Obtained from** | The npm CDN, by the command recorded in [guide 8](docs/8-webrtc-validation.md) and reproduced below. |

```bash
curl -Lo livekit-client.umd.js "https://unpkg.com/livekit-client@2/dist/livekit-client.umd.js"
```

This is a UMD bundle, which means the SDK's own dependencies are compiled *into* the file rather than fetched separately. Those dependencies are therefore redistributed here too, under their own licences. The direct dependency set declared by `livekit-client` 2.19.2 is:

| Package | Licence |
|---|---|
| [@livekit/mutex](https://www.npmjs.com/package/@livekit/mutex) | Apache-2.0 |
| [@livekit/protocol](https://www.npmjs.com/package/@livekit/protocol) | Apache-2.0 |
| [events](https://www.npmjs.com/package/events) | MIT |
| [jose](https://www.npmjs.com/package/jose) | MIT |
| [loglevel](https://www.npmjs.com/package/loglevel) | MIT |
| [sdp-transform](https://www.npmjs.com/package/sdp-transform) | MIT |
| [tslib](https://www.npmjs.com/package/tslib) | 0BSD |
| [typed-emitter](https://www.npmjs.com/package/typed-emitter) | MIT |
| [webrtc-adapter](https://www.npmjs.com/package/webrtc-adapter) | BSD-3-Clause |

`@livekit/protocol` in turn carries [@bufbuild/protobuf](https://www.npmjs.com/package/@bufbuild/protobuf) (`Apache-2.0 AND BSD-3-Clause`), whose runtime is identifiable inside the bundle by its `@bufbuild/protobuf/enum-type` symbol. All of the above are permissive licences; none imposes a copyleft obligation on FrameLink.

## Reference images in the build guides

The build guides embed images stored under `docs/<guide-stem>/`. They are a mix of the project's own build photographs and third-party material reproduced for identification and instruction:

- **Manufacturer documentation and product imagery** in `docs/1-hardware-build-guide/` — for example `display-dsi-touch-a.jpg`, which is Waveshare's own wiring diagram for the [10.1-DSI-TOUCH-A display](https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A). These remain the property of their respective manufacturers (Waveshare, Raspberry Pi Ltd, Seeed Studio, Adafruit) and are not relicensed here.
- **Raspberry Pi Imager screenshots** in `docs/2-sd-flash-first-boot/` — screen captures of [Raspberry Pi Imager](https://www.raspberrypi.com/software/) v2.0.7 walking through device, OS, storage and customisation selection. The Imager interface, the Raspberry Pi logo, and the OS names shown in it belong to Raspberry Pi Ltd.

Neither category is covered by the [FrameLink License](LICENSE). If you fork the project and republish the guides, satisfy yourself that your use of these images is permissible in your jurisdiction, or replace them with your own.

## Software FrameLink uses but does not redistribute

The following are fetched from their own publishers at install or run time. No copy of them lives in this repository, so no notice obligation attaches to this project — they are listed only so it is clear what a running FrameLink unit is made of, and under what terms.

| Component | Licence | How it reaches the device |
|---|---|---|
| [Immich Kiosk](https://github.com/damongolding/immich-kiosk) | AGPL-3.0 | Pulled as the published container image pinned in `deploy/immich-kiosk/compose.yaml` (v2 will fetch the pinned upstream binary release and verify its checksum instead). Fetching from upstream rather than redistributing is deliberate: it keeps AGPL source-offer obligations with the publisher, off this project and off every self-hoster. |
| [LiveKit server](https://github.com/livekit/livekit) | Apache-2.0 | Pulled as the pinned `livekit/livekit-server` container image by [guide 7](docs/7-livekit-server.md). |
| labwc, Chromium, PipeWire, libcamera, Docker | Various — as distributed by Debian and Raspberry Pi Ltd | Installed from the distribution's own package repositories by the build guides. |
| Raspberry Pi OS Lite (Trixie / Debian 13) | Various — see the section below | The operating system every frame runs, installed from Raspberry Pi Ltd's own download server. **The FrameLink Fleet Manager can also generate a modified copy of it**, which is a redistribution and has its own section below. |

## Raspberry Pi OS, and images generated from it

FrameLink v2's Fleet Manager can take a stock Raspberry Pi OS image and write four files into it — the `fl-agent` binary, its systemd unit, the symlink that enables that unit, and a `framelink.conf` naming the Fleet Manager — producing a ready-to-flash image (see §3.9 of the build specification). This section exists because that is a **redistribution of Raspberry Pi OS**, and it is worth being precise about who performs it and on what terms.

| | |
|---|---|
| **Project** | [Raspberry Pi OS](https://www.raspberrypi.com/software/operating-systems/) Lite, 64-bit (Trixie / Debian 13), by Raspberry Pi Ltd |
| **Pinned release** | `2026-06-18-raspios-trixie-arm64-lite.img.xz`, published 2026-06-19 |
| **Obtained from** | [downloads.raspberrypi.com](https://downloads.raspberrypi.com/raspios_lite_arm64/images/raspios_lite_arm64-2026-06-19/) |
| **SHA-256 (published archive)** | `acff736ca7945e3b305f07cda4abdb870910e12634991da69783611756e381b3` — the value Raspberry Pi Ltd publishes in the `.sha256` sidecar beside the download |
| **SHA-256 (decompressed image)** | `e235fd24fc5f039c08daba7d3abc04aecc7313f979d16d2a3fdad29dd44c33a9`, 2,977,955,840 bytes — measured here, because no vendor publishes it, and it is what the generator verifies before touching the file |
| **Licence** | Not a single licence. Raspberry Pi OS is a Debian derivative: the great majority of it is GPL-2.0, GPL-3.0, LGPL, MIT, BSD and Apache-2.0, alongside a set of non-free Broadcom firmware and Raspberry Pi Ltd's own terms in `raspberrypi-sys-mods`. Every package carries its own licence text in `/usr/share/doc/<package>/copyright` inside the image itself. |
| **Redistribution** | Permitted. Raspberry Pi Ltd distributes these images for exactly this purpose, and the copyleft source obligations for the Debian-derived majority are discharged by Debian's and Raspberry Pi Ltd's own source archives, which remain the authoritative source for anything in a generated image. |

Three things follow, and all three matter more than the table:

- **No copy of Raspberry Pi OS lives in this repository, and none ever will.** The image is fetched by the operator, verified against the digests above, and used as an input. The [FrameLink License](LICENSE) applies to the four files FrameLink adds and to nothing else in a generated image.
- **The redistributor is each self-hoster, not this project.** FrameLink is self-hosted by design (§1.2 principle 1), so the program that produces a modified image runs on the operator's own server. If you hand a generated image to somebody else, you are the one distributing Raspberry Pi OS to them, on Raspberry Pi Ltd's terms and Debian's — not on FrameLink's.
- **The pin is the point.** The release, the URL and both digests are recorded in the source that uses them, so a generated image can always be traced back to an exact upstream artifact. An image built from "whatever the mirror served that day" could not be.

The `fl-agent` binary written into a generated image is FrameLink's own work and carries the [FrameLink License](LICENSE). It links only `libc` and `libm` from the base image.

## Corrections

If you believe something is listed incorrectly, or that a file in this repository is missing from this page, please say so: [jori@voipfabric.com](mailto:jori@voipfabric.com). Getting attribution right matters more than being right the first time.
