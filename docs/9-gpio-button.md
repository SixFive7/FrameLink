# Software Build Guide 09 — GPIO Button Daemon

Wire up the physical call button to a Python daemon that watches the GPIO pin and sends a toggle command to the SPA over a localhost WebSocket.


---

## Steps

### 1. Install dependencies via apt

Trixie enforces PEP 668, so avoid `pip install` in the system environment — use the apt-packaged versions:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
sudo apt install python3-gpiozero python3-lgpio python3-websockets -y
```

`python3-lgpio` is required on Trixie — gpiozero's default `LGPIOFactory` backend will silently fall back to a mock factory if it's missing, and the button will never fire.

### 2. Write the daemon

`/home/framelink/gpio-daemon.py`:

```python
from gpiozero import Button
import asyncio
import websockets

BUTTON_PIN = 17  # Adjust to your wiring
WS_URL = "ws://localhost:8889"

# ... button press detection -> WebSocket send "toggle"
```

### 3. Create the systemd service

`/etc/systemd/system/gpio-daemon.service`:

```ini
[Unit]
Description=FrameLink GPIO daemon
After=network.target

[Service]
ExecStart=/usr/bin/python3 /home/framelink/gpio-daemon.py
Restart=always
RestartSec=5
User=framelink

[Install]
WantedBy=multi-user.target
```

Enable and start:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
sudo systemctl enable --now gpio-daemon
```

**Checkpoint:** Pressing the GPIO button sends a `toggle` message over `ws://localhost:8889`, and the SPA switches between slideshow and call modes in response.
