# Kiosk Switching Architecture Research

## Decision

**Single-page application (SPA) with an iframe** is the kiosk switching mechanism for FrameLink. The WebRTC video calling client runs in the parent page. Immich Kiosk runs inside an iframe. A CSS toggle switches between slideshow and call modes in <16ms.

## Requirements

The kiosk must switch between two modes — photo slideshow and video call — triggered by a GPIO button press. The switch must be fast (<1 second perceived), reliable, and require zero user interaction. WebRTC connections must remain alive across mode switches (no re-negotiation on every button press). Memory usage must be minimized on the 2GB Pi. The switching mechanism must work under Wayland (labwc) with Chromium in `--kiosk` mode.

## Why SPA with iframe

Three factors made this the clear winner:

1. **WebRTC stays in the foreground page.** The WebRTC connection lives in the parent page, which is always the active/visible page. This eliminates the documented risk of background tab throttling — Chromium throttles timers in background tabs to 1/second, and there are reports of WebRTC streams freezing or stopping entirely when the tab loses focus. With the SPA approach, the WebRTC connection is never in a background tab.

2. **Lowest memory footprint.** A same-origin iframe shares the parent page's Chromium renderer process, saving ~50-100MB compared to two separate tabs (which each get their own renderer process). On a 2GB system, this matters.

3. **Instant switching.** Toggling between modes is a CSS `display` property change — one line of JavaScript, <16ms. No page navigation, no tab switching, no window management. The video elements and iframe are always in the DOM; only their visibility changes.

## Candidates Evaluated

### SPA with iframe -- chosen

```
Parent page (localhost:8888)
+-- Touch shield div (blocks all touch events, z-index: 999)
+-- Immich Kiosk iframe (visible when idle, hidden during call)
+-- CSS Grid of <video> elements (hidden when idle, visible during call)
+-- WebRTC client (always connected, muted when idle)
+-- WebSocket listener (receives GPIO commands from daemon)
```

- **Switch latency**: <16ms
- **Memory**: 300-450 MB (lowest)
- **WebRTC control**: Full (lives in parent page)
- **Works with labwc**: Yes
- **24/7 reliability**: Excellent

### Dual iframes in wrapper page

Both Immich Kiosk and video call page in separate iframes within a parent. Toggle CSS visibility.

**Why not chosen:** If the video call iframe is cross-origin, Chromium creates an Out-of-Process iframe (OOPIF), and the parent page cannot control the WebRTC connection inside it. This breaks programmatic join/leave from the GPIO handler. If same-origin, it works but adds an unnecessary iframe layer around the video calling code that could simply live in the parent page.

### CDP tab switching (two Chromium tabs)

Two tabs in Chromium. A Python/Node script uses Chrome DevTools Protocol (`Target.activateTarget`) to switch between them. WebRTC runs in one tab, Immich Kiosk in the other.

**Why not chosen:** Background tab throttling is a documented risk for WebRTC — streams may freeze or stop when the tab is not focused. Switch latency is 50-150ms with possible white flash during tab transition. Memory is ~50-100MB higher due to two renderer processes. Requires `--remote-debugging-port` flag and an external script for tab management. Under X11, `xdotool` can also switch tabs, but this requires X11 (not Wayland).

### xdotool tab switching (alpha.md approach)

Two Chromium tabs. A Python script uses `xdotool key ctrl+Tab` to switch between them on GPIO press.

**Why not chosen:** Requires X11. The official Raspberry Pi kiosk setup uses Wayland/labwc, where xdotool does not work. The Wayland equivalent (`wtype`) exists but adds fragility. Same background tab throttling risks as CDP tab switching. Same memory overhead of two renderer processes. This approach was proposed in the original alpha analysis but is superseded by the SPA architecture.

### CDP JavaScript injection (single tab)

Single tab showing Immich Kiosk. On button press, inject JavaScript via CDP to overlay video elements on top of the Kiosk page.

**Why not chosen:** Fragile coupling to Immich Kiosk's DOM structure. If Kiosk updates change the DOM, the injection breaks. No clean separation between the two modes.

### Dual Chromium instances with labwc window switching

Two separate Chromium processes. Use `wlrctl toplevel focus` under labwc to swap between them.

**Why not chosen:** Heaviest memory footprint (700-1200 MB — two full Chromium instances). Forces labwc-specific tooling. Requires `--user-data-dir` to avoid profile lock conflicts. Not viable on 2GB.

## SPA Implementation Details

### Mode Toggle (JavaScript)

```javascript
function enableCall() {
    document.getElementById('kiosk-iframe').style.display = 'none';
    document.getElementById('video-grid').style.display = 'grid';
    // Unmute local tracks, subscribe to remote participants
}

function disableCall() {
    document.getElementById('video-grid').style.display = 'none';
    document.getElementById('kiosk-iframe').style.display = 'block';
    // Mute local tracks, optionally unsubscribe
}
```

### Auto-Layout CSS Grid

```css
.video-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(40%, 1fr));
    gap: 4px;
    height: 100vh;
}
```

Automatically adjusts from 1 to 5 tiles as participants join/leave.

### GPIO Integration

A Python daemon using `gpiozero` detects button presses and sends toggle commands to the SPA via a localhost WebSocket:

```
GPIO rising edge -> Python daemon -> WebSocket localhost:8889 -> SPA toggles mode
```

### Immich Kiosk iframe Compatibility

Immich Kiosk is designed for iframe embedding. It does not set restrictive `X-Frame-Options` or CSP `frame-ancestors` headers. It is commonly embedded in Home Assistant dashboards (cross-origin). If the SPA serves from `localhost:8888` and Immich Kiosk from `localhost:3000`, same-origin policy does not apply, but Kiosk's permissive headers allow the embedding. The iframe can optionally be destroyed (removed from DOM) during calls to free ~50-100MB of memory and recreated when the call ends.
