#!/usr/bin/env python3
# FrameLink GPIO button daemon.
#
# Watches the physical call button on a GPIO pin and pushes a {"cmd":"toggle"}
# message to the kiosk SPA over a localhost WebSocket. The SPA (app/control.js)
# is the WebSocket *client*; this daemon is the *server* on 127.0.0.1:8889.
#
# The button press fires in gpiozero's own thread, while the WebSocket clients
# live in the asyncio event loop — so the press callback schedules the send back
# onto the loop with run_coroutine_threadsafe (calling loop methods directly from
# the gpiozero thread would raise "no running event loop").
#
# To simulate a press without the hardware (useful for testing and in guide 11):
#   systemctl --user kill -s SIGUSR1 framelink-gpio.service
#
# The channel is two-way: the app reports {"event":"call-end"} after each hangup and
# this daemon then restarts framelink-camera.service. The camera node's provide-mode
# stream wedges after a few acquire/release cycles (measured: third acquisition on the
# same node instance fails), so every call gets a freshly started node.
#
# The connection doubles as a liveness signal for the kiosk page itself. A healthy
# SPA always holds this socket; if the page dies — a renderer killed by the OOM
# killer leaves an "Aw, Snap!" tab while chromium-kiosk.service still reports
# active, so systemd's Restart= never fires — the socket drops and stays down. The
# kiosk watchdog below restarts the browser after a sustained silence. Restarting
# the kiosk is deliberately the FIRST recovery action anywhere in the frame: it
# frees the renderer's memory, and on a memory-starved system nothing else can run
# until that happens (measured during the August 2026 leak incident).
import asyncio
import json
import signal
import subprocess
import time

from gpiozero import Button
import websockets

BUTTON_PIN = 17                       # BCM pin the call button is wired to (button to ground). Match your wiring.
WS_HOST, WS_PORT = "127.0.0.1", 8889

KIOSK_DISCONNECT_RESTART_S = 90       # SPA socket absent this long -> restart the kiosk
KIOSK_RESTART_COOLDOWN_S = 300        # never restart more often than this (loop guard)
KIOSK_CHECK_INTERVAL_S = 15

clients = set()
loop = None
last_client_seen = 0.0
last_kiosk_restart = 0.0


def _on_app_event(event):
    if event == "call-end":
        subprocess.Popen(["systemctl", "--user", "restart", "framelink-camera.service"])


async def _handler(ws, path=None):    # path arg kept for older websockets releases
    clients.add(ws)
    try:
        async for raw in ws:
            try:
                _on_app_event(json.loads(raw).get("event"))
            except (ValueError, AttributeError):
                pass
    finally:
        clients.discard(ws)


def broadcast(cmd):
    msg = json.dumps({"cmd": cmd})
    for ws in list(clients):
        asyncio.run_coroutine_threadsafe(ws.send(msg), loop)


async def kiosk_watchdog():
    global last_client_seen, last_kiosk_restart
    while True:
        await asyncio.sleep(KIOSK_CHECK_INTERVAL_S)
        now = time.monotonic()
        if clients:
            last_client_seen = now
            continue
        if now - last_client_seen < KIOSK_DISCONNECT_RESTART_S:
            continue
        if now - last_kiosk_restart < KIOSK_RESTART_COOLDOWN_S:
            continue
        print(f"kiosk-watchdog: no SPA connection for {int(now - last_client_seen)}s, "
              "restarting chromium-kiosk", flush=True)
        last_kiosk_restart = now
        last_client_seen = now        # fresh grace period for the restarted kiosk
        subprocess.Popen(["systemctl", "--user", "restart", "chromium-kiosk.service"])


async def main():
    global loop, last_client_seen
    loop = asyncio.get_running_loop()
    last_client_seen = time.monotonic()   # daemon start grants the kiosk one grace window
    loop.add_signal_handler(signal.SIGUSR1, lambda: broadcast("toggle"))
    button = Button(BUTTON_PIN, pull_up=True, bounce_time=0.05)
    button.when_pressed = lambda: broadcast("toggle")
    watchdog_task = asyncio.create_task(kiosk_watchdog())   # reference kept: bare tasks can be GC'd
    async with websockets.serve(_handler, WS_HOST, WS_PORT):
        await asyncio.Future()        # serve forever
    await watchdog_task               # unreachable; silences the unused-variable lint


if __name__ == "__main__":
    asyncio.run(main())
