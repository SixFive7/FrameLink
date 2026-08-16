# Software Build Guide 07 — LiveKit Server Deployment

Stand up the [LiveKit](https://github.com/livekit/livekit) server that carries every FrameLink video call. **The SSH target in this guide is not the Pi**: steps 1–4 run on the *server* — an always-on Linux machine on your home network that will host LiveKit — and steps 5–6 run on your Windows workstation in Git Bash. You install Docker on the server, write LiveKit's configuration with a freshly generated API secret, start the pinned `livekit/livekit-server` container, and confirm it answers; then, from the workstation, you install the LiveKit CLI, prove it can reach the server across the network, and mint the long-lived access token this frame will use to join calls. At the end the server is reachable on your home network at `ws://YOUR-SERVER:7880` — no domain name, TLS certificate, or internet exposure required — ready for [guide 10 step 2](10-spa.md#2-create-the-app-configuration) to consume the URL and token.

---

<a id="1-install-docker-engine-on-the-server"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_Docker_Engine_on_the_server-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install Docker Engine on the server"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

LiveKit is distributed as a Docker image, and the machine that will host it may not have Docker yet. Without Docker there is nothing to run the video-call server in.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Connect over SSH to the machine that will host LiveKit — the server, not the Pi — and install Docker Engine using Docker's official install script, then allow your server user to run Docker without `sudo`.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Every command in steps 1 through 4 runs in an SSH session on the LiveKit server. `get.docker.com` is Docker's official convenience script: it adds Docker's apt repository and installs `docker-ce`, the CLI, and the Compose plugin in one step. Piping it to `sudo sh` runs it as root, which it needs to add the repo and install packages; re-running it later is safe (it detects an existing install and exits). The second command adds your server login user (`$USER` expands to whatever name you are logged in as) to the `docker` group so `docker` and `docker compose` run without `sudo`; that membership only takes effect in a **new** login session, which is why the last thing this step asks you to do is reconnect.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The install script prints a long progress log ending with the installed Docker Engine version, and usermod prints nothing on success.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The script ends without an error and reports an installed version (you can confirm with `docker --version`). `usermod` is silent on success. If the server already had Docker, the script says so and exits without changing anything — that is fine. A `Cannot connect to the Docker daemon` message at this point is expected if you try `docker` immediately — the group membership is not active until you reconnect.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Docker Engine and Compose are installed on the server and the daemon is running. **Log out of this SSH session and reconnect to the server** before the next step, so the `docker` group membership takes effect and you can run `docker` without `sudo`.

<a id="2-create-the-livekit-configuration"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_LiveKit_configuration-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the LiveKit configuration"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

LiveKit needs to know which network ports to use, and it needs a master credential — an API key and secret — that every tool and token in the rest of this build will be checked against. Neither exists yet.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Still over SSH on the server, generate a strong random secret and write LiveKit's two files — its configuration and the Docker Compose file that runs it — in one block, then print the finished configuration so you can copy the key and secret somewhere safe.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The four commands:

1. `mkdir -p ~/livekit` creates the folder both files live in.
2. The guarded `printf` line writes `~/livekit/livekit.yaml` **only if it does not exist yet**, filling in a fresh secret from `openssl rand -base64 32` (44 random characters — LiveKit requires at least 32). The guard is not the usual re-run cosmetics: without it, a re-run would mint a *new* secret and silently invalidate every credential already derived from the old one — the workstation CLI settings in step 5 and every frame token from step 6. An existing `livekit.yaml` is therefore never touched.
3. The heredoc writes `~/livekit/compose.yaml` describing the container. It is rewritten with identical content on every run, so re-running is harmless.
4. `cat` prints the finished `livekit.yaml` back so you can record the key and secret.

Inside `livekit.yaml`: `port: 7880` is the WebSocket-and-HTTP port — the `ws://YOUR-SERVER:7880` address every frame, phone, and CLI dials; `tcp_port: 7881` is the fallback path media takes when UDP is blocked; `port_range_start`/`port_range_end` reserve UDP ports 50000–50100, where call audio and video actually flow; `use_external_ip: false` makes the server advertise its home-network address instead of asking the internet for a public one — correct for a LAN deployment; and `keys` pairs the API key `framelink` (an identifier, not a secret — it stays the same on every deployment) with your generated secret, the value that signs every access token.

Inside `compose.yaml`: the image is pinned to the exact tested version `v1.13.3` rather than a moving `latest` tag; `container_name: livekit` gives the container a stable name for `docker logs`; `restart: unless-stopped` brings LiveKit back after a crash or a server reboot; the three `ports` entries publish exactly the ports `livekit.yaml` claims; and the volume mounts the configuration into the container where the `--config` flag reads it.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/livekit
[ -f ~/livekit/livekit.yaml ] || printf 'port: 7880\nrtc:\n  tcp_port: 7881\n  port_range_start: 50000\n  port_range_end: 50100\n  use_external_ip: false\nkeys:\n  framelink: "%s"\n' "$(openssl rand -base64 32)" > ~/livekit/livekit.yaml
cat > ~/livekit/compose.yaml << 'EOF'
services:
  livekit:
    image: livekit/livekit-server:v1.13.3
    container_name: livekit
    restart: unless-stopped
    ports:
      - "7880:7880"
      - "7881:7881"
      - "50000-50100:50000-50100/udp"
    volumes:
      - ./livekit.yaml:/etc/livekit.yaml
    command: --config /etc/livekit.yaml
EOF
cat ~/livekit/livekit.yaml
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The mkdir and the two file writes are silent, then the final cat echoes the eight-line livekit.yaml back with the generated secret in the keys line — the secret will be redacted in the capture.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `cat` at the end must show the eight configuration lines, ending with `framelink:` followed by a 44-character quoted secret. **Copy the key (`framelink`) and that secret somewhere safe now** — you type both into the workstation in step 5. If you re-run this step later and the secret shown is the old one, that is the guard protecting your existing credential — keep using it.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The server now has a complete LiveKit configuration and a master API credential recorded in your notes. Nothing is running yet — that is the next step.

<a id="3-start-the-livekit-server"></a>
<img src="https://img.shields.io/badge/STEP_03-Start_the_LiveKit_server-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Start the LiveKit server"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The configuration exists but no video-call server is running. We need LiveKit up now, and up again automatically after every server reboot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Start the container with Docker Compose in the background, still over SSH on the server.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`docker compose up -d` reads the Compose file, pulls the pinned `livekit/livekit-server:v1.13.3` image from Docker Hub the first time, and starts the container detached (`-d`, in the background). The `restart: unless-stopped` policy written into the Compose file tells Docker to relaunch LiveKit after a crash or a reboot, so the call server comes back on its own unless you deliberately stopped it. Re-running the command against an already-running container reports it as up to date and changes nothing.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker compose -f ~/livekit/compose.yaml up -d
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. First run shows image-pull progress lines for livekit/livekit-server:v1.13.3, then "Container livekit Started".]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The run ends with `Container livekit Started` (or `Running`). `docker ps` should then list `livekit` with status `Up`. A registry pull failure means the server has no internet path to Docker Hub. If the container starts and immediately exits, the configuration was rejected — `docker logs livekit` shows the reason (for example a `keys` secret shorter than 32 characters).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The LiveKit server is running on your server and set to restart on every boot. Whether it is actually answering is confirmed next.

<a id="4-confirm-the-server-is-answering"></a>
<img src="https://img.shields.io/badge/STEP_04-Confirm_the_server_is_answering-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Confirm the server is answering"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A running container is not proof of a working call server — LiveKit could have started and then choked on its configuration. We confirm it is really serving before anything else depends on it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the server for a response on LiveKit's port and read the container's recent log lines, still over SSH on the server.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`curl` fetches from `127.0.0.1:7880` — the same port that carries the WebSocket connections — and `-o /dev/null -w 'HTTP %{http_code}\n'` discards the body and prints just the HTTP status; `200` means LiveKit is up and answering. `docker logs --tail 20` shows the most recent log lines, where a healthy LiveKit reports its version and the ports it bound at startup. This proves the server works locally; that it is reachable from *other* machines on your network is proven from the workstation in the next step.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:7880
docker logs --tail 20 livekit
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. curl prints "HTTP 200", and the log tail shows LiveKit's startup lines including the server version and the ports it bound.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`HTTP 200` from `curl`, and startup log lines with no `error` entries. A `Connection refused` from `curl` means the container is not running — go back to [step 3](#3-start-the-livekit-server) and check `docker logs livekit` for the startup failure.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

LiveKit is serving on port 7880 of your server. The server-side work is done — everything from here on happens on your Windows workstation.

<a id="5-connect-the-workstation-cli-to-the-server"></a>
<img src="https://img.shields.io/badge/STEP_05-Connect_the_workstation_CLI_to_the_server-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Connect the workstation CLI to the server"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The server answers on its own machine, but nothing has yet proven that *other* devices on your network can reach it — and you have no tool anywhere that can create the access passes frames need to join calls.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

This step runs on your **Windows workstation in a Git Bash window — not over SSH**. Install the LiveKit command-line tool, tell it where the server is and what the credentials are, and ask it to list the server's rooms as a reachability test.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The LiveKit CLI (`lk`) is the administration tool for a LiveKit server; `winget` installs it on Windows. The three `export` lines configure it through environment variables: `LIVEKIT_URL` is the server's address — replace `<your-livekit-server>` with the server's LAN IP address or hostname (for example `ws://10.20.30.250:7880`; this exact value later goes into the frame's `livekitUrl` field in [guide 10 step 2](10-spa.md#2-create-the-app-configuration)) — and `LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET` are the key `framelink` and the secret you copied from the `cat` in [step 2](#2-create-the-livekit-configuration). `lk room list` then calls the server's API across the network using those credentials, so one short command proves both reachability and authentication in a single pass. The exports live only in this Git Bash window, so step 6 must run in the **same window**.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
winget install --id LiveKit.LiveKitCLI --accept-source-agreements --accept-package-agreements
export LIVEKIT_URL="ws://<your-livekit-server>:7880"
export LIVEKIT_API_KEY="framelink"
export LIVEKIT_API_SECRET="<your-api-secret>"
lk room list
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. winget reports the CLI installed, the three export lines print nothing, and lk room list returns an empty listing because no room exists yet.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `winget` line finishes with "Successfully installed" (or notes the package is already present — run `lk version` in a fresh window if the `lk` command is not found straight after installing). `lk room list` returning to the prompt without an error is the pass — an empty listing is correct, since no room has been created yet. A connection refused or timeout means the address is wrong or a firewall on the server is blocking TCP port 7880; an authentication error means the key or secret does not match what step 2's `cat` showed.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Your workstation can reach the LiveKit server across the network with valid credentials. The same tool now mints the frame's own access pass — keep this Git Bash window open.

<a id="6-mint-a-long-lived-token-for-the-frame"></a>
<img src="https://img.shields.io/badge/STEP_06-Mint_a_long--lived_token_for_the_frame-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Mint a long-lived token for the frame"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The frame must prove to the server that it is allowed to join your family's video room, but it must never hold the master API secret itself. It needs its own limited, personal pass.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Still on the workstation, in the same Git Bash window as step 5, create a long-lived access token tied to this frame's name and your family room.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

An access token is a signed pass (a JWT) that the server verifies against the API secret — the frame presents the token and never sees the secret. `lk token create` reads the server address and credentials from the environment variables exported in [step 5](#5-connect-the-workstation-cli-to-the-server). `--join --room family --identity framelink-douwe` grants exactly one right: joining the room `family` as the participant `framelink-douwe`, with the default permissions to publish its camera and microphone and subscribe to the other callers. These two values must match what you will put in the frame's `room` and `identity` fields in [guide 10 step 2](10-spa.md#2-create-the-app-configuration) — every device in the household shares the one room name, and each frame gets its own unique identity. For every additional frame you deploy in [guide 13](13-multi-device-deploy.md), repeat this command with that unit's own identity.

`--valid-for 87600h` makes the token valid for ten years, and that number is a deliberate, hardware-taught decision, not a shrug. A frame on a relative's wall must never need credential maintenance on a schedule: during validation, a token that silently aged out took the frame from "working" to "degrading on every boot" with nothing on screen to say why — the app retried the dead credential forever, and (before the app learned better) that retry loop itself destabilised the frame. The app now backs off gently and logs clearly when a token is rejected, but the real protection is a token that outlives the hardware. The trade-off is honest: a stolen token stays valid for ten years too. It is confined to joining this one room on a server that is only reachable inside your home network, and if it ever leaks, rotating the API secret in `~/livekit/livekit.yaml` (and re-minting every frame's token) revokes it instantly.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
lk token create --join --room family --identity framelink-douwe --valid-for 87600h
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending capture. The command prints a summary of the token's grants and one long access token starting with "eyJ" — the token will be redacted in the capture.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

One long unbroken string starting with `eyJ` — that is the token. **Copy it now and store it like a password**: it grants entry to your family's video room, and [guide 10 step 2](10-spa.md#2-create-the-app-configuration) pastes it into the frame's `config.json` and nowhere else. An error saying the api-key or api-secret is missing means this Git Bash window lost step 5's exports (or is a new window) — re-run the three `export` lines from [step 5](#5-connect-the-workstation-cli-to-the-server) and mint again.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

This frame now has a decade-long pass into the family video room, saved in your notes alongside the server URL. Nothing is on the Pi yet — guide 10 is where the URL and token are configured into the frame itself.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

On the server, `docker ps` lists the `livekit` container as `Up` and `curl http://127.0.0.1:7880` returns HTTP `200`, and the container restarts on its own after a server reboot. From the Windows workstation, `lk room list` reaches the server at `ws://YOUR-SERVER:7880` with the `framelink` API key and returns without error, and a long-lived access token for this frame's identity in room `family` is minted and stored somewhere safe. [Guide 10 step 2](10-spa.md#2-create-the-app-configuration) will consume the URL and token.
