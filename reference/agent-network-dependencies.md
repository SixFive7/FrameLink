# FrameLink v2 — what the agent fetches from the network, and what could travel inside it instead

This file answers one question the operator asked while deciding that the microphone-array firmware
must live inside the agent binary: **is there anything else an agent has to download, ad hoc, at any
possible state — and can that be embedded too?** It then answers the question underneath it, which
was not asked: **what is the minimum set that would let a frame reach a working, useful state with no
internet at all?** A frame that can flash firmware offline but cannot render a photograph offline has
not gained much.

The short version, before the evidence:

- **Six things are fetched over the network at frame runtime**, and only four of them are artifacts
  the agent itself downloads: the agent's own next binary, Immich Kiosk, the `xvf_host` control set,
  and the three XVF3800 DFU images. The fifth is the apt package block. The sixth is the
  photographs themselves.
- **The apt block is the wall, not GitHub.** The fifteen `pkg.*` resources are positions 6–22 of 81,
  ahead of everything that draws a picture, and they are the *only* fetch that cannot be embedded:
  measured today, 121.7 MB of direct `.deb` and a 332.1 MB dependency closure against a 10.7 MB
  binary. `chromium` alone is 119.3 MB compressed and 297.5 MB installed.
- **A frame with no internet today dies at position 6 of 81** — `pkg.labwc` — after three attempts
  and three reboots, and stops the whole pass ([§2.5](../version2.md) rung 4). It lights its panel,
  narrates the escalation on `/dev/tty8`, and never reaches a browser, a slideshow or a camera.
- **Embedding costs exactly the raw byte length.** Measured: the 538,455-byte vendored
  `livekit-client.umd.js` appears byte-for-byte contiguous inside the 10,662,576-byte AOT binary.
  Native AOT stores embedded resources uncompressed, so a blob costs what it weighs — unless the
  agent compresses it itself, which changes the arithmetic dramatically for one candidate.
- **The recovery firmware image is 4,194,304 bytes of `0xFF` that Brotli takes to 14 bytes.** All
  three DFU images together are 6,062,080 raw and 466,818 Brotli'd. Embedding the full firmware set
  compressed costs +4.4% of the binary; embedding it raw costs +56.9%.
- **Two of the four artifacts cannot be embedded for legal reasons rather than technical ones**, and
  both reasons are already recorded in this repository: `xvf_host` (no licence upstream at all,
  plus XMOS's standalone-redistribution ban) and Immich Kiosk (AGPL-3.0, where
  [§2.1](../version2.md) chose fetching deliberately to keep the source-offer obligation off every
  self-hoster). Neither is an engineering question.
- **The largest single win is not in the binary.** §2.1 already names a Fleet-Manager mirror as "a
  later operator setting". One LAN-side mirror answers all four artifact fetches *and* apt, costs
  zero binary bytes, and is the only option that reaches the photographs as well.

This is reference material, not a build guide: the seven-block step structure of
[CLAUDE.md §2.1](../CLAUDE.md) does not apply here. The link and honesty rules do.

---

## Provenance and coverage

**Everything labelled *measured* was measured on 2026-08-24 from this workstation**, by fetching the
artifact and hashing it, or by reading the byte length of a file in this repository. Everything
labelled *read* was read out of the source or the specification in this working tree and is a fact
about the code as it stands, not about a running frame. Everything labelled *inferred* is reasoning
from the two, and is marked so it can be argued with.

**No frame was contacted, no card was flashed, and nothing was deployed.** The `.work/fw-embed` build
another workstream was running while this file was written was inspected read-only and is discussed
in §5, because its output is easy to misread.

**The work set was enumerated before the search, and the counts are checkable:**

| Set | Size | How the count was established |
| --- | ---: | --- |
| Resources in the agent's graph | **81** | `AgentResourceGraphTests.cs:213` asserts `graph.Count == 81`; independently reconstructed here as 66 `ResourceName` constants (excluding `UnitInstaller.ResourceName`, which names an embedded file rather than a resource) plus 15 `AptPackageSpec` entries |
| Resources in [the resource catalog](resource-catalog.md) | **80** | `AgentResourceGraphTests.cs:325` and `ParityHarnessTests.cs:193` both assert 80 |
| `IResource` implementations examined | **56 classes** | every `: IResource` declaration under `src/FrameLink.Agent/`, enumerated and read for a fetch path; 56 classes produce 81 instances because `PackageResource`, `KioskConfigResource`, `AppConfigResource`, `MixerVolumeResource` and `MixerSwitchResource` are each built once per spec |
| Resources that reach the network for an **artifact** | **19 of 81** | `agent.version`, `kiosk.binary.pinned-release`, `tool.xvf-host.installed`, `firmware.xvf3800.image`, and the 15 `pkg.*` |
| Resources that are the **adoption gate or gated on it**, and so need the Fleet Manager for a value while fetching no artifact | **13** | eleven declare a direct edge on `agent.adoption` — `agent.device-name`, `app.config.identity`, `app.config.room`, `app.config.livekit-url`, `identity.hostname`, `kiosk.config.immich-url`, `kiosk.config.immich-api-key`, `kiosk.config.albums`, `system.timezone`, `system.locale`, `boot.cmdline.wifi-regdom` — plus `app.config.livekit-token` transitively and `agent.adoption` itself. `app.config.immich-kiosk-url` is deliberately **not** among them: its base URL is fixed and `slideshow.interval` has a catalog default |
| Remaining resources, verified to touch no network | **49** | the balance; each was read for `HttpClient`, for process invocations reaching an archive, and for a download seam |
| Distinct HTTP/socket call sites in the agent | **6 files** | `MdnsEndpointSource`, `WebSocketControlTransport`, `HttpReleaseSource`, `HttpKioskDownload`, `HttpXvfHostDownload` (which also serves the firmware installer), `LocalOrigin` |
| Entries in the upstream review ledger | **10 of 10 read** | `upstream-review.json` — 6 runtime artifacts, 3 build-time NuGet/.NET entries, 1 base image |

The fifth and sixth rows overlap: `boot.cmdline.wifi-regdom` and the two `locale.*` resources declare
an adoption edge, and `kiosk.config.immich-url` / `kiosk.config.immich-api-key` need issued values.
They are counted separately because they are a *configuration* dependency on the Fleet Manager, not
an artifact dependency, and the Fleet Manager is expected to be on the LAN.

**What was deliberately not covered**, so the boundary is stated rather than implied:

- **Build-time dependencies of the two programs** — the .NET 10 SDK, NuGet (`Microsoft.Data.Sqlite`,
  `xunit.v3`), the `mcr.microsoft.com/dotnet/sdk:10.0` base image, and the Fleet Manager GUI's npm
  tree (Svelte, Vite, and the two `@fontsource-variable` packages). None of these is fetched by a
  frame or by a running Fleet Manager; they are named in §2 for completeness and not inventoried.
- **The harness in `tools/harness`**, which runs on the workstation and never on a frame.
- **`deploy/`**, which holds v1 artifacts the catalog records as superseded.
- **The transitive apt closure's overlap with Raspberry Pi OS Lite.** The closure was computed over
  the published indices; how much of it is already on the pinned base image was not measured,
  because doing so needs the 2.98 GB image mounted. The consequence is stated in §1.5 as a bracket
  rather than a number.

---

## 1. Everything the agent fetches at runtime

One row per artifact. **When** uses [§2.2](../version2.md)'s vocabulary: the reconciliation loop is
level-triggered, so "on drift" means *whenever an Observe finds the artifact absent or hashing to
something other than the pin*, which on a bare frame is the first pass and on a converged frame is
never.

### 1.1 `agent.version` — the agent's own next binary

| Field | Value |
| --- | --- |
| **What** | The `fl-agent` Native AOT `linux-arm64` executable the Fleet Manager is serving |
| **Source** | `GET {control-url}/agent/release/linux-arm64` for the metadata, then the `Url` that metadata names (`/agent/binary/{rid}`) — **read**, `ControlRoutes.ReleaseFor`, `WebSocketControlTransport.cs:160` |
| **When** | Hourly, out of band, on its own timer; the handshake only brings the tick forward. **Matches, never compares** — a downgrade is an ordinary convergence — **read**, `UpdateService.cs` |
| **Pinned how** | Not pinned in source at all, and correctly so: the server issues `Version`, `Sha256` and `SizeBytes` per request, and the download is length-bounded and digest-checked before the atomic rename — **read**, `AgentRelease.cs`, `BinarySwap.cs` |
| **Size** | 10,662,576 bytes — **measured**, `build/out/fl-agent`, built 2026-08-23 23:16 |
| **If the fetch fails** | `UpdateOutcome.Unreachable`, logged, retried at the next tick. `agent.version` returns `ResourceObservation.Unevaluable` rather than failing, so **no attempt is spent and nothing is blocked** — no resource declares an edge on it, deliberately, because an edge would mark all 80 others `Blocked` on a frame whose server is unreachable — **read**, `AgentRootResources.cs:98`, `DeviceCatalog.cs` remarks |
| **Embeddable?** | **No, by definition.** It is the thing that changes. A binary containing its own successor is a fixed point, not an update mechanism |
| **Cost of embedding** | Not applicable |

### 1.2 `kiosk.binary.pinned-release` — Immich Kiosk

| Field | Value |
| --- | --- |
| **What** | `immich-kiosk` v0.42.0, a static Go `linux-arm64` executable, AGPL-3.0, supervised by the agent as a child process |
| **Source** | `https://github.com/damongolding/immich-kiosk/releases/download/v0.42.0/immich-kiosk_Linux_arm64.tar.gz`, with `immich-kiosk_0.42.0_checksums.txt` beside it for human review — **read**, `KioskRelease.cs:64` |
| **When** | On drift: whenever the file at `/var/lib/fl-agent/immich-kiosk/immich-kiosk` is absent, hashes to something else, or is not executable. In practice: once, on the first pass |
| **Pinned how** | Four locks — version, published archive SHA-256, exact archive length (which bounds the download), and the measured SHA-256 of the executable *inside* the archive, which is what the resource observes on every later pass |
| **Size** | Archive **7,712,323** bytes, `sha256 93476535…7e423`; executable **18,546,850** bytes, `sha256 162043f2…ec81c`. Both **measured** 2026-08-24 and both digests match the pin exactly. Archive members: `LICENSE` (34,523), `README.md` (3,021), `immich-kiosk` (18,546,850, mode 0755) |
| **If the fetch fails** | `KioskInstallResult.Unreachable` → the Act reports `(refused: Unreachable)` → post-reboot Verify fails → three attempts, three reboots → `Escalated`, which **stops the whole pass**. `kiosk.offline-cache.dir`, all five `kiosk.config.*`, `kiosk.listen-address` and `kiosk.process.supervised` go `Blocked`. The frame renders the repair screen and shows no photographs, ever — **read**, `KioskResources.cs:84`, §2.5 rung 4 |
| **Embeddable?** | **Technically yes, trivially. Deliberately no.** §2.1: "Fetching from upstream rather than redistributing keeps AGPL source-offer obligations off this project and off every self-hoster." Embedding it makes every operator who ships a binary an AGPL distributor. This is a policy reversal, not an engineering change |
| **Cost of embedding** | Archive as published: **+7,712,323** → +72.3%, binary becomes 18,374,899. Extracted binary raw: **+18,546,850** → +173.9%. Extracted binary Brotli q11: **+5,938,933** → +55.7% (**measured**) — Brotli beats upstream's own gzip by 1.77 MB, so the cheapest embed is *recompress, do not carry the tarball* |

### 1.3 `tool.xvf-host.installed` — the `xvf_host` control set

| Field | Value |
| --- | --- |
| **What** | Six files: the `xvf_host` aarch64 executable, `libcommand_map.so`, `libdevice_i2c.so`, `libdevice_usb.so`, `dfu_cmds.yaml`, `transport_config.yaml`. §2.1 names this "the sharpest exception" to the one-binary rule |
| **Source** | `https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/725f38464e73477a30aba9f5c220f1cfdc66d682/host_control/rpi_64bit/<file>`, one request per file, each carrying `Cache-Control: no-cache` — **read**, `XvfHostRelease.cs:124`, `:219` |
| **When** | On drift. Observe also runs `xvf_host VERSION` and requires the array to answer, so a frame with the files but a dead array is `Degraded` rather than `InSync` |
| **Pinned how** | Full commit SHA in the URL, which is content-addressed, **plus** a measured SHA-256 and exact length per file. Upstream has zero releases and zero tags, so there is no version to pin instead |
| **Size** | `xvf_host` 1,772,904 · `libcommand_map.so` 151,680 · `libdevice_i2c.so` 72,568 · `libdevice_usb.so` 73,312 · `dfu_cmds.yaml` 2,507 · `transport_config.yaml` 30 = **2,073,001** bytes. **Measured** 2026-08-24; all six digests match the pin |
| **If the fetch fails** | Same escalation shape as Immich Kiosk. `audio.xvf3800.gpo-x0d31-amp-enable` — the resource that switches the speaker amplifier on — declares this resource and nothing else, so it goes `Blocked`; and the pass stops regardless. A frame in this state has no sound at all |
| **Embeddable?** | **No — licence, and it is already argued in source.** The upstream repository carries no licence file (0 of 19 blobs at the pinned commit, 0 of 51 at head), so default copyright applies and nothing grants redistribution. The tool appears to be built from XMOS's `host_xvf_control`, whose XCORE VOCALFUSION LICENCE forbids making the software available to a third party "on a standalone basis" while expressly permitting shipping it *installed on the devices*. Fetching onto a frame sits inside that permission; committing the bytes here does not — **read**, `XvfHostRelease.cs`, decision 63 |
| **Cost of embedding** | If it ever became legal: **+2,073,001** raw → +19.4%; **+657,801** gzip -9 → +6.2%; **+475,219** Brotli q11 → +4.5% (**measured**). The real retirement path is the one `TODO.md` records — implementing the XVF3800 wire protocol natively, which deletes all six files rather than moving them |

### 1.4 `firmware.xvf3800.image` — the three DFU images

| Field | Value |
| --- | --- |
| **What** | `respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin` (target), `…_v2.0.6.bin` (known-good fallback), `4mb_all_ff.bin` (the blank image that erases a half-written Upgrade partition) |
| **Source** | `https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/<per-file commit>/xmos_firmwares/{usb,recover}/<file>` — three different commit SHAs, one per file — **read**, `XvfFirmwareRelease.cs:186` |
| **When** | On drift, unattended, on every frame in the fleet whether or not anybody will ever flash it. Guarded on `/proc/asound/cards` existing, so a machine with no sound hardware reports `InSync` and holds nothing |
| **Pinned how** | Per-file commit SHA in the URL plus measured SHA-256 plus exact length. The version string is explicitly *not* the identity: `…v2.0.10.bin` has been published twice under one name with different bytes (402,246 of 933,888 bytes differing), both answering `VERSION 2 0 10` |
| **Size** | target **933,888** · fallback **933,888** · recovery **4,194,304** = **6,062,080** bytes. **Measured** against this repository's own copies at `.work/vendor-firmware/`; all three digests match the pin |
| **If the fetch fails** | Same escalation shape, and one extra consequence: `ArrayFirmwareFlash` refuses to start without a digest-verified target *and* a digest-verified way back, so an unreachable GitHub silently disarms the recovery route as well as the flash |
| **Embeddable?** | **Yes — and it is half-done in the working tree right now.** `vendor/respeaker-xvf3800/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin` is tracked, `NOTICE.md` records its provenance, `XvfVendoredFirmware.cs` is the accessor, and an uncommitted `<EmbeddedResource Include="..\..\vendor\respeaker-xvf3800\**\*.bin">` glob compiles it in. The fallback and the erase image are pinned but not yet vendored; the csproj comment calls that "an open question rather than an oversight" |
| **Cost of embedding** | Target alone: **+933,888** → +8.8%. All three raw: **+6,062,080** → +56.9%. All three gzip -9: **+603,224** → +5.7%. All three Brotli q11: **+466,818** → +4.4% (**measured**). The recovery image is the reason the spread is so wide: 4,194,304 bytes of `0xFF` compresses to 4,098 with gzip and **14 bytes** with Brotli |

### 1.5 The fifteen `pkg.*` resources — the apt block

| Field | Value |
| --- | --- |
| **What** | Fourteen packages that must be present and one (`libspa-0.2-libcamera`) that must be absent: `labwc`, `chromium`, `wireplumber`, `pipewire-alsa`, `wlr-randr`, `xdg-desktop-portal`, `xdg-desktop-portal-gtk`, `gstreamer1.0-tools`, `gstreamer1.0-plugins-base`, `gstreamer1.0-libcamera`, `gstreamer1.0-pipewire`, `dfu-util`, `grim`, `unattended-upgrades` |
| **Source** | `env DEBIAN_FRONTEND=noninteractive apt-get update` followed by `apt-get install -y <pkg>`, against whatever the frame's sources list names — in practice `deb.debian.org/debian` and `archive.raspberrypi.com/debian`. There is no apt-index resource; the refresh lives inside `AptPackages.InstallAsync` — **read**, `AptPackages.cs:388` |
| **When** | Positions **6–22 of 81**, immediately after the agent roots and adoption, and *ahead of every resource that draws a picture*. Then continuously afterwards, because the agent installs and enables `unattended-upgrades` |
| **Pinned how** | **Not at all, by design.** `AptPackageSpec.ReviewedVersion` is a recorded floor transcribed from [the v1 state inventory](v1-state-inventory.txt), not a constraint the resource enforces, and `upstream-review.json` excludes Debian versions from the ledger outright: "an entry per Debian package would turn a release gate into a daily chore" |
| **Size** | Direct `.deb` bytes for the fourteen: **121,685,158**. Full dependency closure over the two published `Packages` indices: **394 packages, 332,128,712 bytes**. `chromium` alone is **119,286,222** `.deb` / **297,490,432** installed. **Measured** 2026-08-24 from the Debian and Raspberry Pi Trixie `main/binary-arm64` indices, both cached at `.work/net-deps/apt/`. The true incremental download sits between those two numbers, because Raspberry Pi OS Lite already carries part of the closure — **inferred**, not measured |
| **If the fetch fails** | The first failing package escalates after three attempts and three reboots and **stops the pass at position 6**. Nothing after it is acted on: no compositor, no browser, no camera stack, no slideshow, no audio. The frame lights its panel (positions 2–3, no network) and narrates one escalated package on `/dev/tty8` indefinitely. `AptPackages` does distinguish an unreachable archive from a genuinely missing package and says so in the delta, which is what a person reads on the screen — **read**, `PackageResources.cs:200`, `AptPackages.cs:397` |
| **Embeddable?** | **No.** Three independent reasons, any one of which is sufficient: the volume (122–332 MB against a 10.7 MB binary); the fact that the agent does not own dpkg's database, triggers or maintainer scripts, so shipping `.deb` payloads would mean reimplementing dpkg; and the deliberate policy that these packages *float* forward through security updates |
| **Cost of embedding** | Not a candidate. The offline answer for this row is a mirror or a fat image, not the binary — see §7 |
| **Drift note** | The archives have already moved past the reviewed floors: today `labwc` is `0.9.8-1+rpt1` against a reviewed `0.9.2-1+rpt4`, `gstreamer1.0-libcamera` is `0.7.2+rpt20260817-1` against `0.7.0+rpt20260205-1`, and `chromium` is `1:151.0.7922.137-1~deb13u1+rpt1`. **Measured** 2026-08-24. This is the design working, not a fault |

### 1.6 The photographs

| Field | Value |
| --- | --- |
| **What** | The operator's own photo library, fetched by the Immich Kiosk child from an Immich server |
| **Source** | `KIOSK_IMMICH_URL` with `KIOSK_IMMICH_API_KEY`, scoped by `KIOSK_ALBUMS`. Both the URL and the key are Fleet-Manager-issued and gate on `agent.adoption` — there is no catalog default and none is possible |
| **When** | Continuously, while the slideshow runs. Plus an offline cache of `KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS` assets, default **200**, written to `./offline-assets` beside the child's working directory |
| **Pinned how** | Not pinned, and cannot be — the library is the product |
| **Size** | Unbounded. The cache is 200 assets by default; no per-asset size is asserted anywhere in this build |
| **If the fetch fails** | With `KIOSK_OFFLINE_MODE_ENABLED=true` **and** `use_offline_mode=true` in the browser's kiosk URL **and** a warm cache, the slideshow keeps running from disk — this is exactly the [§2.6](../version2.md) rule that "an outage in the *operator's* house must never blank a frame in someone else's". With a **cold** cache, there is nothing to show. `kiosk.offline-cache.dir` asserts only that the directory exists and takes a test write; **nothing in the graph asserts that the cache has ever filled** |
| **Embeddable?** | **No, by definition.** Their purpose is to change |
| **Cost of embedding** | Not applicable |

---

## 2. What a frame depends on that somebody else fetches

These are not agent downloads, but a frame is not useful without them, so they belong in the same
inventory.

| Artifact | Who fetches it | Source | When | Pinned | Size | If it fails | Embeddable |
| --- | --- | --- | --- | --- | ---: | --- | --- |
| **`livekit-server` 1.13.5** | The Fleet Manager container (`fl-control`), not the frame | `github.com/livekit/livekit/releases/download/v1.13.5/livekit_1.13.5_linux_{amd64,arm64}.tar.gz` plus `checksums.txt` | Container start | Version + archive SHA-256 + length + inner binary SHA-256 + length, both architectures | arm64 archive 16,349,520 / binary 49,938,594; amd64 archive 18,064,127 / binary 53,420,194 — **read** from the pin, not re-measured | No calls anywhere in the fleet; frames still show photographs | Into the *container*, yes — Apache-2.0, no obstacle. Not into the agent: wrong machine |
| **Raspberry Pi OS Lite (Trixie) arm64, 2026-06-18** | The operator, once per pin, by hand | `downloads.raspberrypi.com/raspios_lite_arm64/images/raspios_lite_arm64-2026-06-19/2026-06-18-raspios-trixie-arm64-lite.img.xz` | Once, before the first image build; `BaseImagePin.PreparationCommand` is the exact command | Published archive SHA-256 + length, **and** the decompressed image's own SHA-256 + length, which is what the generator verifies | archive **524,875,608** (`Content-Length` confirmed by `HEAD`, **measured** 2026-08-24, matching the pin exactly); decompressed 2,977,955,840 | No images can be generated; existing frames unaffected | Into nothing — it is the substrate |
| **Build-time: .NET 10 SDK, NuGet, npm, Docker base images** | The build host | `api.nuget.org`, `mcr.microsoft.com`, the npm registry | Build | `Microsoft.Data.Sqlite` and `xunit.v3` float by [§7.1](../version2.md) and are ledger-tracked; `net10.0` is the pinned band | not inventoried | No release can be built | Out of scope |

---

## 3. Network dependencies that are not artifacts

Worth naming because "no internet" and "no network" are different states, and only one of them is
survivable today.

| Dependency | Scope | What happens without it |
| --- | --- | --- |
| **Fleet Manager control link** (`wss://…/agent`) | LAN or WAN, operator's choice | `NoContact`: a small persistent overlay, and the product **keeps running** if the frame was fully green when contact dropped. Rejection is an answer; silence is not |
| **mDNS discovery** (`MdnsEndpointSource`) | LAN multicast only | One of three discovery candidates; the boot-partition `framelink.conf` seed is the one a generated image carries |
| **LiveKit signalling and media** | LAN | No calls. `use_external_ip: false` is written into every generated config, which is what stops LiveKit reaching for **public STUN servers** — so a call that never leaves the house needs no internet — **read**, `LiveKitConfigFile.cs:103` |
| **NTP (`systemd-timesyncd`)** | Internet by default | A Pi 5 with no RTC battery boots at an arbitrary time. The agent is written against this: both halves of the page-staleness comparison are monotonic (`performance.now()` and `Environment.TickCount64`) precisely so a clock step seconds after boot cannot corrupt them |
| **`unattended-upgrades`** | Internet | The agent installs *and* enables it, so a converged frame pulls security updates continuously and indefinitely. This is the one network dependency the agent deliberately creates rather than merely has |
| **`rpi-eeprom-update.service`** | Internet, via apt | Enabled in the v1 reference. Bootloader images arrive inside the `rpi-eeprom` package, so a bootloader can change under `eeprom.config` without anyone asking — the catalog names it "an autonomous owner" |
| **The Immich server** | LAN or WAN | See §1.6 |

---

## 4. What the image generator bakes in, and what it does not

**Read** from `ImagePlan.Create` and `ImageSeed`, which are values rather than method bodies
specifically so this list is assertable.

**Baked in — four things, and that is the whole list:**

1. `framelink.conf` on the FAT boot partition, carrying `control-url` and optionally
   `control-lan-url`. **Nothing else.** `ImageSeed` has exactly two fields, both URLs, and refuses
   user-info, query strings, fragments and control characters — so "helpfully pre-seed the identity"
   cannot be done by adding a line, it needs the record widened, which is a review a person performs.
2. The `fl-agent` binary at `/usr/local/bin/fl-agent`, mode `0100755`, uid 0, gid 0.
3. `fl-agent.service` at `/etc/systemd/system/`, mode `0100644`, uid 0, gid 0 — written from the
   agent's own embedded copy of the unit.
4. The `multi-user.target.wants` symlink that *is* `systemctl enable`.

Then `e2fsck -fn` as the gate, and the artifact is not offered unless it exits 0.

**Not baked in — everything else, and two absences are deliberate:**

- Every apt package, Immich Kiosk, `xvf_host`, the DFU images, every fleet setting, the device
  keypair, the adoption token.
- **Wi-Fi credentials**, deliberately: the vendor's supported channel is `custom.toml`, which also
  governs first-boot user creation, hostname and SSH, and on Bookworm and later the WLAN interface
  stays rfkill-soft-blocked until a wireless regulatory country is set. `ImageSeed` names the shape
  that would work and says it needs a card and a boot to prove.
- **Any secret at all**, by the shape of the type rather than by anybody remembering.

**The consequence, stated plainly:** a generated image contains an agent and an address. Everything
that makes a frame *useful* arrives over the network afterwards.

---

## 5. The cost of embedding, measured

**The mechanism first, because it decides every row.** Native AOT stores embedded resources
**uncompressed and contiguous**. Proven rather than assumed: the 538,455-byte vendored
`app/vendor/livekit-client.umd.js` was located inside `build/out/fl-agent` at offset 6,479,619 and
compared byte-for-byte over its full length — an exact match (**measured** 2026-08-24). So the binary
delta for embedding a blob **is its raw byte length**, and the only way to pay less is for the agent
to carry the artifact compressed and inflate it at install time.

Baseline: **10,662,576 bytes** (`build/out/fl-agent`, built 2026-08-23 23:16, **measured**).

| Candidate | Bytes added | New binary | Growth | Note |
| --- | ---: | ---: | ---: | --- |
| Firmware, target `v2.1.0` only (raw) | 933,888 | 11,596,464 | +8.8% | What the working tree does today |
| Firmware, all three (raw) | 6,062,080 | 16,724,656 | +56.9% | |
| Firmware, all three (gzip -9) | 603,224 | 11,265,800 | +5.7% | |
| **Firmware, all three (Brotli q11)** | **466,818** | **11,129,394** | **+4.4%** | Recovery image: 4,194,304 → **14 bytes** |
| `xvf_host` set, six files (raw) | 2,073,001 | 12,735,577 | +19.4% | Licence-blocked |
| `xvf_host` set (gzip -9) | 657,801 | 11,320,377 | +6.2% | Licence-blocked |
| `xvf_host` set (Brotli q11) | 475,219 | 11,137,795 | +4.5% | Licence-blocked |
| Immich Kiosk, published archive as-is | 7,712,323 | 18,374,899 | +72.3% | AGPL-policy-blocked |
| Immich Kiosk, extracted binary (raw) | 18,546,850 | 29,209,426 | +173.9% | AGPL-policy-blocked |
| Immich Kiosk, extracted binary (Brotli q11) | 5,938,933 | 16,601,509 | +55.7% | Beats upstream's own gzip by 1.77 MB |
| **All three artifacts, raw** | 15,847,404 | 26,509,980 | +148.6% | |
| **All three artifacts, best compression** | 6,880,970 | 17,543,546 | +64.5% | |

All compression figures **measured** 2026-08-24 with `gzip` at level 9 and `brotli` at quality 11,
over the exact bytes the pins name.

**Build complexity, per option:**

- **Raw embed** — an `<EmbeddedResource>` glob and a `GetManifestResourceStream` accessor. That is
  what `XvfVendoredFirmware` already is: about 130 lines, no new dependency, one `LogicalName`
  transform to keep the resource name independent of the build host's directory separator. The
  verification path is unchanged, and deliberately so: the embedded stream is checked against the
  *same* `XvfFirmwareImage.Sha256` by the *same* `VerifiedFetch` that checks a download, so there is
  one answer to "are these the right bytes" rather than two that can disagree.
- **Compressed embed** — adds a decompressor. `System.IO.Compression.GZipStream` and `BrotliStream`
  are both in the base class library and both work under Native AOT with no new package, so the cost
  is a stream wrapper and a decision about *where* the digest is checked (it must be checked after
  inflation, against the plain-text digest, or the pin stops meaning what it says). Brotli's
  advantage here is not marginal — 466,818 against 6,062,080 — because one of the three images is
  4 MB of a single repeated byte.
- **What embedding would break** — nothing in the resource contract. `firmware.xvf3800.image` keeps
  its Observe, its Act and its digest check; the Act simply stops needing a network. What it *does*
  change is [§7.1](../version2.md)'s review posture: a vendored artifact no longer moves when the pin
  moves, so the `upstream-review.json` entry and the `NOTICE.md` row become the only record that the
  bytes on disk and the bytes upstream are still the same ones.

**A trap worth naming, because it was live while this file was written.** The build under
`.work/fw-embed/` produced a 10,795,360-byte `fl-agent` at 03:42 on 2026-08-24, which reads like a
+132,784-byte delta for a 933,888-byte image. It is not. That binary contains **neither** the
firmware bytes **nor** the resource name `firmware/respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin`
**nor** the `XvfVendoredFirmware.Origin` string — all three searched for and absent (**measured**).
It is a *before* build, which its own `before.log` says. Do not read it as an after.

---

## 6. What a frame with no internet reaches today

Declaration order in `DeviceCatalog.Build` is the order a bare frame converges in, and it puts the
network walls in a specific and unfortunate place. **Read** from the catalog and the loop:

| Position | Resource | Needs |
| ---: | --- | --- |
| 1 | `agent.version` | Fleet Manager — but *unevaluable*, not failing, so it blocks nothing |
| 2–3 | `boot.cmdline.fbcon-rotate`, `boot.config.dtoverlay-waveshare-panel` | **Nothing.** The panel lights offline |
| 4 | `agent.keypair` | Nothing — generated on device |
| — | `journal.storage-persistent` | Nothing |
| 5 | `agent.adoption` | Fleet Manager (LAN is enough) |
| **6–22** | **the fifteen `pkg.*`** | **the Debian and Raspberry Pi archives — first wall** |
| 23–47 | system configuration, session, compositor, browser | the packages above |
| ~62 | `kiosk.binary.pinned-release` | **github.com — second wall** |
| ~63 | `tool.xvf-host.installed`, `firmware.xvf3800.image` | **raw.githubusercontent.com — third wall** |
| 76–80 | GPIO, camera overlay, HDMI audio, Wi-Fi regdom, EEPROM | nothing further |

**So a frame with a LAN Fleet Manager, a LAN Immich server and no internet reaches exactly this
today:** the panel is lit and rotated, the keypair exists, the journal is persistent, the frame is
adoptable and adopted, and then `pkg.labwc` fails three times across three reboots, escalates, and
stops the pass. The frame narrates that one escalated package on `/dev/tty8` — correctly, honestly,
naming who to call — and does nothing else for as long as it stands there. **Inferred** from the
declaration order, the apt failure classification and §2.5 rung 4; not observed on hardware.

**Embedding the firmware does not move that line by one position.** `firmware.xvf3800.image` is
about sixty positions past the wall.

---

## 7. The minimum offline set

Define "useful" as the operator would: **the panel shows the household's photographs, and a call can
be placed.** Working backwards from that, in dependency order, here is everything that must be
present and where it can come from.

| # | What is needed | Can it be offline today? | If not, what would make it |
| ---: | --- | --- | --- |
| 1 | A lit, correctly rotated panel | **Yes** — positions 2–3 need no network | — |
| 2 | The agent binary and its unit | **Yes** — baked into the generated image | — |
| 3 | A reachable Fleet Manager for adoption and settings | **Yes on a LAN.** But the container fetches `livekit-server` from GitHub at start | Vendor `livekit-server` into the container image (Apache-2.0, no obstacle) |
| 4 | `labwc` + `chromium` + the portal / GStreamer / PipeWire stack | **No** — 122–332 MB of apt | A LAN apt mirror, or a fat generated image |
| 5 | `immich-kiosk` | **No** — GitHub | Fleet-Manager mirror, or embed (AGPL policy reversal) |
| 6 | Photographs on the panel | **No on a cold cache.** Offline mode is on by default and caches 200 assets, but nothing in the graph asserts the cache has ever filled | A LAN Immich server plus one online pass to warm the cache — or an asserted cache-fill resource |
| 7 | A call | **Yes once 3–5 are met** — LiveKit is LAN-local and public STUN is disabled by construction | — |
| 8 | `xvf_host` — the speaker amplifier | **No** — GitHub | Fleet-Manager mirror (embedding is licence-blocked) |
| 9 | The DFU images — flashing and recovery | **Being fixed now** — target vendored, two to go | Vendor the remaining two |

**The honest summary:** rows 4, 5, 6 and 8 are the offline gap, and **only rows 5 and 8 are things a
binary could carry**. Rows 4 and 6 are structurally outside the binary — one is 122–332 MB of Debian,
the other is the product's own content. So *no amount of embedding into `fl-agent` produces an
offline-capable frame*. What produces one is a LAN-side source of packages and photographs.

**The minimum set, as one sentence:** a frame reaches a useful state with no internet if and only if
it can reach, on its LAN, (a) a Fleet Manager whose container already carries `livekit-server`,
(b) an apt mirror or a base image with the fourteen packages already installed, (c) an Immich server
with photographs, and (d) a source for `immich-kiosk` and `xvf_host` — with the DFU images and the
agent itself already inside the binary and the image respectively.

---

## 8. Open questions, with directions

Two questions are genuinely open, and each gets its own primer, its own directions and its own
recommendation. Nothing here has been implemented.

### Question 1 — What else, if anything, should travel inside the agent binary?

**The primer.** The firmware decision is taken and half-built: the v2.1.0 target is vendored and
embedded, the fallback and the erase image are pinned but not vendored, and the csproj comment
records that as an open question. Meanwhile three other artifacts are fetched at runtime, and the
measurements above show what each would cost. The question is where to stop.

**Direction A — finish the firmware set and stop there.** Vendor
`respeaker_xvf3800_usb_dfu_firmware_v2.0.6.bin` and `4mb_all_ff.bin` beside the target, raw. Cost
**+5,128,192** over today's embedded target, taking the binary to 16,724,656 (+56.9% over baseline).
Buys: a frame that can flash *and un-flash* an array with no network, which is the whole argument for
embedding the target — an interrupted flash is repaired from Safe Mode by hand, by somebody whose day
is already going badly. Breaks nothing. Downside: the binary is served hourly to every frame in the
fleet, so 6 MB of firmware crosses the network on every version bump.

**Direction B — finish the firmware set, compressed.** Same as A, but the three images travel
Brotli-compressed and are inflated at install time. Cost **+466,818** total, binary 11,129,394
(+4.4%). The 4 MB erase image becomes 14 bytes. Costs a `BrotliStream` wrapper in
`XvfFirmwareInstaller` and one decision about digest placement (check after inflation, against the
plain-text digest, so the pin keeps meaning what it says). This is the best ratio in the entire
inventory.

**Direction C — B, plus reverse the AGPL posture and embed Immich Kiosk.** Cost a further
**+5,938,933** Brotli'd, binary about 16.6 MB. Buys: a frame that renders a slideshow with no GitHub.
Costs: every operator who redistributes an `fl-agent` binary becomes an AGPL-3.0 distributor with a
source-offer obligation, which §2.1 chose specifically to avoid *on their behalf*. This is a legal
decision to make deliberately or not at all.

**Direction D — B, plus obtain written permission for `xvf_host`.** Cost a further **+475,219**
Brotli'd. Buys: offline audio. Costs: a conversation with Seeed and/or XMOS, an outcome nobody
controls, and a licence file that does not currently exist upstream. Worth starting in parallel
because it also informs the native-protocol work `TODO.md` records as the real retirement path.

**Direction E — embed nothing further; move everything to a Fleet-Manager mirror.** §2.1 already
names this: "a Fleet-Manager mirror stays available as a later operator setting." Zero binary growth.
One container-side cache answers Immich Kiosk, `xvf_host` and the firmware, redistributes nothing
publicly, and is the same mechanism that would front an apt mirror. Costs: a new route, a cache
directory in the container's storage budget, a setting, and a fallback order.

**Recommendation: B now, E next, D started in parallel, C not without a deliberate legal decision.**
B is 466,818 bytes for the one operation on a frame that cannot be undone by rewriting the card, and
it makes the recovery route independent of everything. E is what actually buys offline capability and
it costs the binary nothing. C is the only one that trades a legal position for bytes, and the bytes
are not the expensive part.

### Question 2 — Where should offline capability live: the binary, the Fleet Manager, or the image?

**The primer.** §6 shows that a frame with no internet stops at position 6 of 81, on apt — about
sixty positions before any artifact a binary could carry. The firmware decision therefore does not
make a frame offline-capable, and was never going to; it makes one *operation* offline-capable. If
the goal is a frame that provisions with no internet at all, the apt block has to be answered, and it
cannot be answered from inside a 10.7 MB executable.

**Direction A — do nothing; accept that first provision needs internet.** Zero cost. The frame needs
a route to the Debian archives and GitHub exactly once, and after that runs offline indefinitely (the
offline photo cache and `NoContact` already cover the steady state). Honest, and possibly correct:
the failure mode is a frame that must be provisioned somewhere with a network before it is carried to
where it lives.

**Direction B — a Fleet-Manager mirror for artifacts, and `apt-cacher-ng` or a local `deb` mirror for
packages.** The Fleet Manager is already the update feed, already on the LAN, and already has a
storage budget. Adding a caching proxy for the four artifacts is a small route; pointing frames'
`sources.list` at a LAN mirror is a new resource in the catalog and a new fleet setting. Buys: every
frame after the first provisions with no internet. Costs: the operator has to run and seed the
mirror, and `sources.list` becomes a resource with a brick-adjacent failure mode — a wrong mirror is a
frame that cannot install anything.

**Direction C — a fat generated image.** Install the fourteen packages into the image during
generation, so a frame boots with the stack already present and the `pkg.*` resources converge on the
first Observe with no Act. Buys: a genuinely offline first provision, and it deletes fifteen reboots
(about 5.5 minutes at the measured 22.3 s each) from every bare provision. Costs: the generator stops
being a `debugfs`-and-`mcopy` recipe and becomes an arm64 chroot builder — a category change, not an
increment; the image grows by roughly 120–330 MB against a 2.98 GB base; and package drift moves from
apt, where it is deliberate, into the image pin, where it is a review gate. `ImagePlan`'s standing
rule that there must never be a `mkdir` in the plan is a warning about how sharp the current tooling's
edges already are.

**Direction D — a "provisioned elsewhere" workflow.** Keep everything as it is, and make the
*documented* path be: provision on a bench with internet, verify green, then ship. This is what the
build does today and what the harness is built around. Buys: nothing new to build. Costs: it is a
process guarantee rather than a technical one, and it fails silently the first time a frame is
re-imaged in the house it lives in.

**Direction E — B and C together, staged.** Fat image for the packages, the part that cannot be
mirrored cheaply because of volume; Fleet-Manager mirror for the four artifacts, the part that is
small and changes on a review cadence. Each half is independently useful and independently
revertible.

**Recommendation: B first, then reassess whether C is needed.** B is bounded work with a clear owner,
it reuses a mechanism §2.1 already anticipated, and it converts "needs the internet" into "needs the
Fleet Manager", which is a dependency the design already accepts everywhere else. C is a larger change
to the one piece of tooling whose failure mode is a card in somebody's hand, and it should not be
started until B has shown whether the apt volume is genuinely the blocker in practice. A is the honest
status quo and should be written down as the current guarantee whichever way this goes.

### A third item, smaller, that belongs to neither question

**Nothing in the graph asserts that the offline photo cache has ever filled.**
`kiosk.offline-cache.dir` asserts the directory exists and takes a test write — which the catalog
notes was chosen specifically to catch the "exists but not writable" failure — but a frame whose
Immich server has never been reachable has an empty `offline-assets` directory and a green resource.
The catalog is explicit that "either alone leaves the frame blank when Immich is unreachable", and
this is a third way to arrive at blank: both settings correct, cache never populated. Whether that
should become a resource, a telemetry field, or nothing at all is a fourth open question, and it is
the one most directly on the path from "flashes firmware offline" to "renders a photograph offline".

---

## Where the measurements live

Scratch artifacts for this file are under `.work/net-deps/` (gitignored): the six `xvf_host` files at
`xvf-host/`, the Immich Kiosk archive, and the two apt `Packages.gz` indices at `apt/`. The three DFU
images were read from `.work/vendor-firmware/`, which another workstream had already fetched, and
their digests were checked against the pin before use. None of it is the only copy of anything: every
number above can be re-derived from the pins in source plus the URLs they name.
