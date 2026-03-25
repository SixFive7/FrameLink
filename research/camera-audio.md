# Camera & Audio Research

## Decision

The **Pi Camera Module 3** is used with a **v4l2loopback + GStreamer bridge** to make it accessible to Chromium's `getUserMedia()` API. A **USB speakerphone** provides microphone input and speaker output.

## The Problem

Chromium accesses cameras via the V4L2 (Video4Linux2) API using `getUserMedia()`. The Pi Camera Module 3 (IMX708) uses `libcamera`, which is a separate camera stack that Chromium does not support. Calling `getUserMedia()` with only the Camera Module 3 connected returns "device not found."

This affects all WebRTC solutions equally — it is a Pi platform constraint, not a limitation of any specific video calling library.

## Camera Bridge Solution

A GStreamer pipeline captures from `libcamerasrc` and outputs to a virtual V4L2 device created by `v4l2loopback`. Chromium sees this virtual device as a standard webcam.

### Installation

```bash
sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-libcamera v4l2loopback-dkms
```

### Kernel Module Configuration

File: `/etc/modprobe.d/v4l2loopback.conf`

```
options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1
```

`exclusive_caps=1` is required for Chromium to detect the device as a camera (it makes the device report capture-only capabilities).

### GStreamer Pipeline (systemd service)

```bash
gst-launch-1.0 libcamerasrc \
  ! "video/x-raw,width=1280,height=720,framerate=30/1" \
  ! videoconvert \
  ! v4l2sink device=/dev/video8
```

This runs as a systemd service that starts at boot before Chromium.

### Encoding Reality

**Pi 5 has no hardware H.264 encoder.** The BCM2712 SoC dropped both H.264 encode and decode from the hardware. The only hardware codec on Pi 5 is HEVC (H.265) decode. WebRTC uses VP8, VP9, or H.264 — none of which have hardware acceleration on Pi 5.

All video encoding for the outgoing stream is **software-only** on the quad Cortex-A76 cores (2.4 GHz). At 480p30, software VP8 encoding is expected to use ~10-15% of one core. At 720p30, ~20-30%. Using 480p for the outgoing stream is recommended to reduce CPU load.

### Alternative: USB Webcam

A USB webcam works natively in Chromium with zero bridge or workaround. If the v4l2loopback bridge proves unstable or too resource-heavy, a USB webcam is the simplest fallback. Trade-off: lower image quality than the Camera Module 3, and an additional USB device.

## Audio

### The Problem

The Pi Camera Module 3 has **no microphone**. A separate audio input device is required.

### Solution: USB Speakerphone

A USB speakerphone provides both microphone input and speaker output in a single device. It appears as a standard ALSA audio device and works natively with Chromium's `getUserMedia()` — no bridge needed.

Recommended options:
- **Jabra Speak 410** — widely used for conferencing, good echo cancellation
- **Any USB speakerphone** — the specific model is not critical; any USB audio device with a mic and speaker works

### Audio Configuration

Chromium selects the default ALSA audio device. If multiple audio devices are present (HDMI, USB, headphone jack), set the USB speakerphone as default:

```bash
# List audio devices
aplay -l
arecord -l

# Set default in /etc/asound.conf or ~/.asoundrc
```

Alternatively, the SPA can specify the audio device by deviceId in the `getUserMedia()` constraints.

### DSI Display Audio

The 10.1" DSI display does not have built-in speakers. HDMI audio output is not available (display connects via DSI, not HDMI). The USB speakerphone is the only practical audio output for this hardware configuration.
