# Camera & Audio Research

## Decision

The **Pi Camera Module 3** is used with a **v4l2loopback + GStreamer bridge** to make it accessible to Chromium's `getUserMedia()` API. A **ReSpeaker XVF3800** (XMOS XVF3800-based 4-mic circular array) connected via USB provides microphone input, with a separate enclosed speaker connected to the XVF3800's JST PH 2.0 speaker output.

---

## Camera

### The Problem

Chromium accesses cameras via the V4L2 (Video4Linux2) API using `getUserMedia()`. The Pi Camera Module 3 (IMX708) uses `libcamera`, which is a separate camera stack that Chromium does not support. Calling `getUserMedia()` with only the Camera Module 3 connected returns "device not found."

This affects all WebRTC solutions equally — it is a Pi platform constraint, not a limitation of any specific video calling library.

### Camera Bridge Solution

A GStreamer pipeline captures from `libcamerasrc` and outputs to a virtual V4L2 device created by `v4l2loopback`. Chromium sees this virtual device as a standard webcam.

#### Installation

```bash
sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-libcamera v4l2loopback-dkms
```

#### Kernel Module Configuration

File: `/etc/modprobe.d/v4l2loopback.conf`

```
options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1
```

`exclusive_caps=1` is required for Chromium to detect the device as a camera (it makes the device report capture-only capabilities).

#### GStreamer Pipeline (systemd service)

```bash
gst-launch-1.0 libcamerasrc \
  ! "video/x-raw,width=1280,height=720,framerate=30/1" \
  ! videoconvert \
  ! v4l2sink device=/dev/video8
```

This runs as a systemd service that starts at boot before Chromium.

#### Encoding Reality

**Pi 5 has no hardware H.264 encoder.** The BCM2712 SoC dropped both H.264 encode and decode from the hardware. The only hardware codec on Pi 5 is HEVC (H.265) decode. WebRTC uses VP8, VP9, or H.264 — none of which have hardware acceleration on Pi 5.

All video encoding for the outgoing stream is **software-only** on the quad Cortex-A76 cores (2.4 GHz). At 480p30, software VP8 encoding is expected to use ~10-15% of one core. At 720p30, ~20-30%. Using 480p for the outgoing stream is recommended to reduce CPU load.

#### Alternative: USB Webcam

A USB webcam works natively in Chromium with zero bridge or workaround. If the v4l2loopback bridge proves unstable or too resource-heavy, a USB webcam is the simplest fallback. Trade-off: lower image quality than the Camera Module 3, and an additional USB device.

---

## Audio

### Decision

The audio system uses a **ReSpeaker XVF3800** (XMOS XVF3800-based 4-mic circular array) connected to the Pi 5 via USB for microphone input AND speaker output. A separate enclosed speaker connects to the XVF3800's JST PH 2.0 speaker output. This replaces the original "USB speakerphone" plan.

Two speakers were ordered for A/B testing during Phase 1:

- **Adafruit 3351** — Mono Enclosed Speaker, 3W 4-ohm, with pre-attached 57cm JST PH 2.0 cable
- **PUI Audio AS07104PO-LW152-R** — Enclosed Oval Speaker, 3W 4-ohm, with 152mm wire leads (needs Adafruit 261 JST PH cable spliced on)

### Why the XVF3800 (Not a USB Speakerphone)

The project is a wall-mounted 3D-printed case with a 10.1" DSI display (no HDMI, no built-in speakers). This eliminates the "plug into a TV" approach used by all existing Pi video call projects. A USB speakerphone (Jabra Speak 410, Anker PowerConf, EMEET Luna) would work but is bulky and hard to integrate into a slim wall-mounted case.

The XVF3800 provides:

- 4-mic array with hardware AEC, beamforming, noise suppression, de-reverb, AGC, VAD, DoA — all processed on the XMOS chip
- Speaker output (JST PH 2.0 + 3.5mm) with built-in amp (up to 5W into 4-ohm from USB 5V)
- USB plug-and-play on Pi 5 — appears as standard USB audio device (mic input + speaker output), no drivers needed
- 99mm circular PCB, 4mm thick — fits above the display in the case with the camera in the center
- Mono output — correct for video calling (WebRTC sends mono audio)

### Existing Pi Video Call Projects (Community Survey)

Every existing Pi video call project uses HDMI into a TV for audio output:

1. **"Automatic Video Conference for Grandma"** (Instructables) — TV speakers + USB webcam mic, Jitsi
2. **"Meet" by paschkel** (GitHub) — I2S MEMS mic (SPH0645LM4H/INMP441) + TV speakers, Jitsi
3. **balena "Instant Video Call"** — TV speakers + USB webcam mic, Jitsi via balenaOS
4. **Maker Faire "Pi Video Calls for Elderly"** — TV + USB webcam
5. **Hackster.io "Video Call for Seniors" by Rundhall** — USB webcam + TV speakers, fully hands-off

None used a dedicated speakerphone or audio HAT because they all connected to TVs. FrameLink can't do this (DSI display, no HDMI, no built-in speakers) — hence the need for a separate audio solution.

---

### ReSpeaker Product Family Evolution

#### Legacy Products (EOL or End-of-Support)

- **ReSpeaker 2-Mic Pi HAT v1.0** — WM8960 codec, 2 mics, GPIO/I2S, Pi 5 NOT supported
- **ReSpeaker 2-Mic Pi HAT v2.0** — TLV320AIC3104 codec, 2 mics, GPIO/I2S, Pi 5 supported. JST speaker out + 3.5mm. Good but only 2 mics.
- **ReSpeaker 4-Mic Array (circular, AC108)** — 4 mics, NO audio output, discontinued
- **ReSpeaker 4-Mic Linear Array Kit** — Voice Accessory HAT + 4-mic strip + ribbon cable. AC108 ADC + AC101 DAC. JST speaker + 3.5mm. Software AEC only (via librespeaker). Driver (seeed-voicecard) abandoned by Seeed. Community HinTak fork works on Pi 5 kernel 6.6 but unofficial.
- **ReSpeaker 6-Mic Circular Array Kit** — Same Voice Accessory HAT + 6-mic circular board. Same driver issues. 360-degree pickup wasted for a wall-mounted device.
- **ReSpeaker USB Mic Array v2.0 (XVF3000)** — 4 mics, USB, hardware AEC on XMOS chip. But NO speaker output.
- **MATRIX Voice** — 8 mics, FPGA-based. Dead product, no support.

#### Current Products

- **ReSpeaker Lite (XU316)** — 2 mics, XMOS XU316, USB + I2S, JST speaker + 3.5mm. AEC/NS on chip. 3m range. ~$25 bare board.
- **ReSpeaker XVF3000 v3.0** — 4 mics, USB, hardware AEC. No speaker output. ~$80. Superseded by XVF3800.
- **ReSpeaker XVF3800** — 4 mics, XMOS XVF3800, USB + I2S, JST speaker + 3.5mm, hardware AEC/NS/beamforming/de-reverb/AGC/VAD/DoA. 5m range. ~$50 bare board. **THE CHOSEN SOLUTION.**

#### Key Architecture Difference (Old vs New)

- **Old (4-mic/6-mic kits):** AC108 is just an ADC. AEC/beamforming was SOFTWARE running on the Pi via Seeed's librespeaker daemon. Seeed stopped maintaining this software.
- **New (XVF3000/XVF3800/XU316):** AEC/beamforming runs on the XMOS chip itself. Pi just receives clean, processed audio over USB.

#### The 4-Mic and 6-Mic Kits' Two-Board Architecture

- **Board A: "Voice Accessory HAT"** — plugs on Pi GPIO, has DAC (AC101), 3.5mm, JST speaker. No mics.
- **Board B: Mic strip/circle** — connects to Board A via ribbon cable. Has mics + AC108 ADCs.
- The Voice Accessory HAT is NOT the same as the 2-Mic HAT. Different PCBs, different chips.

#### The Waveshare WM8960 Audio HAT

- Functionally equivalent to ReSpeaker 2-Mic HAT v1.0 (same WM8960 codec)
- 2 MEMS mics, JST speaker + 3.5mm, Pi 5 compatible
- Available from TinyTronics NL with speaker set
- Was considered but rejected in favor of XVF3800 (only 2 mics, no hardware AEC)

#### Geekworm Voice HAT

- WM8960 codec (same as above), 2 mics + onboard speaker + RGB LEDs
- Onboard speaker too tiny/quiet for video calling at distance

---

### XVF3800 Technical Details

#### XVF3000 vs XVF3800

| Spec | XVF3000 | XVF3800 |
|---|---|---|
| Status | Legacy | Current |
| Codec | WM8960 (88dB SNR) | TLV320AIC3104 (102dB SNR) |
| Speaker output | NO | YES (JST + 3.5mm, up to 5W) |
| AEC | Yes | Yes (improved algorithms) |
| VAD | No | Yes |
| DoA | No | Yes |
| Price | ~$80 | ~$50 |

No reason to consider the XVF3000 — XVF3800 is better in every way AND cheaper.

#### XVF3800 Board Specs

- **PCB:** 99mm diameter circular, 4mm thick
- **Mics:** 4 MEMS (PDM), positioned at cardinal points (N/S/E/W), ~37-40mm from center, ~10-12mm inboard from edge
- **LED ring:** 12x WS2812 RGB, closer to edge than mics
- **Codec:** TLV320AIC3104 (102dB SNR, 16/20/24/32-bit, up to 96kHz)
- **Speaker amp:** unidentified chip, separate from codec. Max 5W. Powered from USB 5V.
- **Speaker connector:** JST PH 2.0 (2-pin)
- **Headphone:** 3.5mm jack
- **USB:** USB-C, appears as standard USB audio device in both directions
- **Amp enable:** GPIO pin X0D31, active LOW. Must be enabled via xvf_host command: `./xvf_host GPO_WRITE_VALUE 31 0`
- **AEC reference:** left channel of USB/I2S input. DAC plays left channel on both L+R outputs.
- **Firmware modes:** USB (default) or I2S (one active at a time)
- **3D STEP file:** [respeaker_mic_array_xvf3800_1_with-xiao-0820.stp](https://files.seeedstudio.com/wiki/respeaker_xvf3800_usb/3d/respeaker_mic_array_xvf3800_1_with-xiao-0820.stp)

#### Camera + XVF3800 Physical Compatibility

- Camera Module 3: 25 x 24 x 11.5mm
- XVF3800 mics are ~37-40mm from center
- Camera fits in center of XVF3800 board WITHOUT covering any mics
- Camera mounts in front of the mic board (camera lens through case, mic board behind)
- Mics are bottom-firing MEMS — need holes in case front panel aligned with mic positions
- Ribbon cable (1mm thick, 16mm wide) routes between two mics without covering them
- Case design: camera + XVF3800 assembly above the display, ~100mm wide section

#### XVF3800 Product Variants

| Variant | What | Price | Best Source |
|---|---|---|---|
| Bare board (USB only) | Just the mic array PCB | ~$50 / £39.50 | The Pi Hut, Seeed Studio |
| With XIAO ESP32S3 | Board + ESP32S3 for wireless/I2S | ~$55 | Antratek NL, Seeed Studio |
| With case | Board + acoustic housing | ~$54 | Seeed Studio |
| With ESP32S3 + case | Everything | ~$54 | Seeed Studio |

The ESP32S3 variant works identically in USB mode — the ESP32 just sits unused.

---

### AEC (Acoustic Echo Cancellation) Design

This is critical because mic array and speaker are in the same case.

#### How XVF3800 AEC Works

1. Far-end audio (remote caller's voice) is sent to the speaker AND simultaneously used as AEC reference signal
2. Adaptive filter models the acoustic echo path between speaker and mics
3. Filter subtracts the echo from mic input
4. Post-AEC suppression handles residual nonlinear echo
5. Then: noise suppression -> de-reverb -> beamforming -> AGC -> VAD

#### What Degrades AEC (Ranked by Impact)

1. **Speaker distortion** (CRITICAL) — AEC is a linear model. Nonlinear distortion can't be canceled. Don't overdrive the speaker. A speaker with headroom (rated power >> actual power) distorts less.
2. **Mechanical vibration coupling** (HIGH) — sound through the case body bypasses acoustic AEC. Mount speaker with foam/rubber isolation. Separate acoustic chambers.
3. **Speaker-to-mic distance** (HIGH) — more distance = less echo to cancel. Our design: mics at top, speaker at bottom = ~150mm+. Good.
4. **Open-back vs sealed speaker** (MEDIUM-HIGH) — open-back radiates from both sides, unpredictable echo paths. Always use sealed enclosure.
5. **System delay tuning** (CRITICAL) — AUDIO_MGR_SYS_DELAY (default 12) must match actual audio path delay. Wrong value = AEC barely works. Must tune during testing.
6. **Volume control method** (MEDIUM) — use digital volume control (ALSA mixer) only. Analog volume control after DAC breaks the reference signal relationship.

#### XMOS Acoustic Design Guidelines (from Official Docs)

1. Minimize non-linearities (speaker distortion + mechanical coupling)
2. Isolate speakers from mics mechanically (gaskets, foam, separate chambers)
3. Place speakers as far from mics as feasible
4. Maintain linear gain structure (digital volume only)
5. Speakers in separate sealed acoustic cavity with solid barrier

#### Case Design for AEC

```
Side view:
    [Camera + XVF3800 mic board]  <-- foam gasket around mic board
    ============ solid wall ============  <-- physical barrier
    [Display - 239 x 147mm]
    ============ solid wall ============  <-- physical barrier
    [Speaker in sealed cavity]   <-- rubber/foam mounted, sealed back
    |-- grille --|
```

Three acoustically separated chambers: mic section, display section, speaker section.

#### AEC-Relevant Tuning Parameters

- `AUDIO_MGR_SYS_DELAY` (default 12) — compensate for audio path delay
- `AUDIO_MGR_REF_GAIN` (default 8.0) — reference signal gain
- `PP_DTSENSITIVE` — double-talk sensitivity / echo suppression aggressiveness
- Amp enable: `./xvf_host GPO_WRITE_VALUE 31 0`

#### AEC Sources

- [XMOS XVF3800 Acoustic Design Guidelines](https://www.xmos.com/documentation/XM-014888-PC/html/modules/fwk_xvf/doc/user_guide/06_acoustic_design_guidelines.html)
- [XMOS XVF3800 Tuning Guide](https://www.xmos.com/documentation/XM-014888-PC/html/modules/fwk_xvf/doc/user_guide/04_tuning_the_application.html)
- [XMOS XVF3800 Audio Pipeline](https://www.xmos.com/documentation/XM-014888-PC/html/modules/fwk_xvf/doc/datasheet/03_audio_pipeline.html)

---

### Speaker Selection

#### Requirements

- 4-ohm impedance (maximizes power from 5V USB amp: ~4W into 4-ohm vs ~2W into 8-ohm)
- 3-5W rated power
- Sealed back cavity (for AEC: prevents rear radiation from reaching mics via unpredictable paths)
- Good voice quality (speech frequencies 300Hz-4kHz)
- Low distortion (critical for AEC)
- Small enough to fit along 239mm display bottom edge
- Vibration isolation (foam tape / rubber gasket between speaker and case)

#### Impedance Trade-offs

- **2-ohm:** Amp overloaded, clipping, overheating. Avoid.
- **4-ohm:** ~3-5W from the 5V amp. Maximum clean volume. Sweet spot.
- **8-ohm:** ~1.5-2.5W. Safe but quieter. The Waveshare 8-ohm 5W speaker would only get ~2W actual.

#### Loudness Estimates (80 dB SPL/1W@1m baseline)

- 8-ohm at ~2W: ~83 dB at 1m, ~77 dB at 2m (quiet conversation)
- 4-ohm at ~4W: ~86 dB at 1m, ~80 dB at 2m (normal conversation)

#### All Speakers Considered (Comprehensive Comparison)

| Speaker | Type | Impedance | Power | Sealed Enclosure | Vibration Isolation | Magnet | Surround | Voice Quality | AEC Friendliness | XVF3800 Connector | Needs Chamber | Dimensions | Shape | Datasheet | Manufacturer | Sourcing | EU/NL Source | Price |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Adafruit 3351 | Enclosed mono | 4-ohm | 3W | Yes (factory) | No (add foam tape) | Ferrite | Paper cone | Good (generic) | Good (sealed) | JST PH 2.0 pre-attached (57cm) | No | ~40mm round enclosed | Round | No | Generic OEM | Reliable | DigiKey NL | ~$8 |
| Adafruit 1669 | Enclosed stereo pair | 8-ohm | 3W each | Yes (factory) | No | Ferrite | Paper | Good | Good | Bare wires | No | ~40mm round each | Round | No | Generic OEM | Reliable | DigiKey NL | ~$10/pair |
| Adafruit 3968 | Enclosed mini oval | 8-ohm | 1W | Yes (factory) | No | Ferrite | Paper | Fair | Good | JST PH 2.0 | No | ~28x40mm oval | Oval | No | Generic OEM | Reliable | DigiKey NL | ~$5 |
| Adafruit 4445 | Enclosed mini round | 4-ohm | 3W | Yes (factory) | No | Ferrite | Paper | Good | Good | JST PH 2.0 | No | 36mm round | Round | No | Generic OEM | Reliable | DigiKey NL | ~$6 |
| PUI AS07104PO-WR-R | Enclosed oval, weather-resistant | 4-ohm | 3W | Yes (factory) | Yes (gasket) | Neodymium | Rubber (PEI cone) | Very Good | Very Good | Solder pads | No | 71x41mm | Oval | Yes | PUI Audio | Reliable | DigiKey NL | ~$12 |
| PUI AS07104PO-LW152-R | Enclosed oval, wire leads | 4-ohm | 3W | Yes (factory) | No (add foam) | Neodymium | Rubber (PEI cone) | Very Good | Very Good | 152mm wire leads (needs JST splice) | No | 71x41mm | Oval | Yes | PUI Audio | Reliable | DigiKey NL | ~$9 |
| PUI AS07104PO-R | Enclosed oval, solder pads | 4-ohm | 3W | Yes (factory) | No (add foam) | Neodymium | Rubber (PEI cone) | Very Good | Very Good | Solder pads only | No | 71x41mm | Oval | Yes | PUI Audio | Reliable | DigiKey NL | ~$8 |
| Tectonic TEBM28C10-4/B | BMR driver (bare) | 4-ohm | 10W | No (needs chamber) | No (add gasket) | Neodymium | Rubber | Excellent (BMR) | Excellent (headroom) | Bare terminals | Yes (~80-110ml) | 55mm dia x 24mm | Round | Yes | Tectonic | Reliable | SoundImports EU, DigiKey | ~$12-15 |
| Same Sky CES-39209-28PM | Enclosed round | 8-ohm | 2W | Yes (factory) | No | Ferrite | Paper | Fair | Good | Solder pads | No | 39mm round | Round | Yes | Same Sky | Reliable | DigiKey NL | ~$5 |
| Visaton K 50 (4-ohm) | Bare driver | 4-ohm | 3W | No (needs chamber) | No | Ferrite | Paper | Good | Fair | Solder tabs | Yes | 50mm round | Round | Yes | Visaton | Reliable | Conrad NL, TinyTronics | ~$6 |
| Visaton K 40 | Bare driver | 8-ohm | 2W | No (needs chamber) | No | Ferrite | Paper | Good | Fair | Solder tabs | Yes | 40mm round | Round | Yes | Visaton | Reliable | Conrad NL | ~$5 |
| Visaton K 28.40 | Bare driver | 8-ohm | 2W | No (needs chamber) | No | Ferrite | Paper | Fair | Fair | Solder tabs | Yes | 28x40mm | Oval | Yes | Visaton | Reliable | Conrad NL | ~$4 |
| Dayton CE32A-4 | Bare driver | 4-ohm | 5W | No (needs chamber) | No | Neodymium | Rubber | Good | Good | Solder tabs | Yes | 32mm round | Round | Yes | Dayton Audio | Reliable | Parts Express, SoundImports | ~$8 |
| Dayton DMA45-4 | Bare driver | 4-ohm | 10W | No (needs chamber) | No | Neodymium | Rubber | Very Good | Excellent | Solder tabs | Yes | 45mm round | Round | Yes | Dayton Audio | Reliable | Parts Express, SoundImports | ~$12 |
| Google Home Mini OEM | Extracted driver | 4-ohm | ~3W | No (needs chamber) | No | Neodymium | Rubber | Good | Good | Bare wires | Yes | ~40mm round | Round | No | Unknown | Unreliable (salvage) | N/A | Salvage |
| Generic 40mm cavity | Enclosed generic | 4/8-ohm | 2-3W | Yes (factory) | No | Ferrite | Paper | Fair | Fair | Bare wires | No | ~40mm | Round | No | Various | Inconsistent | AliExpress | ~$2-4 |
| Generic oval 53x35mm | Enclosed generic | 4/8-ohm | 3W | Partial | No | Ferrite | Paper | Fair | Fair | Bare wires | Maybe | 53x35mm | Oval | No | Various | Inconsistent | AliExpress | ~$2-4 |
| Generic racetrack 70x30mm | Enclosed generic | 8-ohm | 3W | Partial | No | Ferrite | Paper | Fair | Fair | Bare wires | Maybe | 70x30mm | Racetrack | No | Various | Inconsistent | AliExpress | ~$2-4 |
| Waveshare 8-ohm 5W | Enclosed | 8-ohm | 5W | Yes (factory) | No | Unknown | Unknown | Good | Fair (8-ohm) | JST PH 1.25 (wrong) | No | Unknown | Unknown | No | Waveshare | Reliable | Waveshare, TinyTronics | ~$4 |
| Waveshare 8-ohm 2W | Enclosed | 8-ohm | 2W | Yes (factory) | No | Unknown | Unknown | Fair | Fair (8-ohm) | JST PH 1.25 (wrong) | No | Unknown | Unknown | No | Waveshare | Reliable | Waveshare, TinyTronics | ~$3 |
| Waveshare 8-ohm 2W (B) | Enclosed | 8-ohm | 2W | Yes (factory) | No | Unknown | Unknown | Fair | Fair (8-ohm) | JST PH 1.25 (wrong) | No | Unknown | Unknown | No | Waveshare | Reliable | Waveshare, TinyTronics | ~$3 |
| Jabra Speak 410 | USB speakerphone | N/A | N/A | Yes | N/A | N/A | N/A | Excellent | Excellent | USB (standalone) | N/A | 120mm puck | Round | N/A | Jabra | Reliable | Coolblue NL, bol.com | ~$55-70 |
| Anker PowerConf | USB speakerphone | N/A | N/A | Yes | N/A | N/A | N/A | Excellent | Excellent | USB (standalone) | N/A | 124mm puck | Round | N/A | Anker | Reliable | bol.com, Amazon.nl | ~$60-80 |
| EMEET Luna | USB speakerphone | N/A | N/A | Yes | N/A | N/A | N/A | Very Good | Very Good | USB (standalone) | N/A | 120mm puck | Round | N/A | EMEET | Reliable | Amazon.nl | ~$50-60 |
| Dell SP3022 | USB soundbar | N/A | N/A | Yes | N/A | N/A | N/A | Good | Good | USB (standalone) | N/A | 226x71mm | Bar | N/A | Dell | Reliable | Dell UK | ~$80-100 |

#### Why Adafruit 3351 Was Selected as Primary

- Factory sealed enclosure — no chamber design needed
- JST PH 2.0 connector pre-attached — plugs directly into XVF3800
- 57cm cable — reaches from bottom of display to top
- 4-ohm, 3W
- Available from DigiKey NL (reliable, fast)
- Known part number, consistent product
- **Downsides:** generic OEM speaker, no published frequency response, 3W = at amp limit, plastic enclosure transmits vibration (add foam tape)

#### Why PUI AS07104PO-LW152-R Was Selected as Backup

- Factory sealed enclosure
- 4-ohm, 3W, 86 dB SPL
- Neodymium magnet, rubber surround (PEI cone)
- Full engineering datasheet with specs
- Voice-band optimized (intercom/kiosk grade)
- Available from DigiKey NL
- Weather-resistant variant (WR) has gasket for vibration isolation but costs more and is unnecessary indoors
- **Downsides:** wire leads need JST PH cable spliced on, 71x41mm (bigger than Adafruit)

#### PUI Audio Part Number Naming Convention

- `AS07104PO-R` — base model, solder pads only
- `AS07104PO-LW152-R` — Lead Wire 152mm pre-attached
- `AS07104PO-WR-R` — Water Resistant gasket
- `AS07104PO-LW152-WR-R` — both

#### The Tectonic TEBM28C10-4/B Option (Not Ordered, Documented for Reference)

- BMR (Balanced Mode Radiator) — superior voice reproduction, widest dispersion
- 4-ohm, 10W rated — massive headroom, minimal distortion at 4W (best for AEC)
- 55mm diameter x 24mm, neodymium, rubber surround
- Full datasheet with impedance curves
- Available from SoundImports EU, Parts Express, DigiKey
- ~$12-15
- **BUT:** requires a 3D-printed sealed back chamber (~80-110ml)
- Sealed chamber design is straightforward (any shape, 3-4mm PETG walls, 100% infill, pinch of polyfill) but requires CAD work

#### Sealed Chamber Design Notes (For Future Reference)

- **Target volume:** 80-110ml gross (Qtc = 0.6-0.8 range, all acceptable for voice)
- **Formula:** Vb = Vas / [(Qtc/Qts)^2 - 1], using Vas=0.104L, Qts=0.44
- **Tolerance:** +/-50% is fine for voice. Speech clarity is 300Hz-4kHz, unaffected by box volume.
- **Shape:** doesn't matter at this scale (standing waves > 3kHz in a 50mm box)
- **Material:** PETG preferred, PLA acceptable. 3-4mm walls, 3+ perimeters, 100% infill
- **Damping:** pinch of polyester pillow stuffing loosely placed inside
- **Seal driver** with foam gasket ring (self-adhesive craft foam)
- **Seal wire exit** with hot glue
- **Driver mounting:** 33.9mm square hole pattern, M3 screws
- **Total depth:** 24mm (driver) + 30-40mm (cavity) = ~55-65mm
- **Thiele-Small parameters:** Fs=145Hz, Qts=0.44, Vas=0.104L (loudspeaker database), Xmax=1.4mm

---

### Connector Details

#### JST PH 2.0 (Used by XVF3800 Speaker Output)

- 2-pin, 2.0mm pitch
- Adafruit 3351 has this connector pre-attached (57cm cable)
- Adafruit 261 is a 100mm pre-made cable (JST PH plug to bare wires) for splicing
- Adafruit 3814 is 200mm but has male header pins (wrong end — doesn't plug into XVF3800)
- Waveshare speakers use JST PH 1.25 (wrong pitch — incompatible without re-crimping)

#### Crimping JST PH

- Generic crimp tools are too wide (2.5mm+), crush the tiny 1.0-1.9mm pins
- Engineer PA-09 is the recommended tool (available as Adafruit 350 on DigiKey, ~$25)
- For just 2 cables: soldering/splicing is simpler than buying a crimp tool

---

### USB Speakerphone Options (Rejected)

Documented for completeness — these were considered but rejected for the wall-mounted case:

- **Jabra Speak 410:** 120mm puck, $55-70. Coolblue NL, bol.com.
- **Anker PowerConf:** 124mm puck, 6-mic array, $60-80. bol.com, Amazon.nl.
- **EMEET Luna:** 120mm puck, 3-mic array, $50-60. Amazon.nl.
- **Dell SP3022:** 226x71mm soundbar, $80-100. Dell UK.

**Reason for rejection:** too bulky for a slim wall-mounted case, hard to integrate into 3D print.

---

### Audio Configuration

Chromium selects the default ALSA audio device. If multiple audio devices are present (HDMI, USB, headphone jack), set the XVF3800 as default:

```bash
# List audio devices
aplay -l
arecord -l

# Set default in /etc/asound.conf or ~/.asoundrc
```

> **Trixie note:** Raspberry Pi OS Trixie uses PipeWire + WirePlumber as the default audio stack (PulseAudio is no longer a `raspi-config` option). On the **Lite** image the ALSA compatibility layer is not installed by default, so `aplay -l` and Chromium WebRTC audio may not see any devices until `pipewire-alsa` and `wireplumber` are installed. This is covered in [2-sd-flash-first-boot.md](../docs/2-sd-flash-first-boot.md).

Alternatively, the SPA can specify the audio device by deviceId in the `getUserMedia()` constraints.

---

### Sourcing

#### Ordered Parts

| Component | Qty | Source | Link |
|---|---|---|---|
| ReSpeaker XVF3800 bare board | 2 | The Pi Hut (UK) | [thepihut.com](https://thepihut.com/products/respeaker-xmos-xvf3800-ai-powered-4-mic-array-for-clear-voice-even-in-noise) |
| Adafruit 3351 speaker | 2 | DigiKey NL | [digikey.nl](https://www.digikey.nl/nl/products/detail/adafruit-industries-llc/3351/6612456) |
| PUI AS07104PO-LW152-R speaker | 2 | DigiKey NL | [digikey.nl](https://www.digikey.nl/nl/products/detail/pui-audio-inc/AS07104PO-LW152-R/4835136) |
| Adafruit 261 JST PH cable | 2 | DigiKey NL | [digikey.nl](https://www.digikey.nl/nl/products/detail/adafruit-industries-llc/261/5353586) |

#### Still Needed

| Component | Notes |
|---|---|
| USB-C to USB-A cable (x2) | Connect XVF3800 to Pi 5. Short (~30cm). Standard cable. |
| GPIO momentary push button (x2) | Call toggle. Any normally-open momentary switch. |
| microSD cards 32GB+ (x2) | For Pi OS. |
| Foam tape / isolation strip | Mount speaker isolated from case. Hardware/craft store. |
| Heat-shrink tubing | For PUI speaker cable splice. |

#### XVF3800 Sourcing Notes

- Bare board out of stock everywhere in EU (Seeed DE warehouse, Reichelt — month+ wait)
- **The Pi Hut (UK):** £39.50 / ~EUR46, 3-5 days to NL, bare board available
- **Antratek NL:** has ESP32S3 variant (EUR72.48) and case variant. No bare board. ESP32 variant works fine in USB mode (ESP32 sits unused).
- **Amazon.de:** check stock/price, 2-4 days
- **OpenELAB:** Munich warehouse, 4-6 days, ~EUR55-65
- No Dutch electronics shops (Kiwi, TinyTronics, Opencircuit, SOS Solutions, etc.) stock the XVF3800

---

### Display Physical Dimensions (For Case Design Reference)

Waveshare 10.1-DSI-TOUCH-A:

- **Overall:** 239 x 147 x 10.8mm
- **Active area:** 217.18 x 135.96mm
- **Bezels:** ~10.91mm left/right, ~5.52mm top/bottom
- **Rear mounting holes:** 218.10 x 133.00mm spacing, M2.5
- **Weight:** 0.74 kg
