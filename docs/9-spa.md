# Software Build Guide 09 — Kiosk SPA (Shell + LiveKit Client)

Build the custom single-page application that runs in Chromium on each Pi. This is the brain of the terminal — it manages the slideshow iframe, video grid, LiveKit connection, and GPIO commands.


---

## Steps

### 1. Project structure

```
spa/
  index.html        # Main SPA page
  app.js            # Mode toggle, LiveKit client, WebSocket listener
  style.css         # Video grid layout, touch shield
  package.json      # If using npm for livekit-client
```

### 2. Core HTML structure

```html
<!DOCTYPE html>
<html>
<head>
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="style.css">
</head>
<body>
  <!-- Touch shield: blocks all touch events in both modes -->
  <div id="touch-shield"></div>

  <!-- Slideshow mode: Immich Kiosk in iframe -->
  <iframe id="kiosk-iframe" src="http://localhost:3000"></iframe>

  <!-- Call mode: video grid (hidden by default) -->
  <div id="video-grid"></div>

  <script src="app.js"></script>
</body>
</html>
```

### 3. Key implementation tasks

1. **LiveKit client connection** — connect to the LiveKit room at page load with tracks muted. Reconnect on failure with infinite retry.
2. **Track subscription handling** — when a remote participant publishes, attach their video track to a new `<video>` element in the grid. Remove it when they leave.
3. **Mode toggle** — `enableCall()` unhides the video grid, hides the iframe, unmutes local tracks. `disableCall()` reverses this. Optionally destroy/recreate the iframe to free memory.
4. **GPIO WebSocket listener** — listen on `ws://localhost:8889` for toggle commands from the GPIO daemon.
5. **Auto-answer** — when the first remote participant joins while in slideshow mode, automatically switch to call mode (for the "family calls elder" use case).
6. **Touch shield** — a fixed, full-viewport div at z-index 999 that absorbs all touch events. Always active.

### 4. Serve the SPA

Use a lightweight static file server running on the Pi:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
python3 -m http.server 8888 --directory /home/framelink/spa/
```

(Or use Node.js `serve`, caddy, or any static server.) Run as a systemd service that starts before Chromium — see [guide 11 (systemd services & reliability)](11-systemd-and-reliability.md).

### 5. Test locally

Before deploying to the Pi, test the SPA in a desktop browser:

1. Verify Immich Kiosk loads in the iframe
2. Verify LiveKit connection and video grid with test participants
3. Verify mode toggle works
4. Verify touch shield blocks interaction

Then deploy to the Pi and test with Chromium in kiosk mode.

**Checkpoint:** SPA loads in Chromium on the Pi. Slideshow displays. Joining a LiveKit room from another device shows their video. Mode toggle works.
