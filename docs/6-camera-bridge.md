# Software Build Guide 06 — Camera Bridge (v4l2loopback)

Bridge the Pi Camera Module 3 (libcamera) to a standard V4L2 device so Chromium's `getUserMedia()` can see it. Install GStreamer and `v4l2loopback`, load the kernel module persistently, run the GStreamer pipeline, and verify the resulting `/dev/video8` shows up in Chromium.


---

## Steps

1. **Install the camera bridge dependencies:**

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-libcamera v4l2loopback-dkms -y
   ```

2. **Load the `v4l2loopback` module** with a fixed device number and label:

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   sudo modprobe v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1
   ```

3. **Make the module load persistently** on boot with the same options:

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   echo "v4l2loopback" | sudo tee /etc/modules-load.d/v4l2loopback.conf
   sudo tee /etc/modprobe.d/v4l2loopback.conf << 'EOF'
   options v4l2loopback video_nr=8 card_label="Chromium device" exclusive_caps=1
   EOF
   ```

4. **Test the GStreamer pipeline manually** — this reads from the Pi Camera via libcamera and writes frames to `/dev/video8`. The Camera Module 3 is natively landscape (16:9), and our display is rotated to landscape too (see [guide 3 (hardware configuration)](3-hardware-configuration.md) for the TTY and [guide 5 step 5](5-kiosk-base.md) for labwc), so the camera and display orientations match — **no `videoflip`/rotation filter is needed in the pipeline**.

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   gst-launch-1.0 libcamerasrc ! "video/x-raw,width=1280,height=720,framerate=30/1" ! videoconvert ! v4l2sink device=/dev/video8
   ```

   If you later decide to mount the camera module rotated 90° or 180° (for example, ribbon exiting the top instead of the bottom of your 3D-printed case), insert `videoflip method=clockwise` (or `rotate-180` / `counterclockwise`) between `videoconvert` and `v4l2sink`.

5. **Verify the device exists** (in another SSH session):

   ![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

   ```bash
   ls -la /dev/video8
   ```

6. **Verify Chromium sees the camera.** Open `chrome://settings/content/camera` in Chromium (temporarily disable kiosk mode if needed) — "Chromium device" should appear in the dropdown.

**Checkpoint:** `/dev/video8` exists, the GStreamer pipeline runs without errors, and Chromium lists "Chromium device" as an available camera.
