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
import asyncio
import json
import signal

from gpiozero import Button
import websockets

BUTTON_PIN = 17                       # BCM pin the call button is wired to (button to ground). Match your wiring.
WS_HOST, WS_PORT = "127.0.0.1", 8889

clients = set()
loop = None


async def _handler(ws, path=None):    # path arg kept for older websockets releases
    clients.add(ws)
    try:
        await ws.wait_closed()
    finally:
        clients.discard(ws)


def broadcast(cmd):
    msg = json.dumps({"cmd": cmd})
    for ws in list(clients):
        asyncio.run_coroutine_threadsafe(ws.send(msg), loop)


async def main():
    global loop
    loop = asyncio.get_running_loop()
    loop.add_signal_handler(signal.SIGUSR1, lambda: broadcast("toggle"))
    button = Button(BUTTON_PIN, pull_up=True, bounce_time=0.05)
    button.when_pressed = lambda: broadcast("toggle")
    async with websockets.serve(_handler, WS_HOST, WS_PORT):
        await asyncio.Future()        # serve forever


if __name__ == "__main__":
    asyncio.run(main())
