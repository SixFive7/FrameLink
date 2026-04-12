# Software Build Guide 12 — Multi-Device Deployment

Scale from one working prototype to the full household rollout. Capture a golden SD-card image (or an Ansible playbook), flash and configure per-device identity, boot each unit, run a multi-device soak test, then deploy to each household.


---

## Steps

### 1. Create a golden image

Once the prototype Pi is fully working:

1. Disable overlayfs temporarily if enabled.
2. Clean up any test data, SSH keys, logs.
3. Use `sudo dd` or [Pi Imager](https://www.raspberrypi.com/software/) to capture the SD card image.
4. Alternatively, script the entire setup with a shell script or Ansible playbook for reproducibility.

### 2. Per-device configuration

Each Pi needs a unique identity:

| Setting          | Per-device                                       |
| ---              | ---                                              |
| Hostname         | `framelink-01` through `framelink-06`            |
| LiveKit identity | `framelink-01` through `framelink-06`            |
| WiFi             | May vary per household                           |
| GPIO pin         | Same across all units (unless wiring differs)    |

Store the device identity in a config file (e.g., `/home/framelink/config.json`) that the SPA reads at startup to request the correct token.

### 3. Flash and test each unit

1. Flash the golden image to each SD card.
2. Boot each Pi, set hostname and device identity.
3. Join all units to a test call simultaneously.
4. Verify: video grid shows all participants, audio works, GPIO toggles mode.
5. Run a multi-device soak test overnight.

### 4. Deploy to households

1. Pre-configure WiFi for each household.
2. Test connectivity to the LiveKit server from each household's network.
3. Place the unit, plug in power, verify it boots into slideshow.
4. Test a call between two units across different households.

**Checkpoint:** All units boot into slideshow, join calls reliably, and survive 24-hour unattended operation in their final locations.

---

## Phase Summary (reference)

| Guide                                                              | What                                            | Estimated effort | Depends on                 |
| ---                                                                | ---                                             | ---              | ---                        |
| [02 SD flash & first boot](2-sd-flash-first-boot.md)               | Get Pi online with Trixie Lite + base packages  | 0.5 day          | Hardware guide complete    |
| [03 Hardware configuration](3-hardware-configuration.md)           | DSI display + kernel parameters                 | 0.5 day          | 02                         |
| [04 Audio configuration](4-audio-configuration.md)                 | XVF3800 pinning, amp enable, AEC tuning         | 0.5-1 day        | 03                         |
| [05 Kiosk base](5-kiosk-base.md)                                   | labwc + Chromium fullscreen                     | 0.5 day          | 04                         |
| [06 Camera bridge](6-camera-bridge.md)                             | v4l2loopback + libcamera pipeline               | 0.5 day          | 05                         |
| [07 WebRTC hardware validation](7-webrtc-validation.md)            | Prove 2 GB can handle 5-way call (go/no-go)     | 2-3 days         | 06                         |
| [08 LiveKit server](8-livekit-server.md)                           | LiveKit + token service + SSL                   | 1 day            | 07 pass                    |
| [09 SPA](9-spa.md)                                                 | Build the kiosk shell + LiveKit client          | 3-5 days         | 08                         |
| [10 GPIO button daemon](10-gpio-button.md)                         | Python gpiozero daemon                          | 0.5 day          | 09                         |
| [11 systemd & reliability](11-systemd-and-reliability.md)          | Services, watchdog, SD protection, restart      | 1-2 days         | 10                         |
| [12 Multi-device deploy](12-multi-device-deploy.md)                | Scale to all units                              | 1-2 days         | 11                         |

Total estimated: ~10-15 days of focused work, assuming the hardware validation gate (guide 07) passes.
