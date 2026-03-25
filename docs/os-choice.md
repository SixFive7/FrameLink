# Operating System Choice

## Decision

**Raspberry Pi OS Lite (Bookworm)** is the operating system for FrameLink.

## Requirements

The OS must support a Raspberry Pi 5 (2GB RAM) running 24/7 from an SD card as an unattended appliance for years. Specific needs:

- **SD card longevity** — the single biggest risk to device lifespan is SD card wear from continuous writes
- **Hardware support** — 10.1" DSI touch display, Pi Camera Module 3 (libcamera), GPIO for a physical call button
- **Kiosk browser** — Chromium in fullscreen for Immich Kiosk and WebRTC video calling
- **Stability** — must survive power outages, recover without intervention, and run unattended in a non-technical household
- **Low RAM footprint** — must fit comfortably within 2GB alongside Chromium and WebRTC
- **Long-term maintenance** — backed by an organization likely to exist and support the platform for 5-10+ years

## Why Raspberry Pi OS Lite

Three factors made this the clear winner:

1. **Built-in overlayfs read-only filesystem.** A single toggle in `raspi-config` makes the entire root filesystem read-only with a RAM overlay. All writes go to RAM and are discarded on reboot. The SD card sees essentially zero writes during normal 24/7 operation, dramatically extending its lifespan. No other evaluated OS matched this level of built-in, first-party SD card protection.

2. **First-party hardware support.** As the Raspberry Pi Foundation's own OS, it is always the first to receive driver and firmware updates for Pi peripherals. The DSI display, Pi Camera Module 3, and GPIO are guaranteed to work out of the box. Every other OS evaluated either lags behind on driver support or requires manual configuration.

3. **Largest community and documentation ecosystem.** The Chromium-on-labwc (Wayland) kiosk pattern is proven and extensively documented specifically for Pi 5. When debugging niche issues (DSI touch rotation, WebRTC camera access, GPIO interrupt handling), the size of the community directly determines how quickly problems get solved.

Additional strengths:

- **Power loss resilience** — with overlayfs enabled, a power outage simply triggers a reboot to the last known-good state. No filesystem corruption risk.
- **Wayland/labwc** — the actively-developed display stack for Pi 5, future-proofing the kiosk setup against the X11 deprecation path.
- **Backed by a publicly traded company** — the Raspberry Pi Foundation has a dedicated software team and commercial mandate to maintain the platform long-term.

## Candidates Evaluated

Twelve operating systems were evaluated against the requirements above. Each is summarized below with the primary reasons it was not selected.

### DietPi

Lightweight Debian-based distribution optimized for single-board computers. Uses the same kernel and firmware as Raspberry Pi OS. Built-in RAMLog system for SD card wear reduction. Idles at ~30 MB RAM.

**Why not chosen:** DietPi does not support overlayfs as a built-in feature — its RAMLog only protects `/var/log`, while all other filesystem writes still hit the SD card. Its Chromium kiosk mode installs the legacy X11 stack rather than the actively-developed Wayland/labwc path. The project is primarily maintained by a single developer (MichaIng), creating long-term sustainability risk for a device expected to run for years. It was the closest runner-up.

### BalenaOS

Container-based OS built on Yocto, designed for IoT fleet management. Read-only rootfs by design with A/B partition updates and automatic rollback. Includes a "browser block" Docker image for digital signage.

**Why not chosen:** Docker overhead is tight on 2GB RAM (~700-900 MB with a kiosk container running). Requires a balenaCloud account and dependency on balena's cloud infrastructure. Container abstraction adds complexity for camera and GPIO access. Overkill for a 2-device project that doesn't need fleet management.

### Alpine Linux

Ultra-minimal, musl-based Linux distribution. Diskless mode runs entirely from RAM — zero writes to SD card, the gold standard for SD card longevity.

**Why not chosen:** Pi Camera Module 3 support (libcamera) is poorly documented and may require significant manual effort. musl libc can cause compatibility issues with some software. No built-in auto-update mechanism. The Pi-specific community is small, making it hard to find answers to kiosk and camera questions.

### Ubuntu Core

Canonical's immutable, snap-based OS for IoT. 10-year support commitment. Automatic atomic updates with rollback.

**Why not chosen:** Snap confinement complicates camera and GPIO access. Chromium-in-snap has known Wayland and kiosk issues (ungraceful shutdown recovery pages). The snap-only software model is restrictive. snapd daemon overhead consumes RAM.

### Ubuntu Server for Raspberry Pi

Canonical's server distribution with 5-year LTS support. Familiar Ubuntu ecosystem with good `unattended-upgrades` support.

**Why not chosen:** Heavier than Raspberry Pi OS Lite. Pi-specific hardware support lags behind the first-party OS. The snapd daemon uses RAM and writes to disk. Less kiosk documentation for Pi deployments.

### Ubuntu Desktop for Raspberry Pi

Full GNOME desktop variant of Ubuntu for Pi.

**Why not chosen:** GNOME desktop uses ~800 MB-1 GB at idle. With Chromium, total RAM usage would approach or exceed 2 GB, causing heavy swapping. Not viable for a 2 GB device.

### Raspberry Pi OS Desktop (Bookworm)

Full desktop variant of Raspberry Pi OS with the PIXEL/Wayland desktop environment.

**Why not chosen:** Runs a full desktop environment that is unnecessary for a kiosk appliance. Idles at ~400-500 MB before Chromium, leaving tight headroom on 2 GB. A reported memory leak in desktop components (pcmanfm/Wayland) can consume 1.2 GB over ~6 hours — disqualifying for 24/7 operation. More attack surface and harder to lock down.

### openSUSE MicroOS

Immutable OS with transactional updates and automatic health-check rollback. Based on openSUSE Tumbleweed.

**Why not chosen:** Based on a rolling release (Tumbleweed), which contradicts the multi-year stability goal. Btrfs snapshot overhead adds storage pressure on SD cards. Pi 5 support is very new (late 2025). The Pi-specific community is tiny.

### Fedora IoT

Immutable Fedora variant using rpm-ostree, designed for edge and IoT devices.

**Why not chosen:** Pi 5 display support is explicitly incomplete — the initial Fedora image for Pi 5 is described as "NOT yet suitable for desktop UXes and related use cases that require a display." Fedora's ~13-month release lifecycle is too short for a device expected to run for years.

### Yocto / OpenEmbedded

Industrial-grade framework for building custom embedded Linux distributions. Used in commercial products. Supports A/B partition updates via RAUC or Mender.

**Why not chosen:** Massive upfront engineering investment (weeks to months) completely disproportionate for a 2-unit personal project. Chromium cross-compilation is a known pain point. Build times of hours on powerful hardware. All ongoing maintenance falls on the builder.

### Buildroot

Tool for building custom, minimal embedded Linux images from source. Full control over every byte in the image.

**Why not chosen:** The leading Buildroot-WebKit kiosk project abandoned Pi 5 support due to porting complexity. Chromium cross-compilation in Buildroot is notoriously difficult. Same massive engineering and maintenance burden as Yocto, with even less community support for the specific configuration needed.
