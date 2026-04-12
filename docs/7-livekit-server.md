# Software Build Guide 07 — LiveKit Server Deployment

Deploy the production LiveKit SFU on the Docker host. Write `docker-compose.yml` and `livekit.yaml`, stand up a minimal JWT token service, put the WebSocket endpoint behind the existing nginx reverse proxy with SSL, and expose the TURN/UDP ports directly. Verify end-to-end with `livekit-cli`, including a TURN path from a network that cannot reach the server directly.


---

## Steps

### 1. LiveKit server

Create the project directory on your Docker host:

```
server/
  docker-compose.yml
  livekit.yaml
  token-service/
    Dockerfile
    server.js (or server.py)
```

`docker-compose.yml`:

```yaml
services:
  livekit:
    image: livekit/livekit-server:latest
    ports:
      - "7880:7880"
      - "7881:7881"
      - "443:443/udp"
      - "50000-50100:50000-50100/udp"
    volumes:
      - ./livekit.yaml:/etc/livekit.yaml
    command: --config /etc/livekit.yaml
    restart: unless-stopped
```

`livekit.yaml`:

```yaml
port: 7880
rtc:
  port_range_start: 50000
  port_range_end: 50100
  use_external_ip: true
  tcp_port: 7881
turn:
  enabled: true
  udp_port: 443
  tls_port: 5349
keys:
  your-api-key: your-api-secret
```

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
docker compose up -d
```

### 2. Token service

LiveKit requires JWT tokens for client authentication. Build a minimal token generation service that issues tokens for known device identities. This can be a simple HTTP endpoint:

```
GET /token?identity=framelink-01&room=family
-> returns JWT token
```

Secure this endpoint (e.g., behind Authelia, or with a shared secret). Each Pi will request a token at boot.

### 3. Reverse proxy & SSL

Put LiveKit behind your existing nginx reverse proxy with SSL. LiveKit's WebSocket endpoint (`/`) needs to be proxied with WebSocket upgrade support. The TURN/UDP ports must be exposed directly (cannot be proxied).

### 4. Verify the server

From any machine, test the HTTP API:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
curl http://your-server:7880
```

Test WebSocket connectivity — use livekit-cli to join a room and verify it works:

![RUN](https://img.shields.io/badge/👤-RUN-blue?style=flat-square)

```bash
livekit-cli join-room --url ws://your-server:7880 --api-key your-api-key --api-secret your-api-secret --room test --identity test-user
```

**Checkpoint:** LiveKit server is running. You can join a room from `livekit-cli`. TURN is working (test from a network that requires TURN — e.g., a phone on mobile data).
