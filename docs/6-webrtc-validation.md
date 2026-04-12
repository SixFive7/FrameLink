# Software Build Guide 06 — WebRTC Hardware Validation

> **Purpose:** Prove that the 2 GB Pi 5 can handle 5 incoming WebRTC video streams before writing any production code. This is the single highest risk in the project. If this step fails, either switch to Pi 5 4GB or reduce the maximum participant count.


---

## Steps

The validation can be run against a public WebRTC test service (Option A, quick but noisy) or against a local development LiveKit server (Option B, recommended — matches production).

### Option A: Public WebRTC test service

1. Open a multi-party WebRTC test page in Chromium on the Pi and join from 5 other devices (phones, laptops) sending 480p video.

2. In an SSH session, monitor RAM and CPU while the call runs:

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   watch -n 2 'free -m && echo "---" && top -bn1 | head -20'
   ```

### Option B: Local LiveKit dev server (recommended)

1. On your Docker host, start a dev LiveKit server:

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   docker run --rm -p 7880:7880 -p 7881:7881 -p 50000-50020:50000-50020/udp \
     -e LIVEKIT_KEYS="devkey:secret" \
     livekit/livekit-server --dev
   ```

2. Install `livekit-cli` on your workstation and generate six tokens for room `test` (one for the Pi, five for other devices):

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   livekit-cli create-token --api-key devkey --api-secret secret --join --room test --identity pi-01
   livekit-cli create-token --api-key devkey --api-secret secret --join --room test --identity phone-01
   # ... repeat for phone-02 through phone-05
   ```

3. Join the room from the Pi and 5 other devices at 480p. Let the call run for **4 hours minimum** while monitoring the Pi as in Option A.

## Pass criteria

- Total RAM stays below 1.5 GB
- CPU stays below 80% average
- No OOM kills
- No Chromium crashes
- Video remains smooth (no persistent freezing)

## If it fails

Try 360p or 240p. Try 3 participants instead of 5. If nothing works at 2 GB, the project needs Pi 5 4GB units.
