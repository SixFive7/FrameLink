# FrameLink

A Raspberry Pi-based digital photo frame with one-button video calling. Designed for elderly family members with dementia — always showing familiar photos, with a simple physical button to start a face-to-face video call between two paired devices.

## Concept

Each FrameLink unit runs [Immich Kiosk](https://github.com/damongolding/immich-kiosk) to display a slideshow of family photos on a touchscreen. A physical GPIO button on the device initiates a bi-directional video call between two FrameLink units, temporarily replacing the photo slideshow. When the call ends, the slideshow resumes automatically.

The goal is zero-interaction operation for the viewer — photos play continuously, and answering a call requires no action. The only interaction is pressing the button to *initiate* a call.

## Hardware

Each unit consists of (x2 ordered, sourced from [Waveshare](https://www.waveshare.com/)):

| Component | Product | Part Number | Brand | SKU |
|---|---|---|---|---|
| Single-board computer | [Raspberry Pi 5 (2GB RAM)](https://www.waveshare.com/raspberry-pi-5.htm?sku=28316) | Raspberry Pi 5-2GB | Raspberry Pi Foundation | 28316 |
| Display | [10.1" DSI Capacitive Touch Display, 800x1280, IPS, Optical Bonding, 10-Point Touch](https://www.waveshare.com/10.1-dsi-touch-a.htm) | 10.1-DSI-TOUCH-A | Waveshare | 30052 |
| Camera | [Raspberry Pi Camera Module 3, 12MP, Auto-Focus, IMX708](https://www.waveshare.com/raspberry-pi-camera-module-3.htm?sku=23943) | Raspberry Pi Camera module 3 | Raspberry Pi Foundation | 23943 |
| Power supply | [Official 27W USB Type-C Power Supply (White, EU)](https://www.waveshare.com/raspberry-pi-5-official-27w-psu.htm?sku=25910) | Raspberry Pi 5 Official 27W PSU White EU | Raspberry Pi Foundation | 25910 |
| Cooling | [Aluminum Heatsink with Thermal Pads and Spring-Loaded Push Pins](https://www.waveshare.com/pi5-active-cooler-c.htm) | Pi5-Active-Cooler-C | Waveshare | 26415 |
| Call button | GPIO momentary push button (TBD) | — | — | — |

## Motivations

Key design decisions and the reasoning behind them are documented separately:

- [Operating System Choice](docs/os-choice.md) — why Raspberry Pi OS Lite over 11 other candidates

## Software Stack

- **OS**: Raspberry Pi OS Lite (Bookworm) — chosen for its first-party hardware support, built-in overlayfs read-only filesystem (critical for SD card longevity on a 24/7 device), and the largest community ecosystem for Pi kiosk deployments
- **Photo slideshow**: [Immich Kiosk](https://github.com/damongolding/immich-kiosk) — connects to an existing Immich server to display photos
- **Video calling**: TBD — needs to support peer-to-peer or relay-based bi-directional video/audio between two Pi units
- **GPIO handler**: Script to detect button press and trigger the video call, managing the transition between kiosk and call modes

## Architecture

```
┌─────────────────────────────────────────┐
│            FrameLink Unit A             │
│                                         │
│  ┌───────────┐    ┌──────────────────┐  │
│  │  Immich    │    │  Video Call      │  │
│  │  Kiosk    │◄──►│  Service         │  │
│  │ (default)  │    │ (on demand)      │  │
│  └───────────┘    └──────────────────┘  │
│        ▲                 ▲    ▲         │
│        │           ┌─────┘    │         │
│   ┌────┴────┐  ┌───┴───┐ ┌───┴──┐      │
│   │ Display │  │Camera │ │Button│      │
│   └─────────┘  └───────┘ └──────┘      │
└─────────────────────────────────────────┘
            │                   ▲
            │   Network         │
            ▼                   │
┌─────────────────────────────────────────┐
│            FrameLink Unit B             │
│           (same setup)                  │
└─────────────────────────────────────────┘
```
