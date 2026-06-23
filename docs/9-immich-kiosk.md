# Software Build Guide 09 — Immich Kiosk (Docker photo slideshow)

The frame's resting screen is a photo slideshow. We run [Immich Kiosk](https://github.com/damongolding/immich-kiosk) — a small web app that pulls photos from your Immich server and displays them full-screen — in Docker on the Pi itself, with offline caching so the slideshow keeps working even when your Immich server is unreachable. This guide installs Docker, has you create a read-only API key on your Immich server so Kiosk can read your photos, writes the Kiosk configuration, and starts it serving the slideshow at `http://127.0.0.1:3000` for the kiosk SPA ([guide 10](10-spa.md)) to embed. Hosting Kiosk locally on the Pi — rather than pointing the frame at a Kiosk running elsewhere — is what makes the offline cache possible: if your Immich server goes down, the Pi still has its own copy of recent photos to show.

---

<a id="1-install-docker-engine"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_Docker_Engine-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install Docker Engine"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A fresh Raspberry Pi OS Lite image has no container runtime, and Immich Kiosk is distributed only as a Docker image. Without Docker there is nothing to run it in.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install Docker Engine using Docker's official install script, then allow the `framelink` user to run Docker without `sudo`.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`get.docker.com` is Docker's official convenience script: it adds Docker's apt repository for Debian and installs `docker-ce`, the CLI, and the Compose plugin in one step — the simplest path for a single Pi. Piping it to `sudo sh` runs it as root, which it needs to add the repo and install packages; re-running it later is safe (it detects an existing install and exits). The second command adds `framelink` to the `docker` group so `docker` and `docker compose` run without `sudo`; that membership only takes effect in a **new** login session, which is why the last thing this step asks you to do is reconnect.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker framelink
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. The install script prints a long progress log ending with the installed Docker version; usermod prints nothing on success.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The script ends without an error and reports an installed version (you can confirm with `docker --version`). `usermod` is silent on success. A `Cannot connect to the Docker daemon` message at this point is expected if you try `docker` immediately — the group membership is not active until you reconnect.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Docker Engine and Compose are installed and the daemon is running. **Log out of this SSH session and reconnect** before the next step, so the `docker` group membership takes effect and you can run `docker` without `sudo`.

<a id="2-create-the-immich-kiosk-configuration"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_Immich_Kiosk_configuration-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the Immich Kiosk configuration"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Immich Kiosk needs permission to read your photos from your Immich server, plus a configuration telling it where that server is and to keep a local copy of recent photos so the slideshow survives the server going offline.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create a read-only API key in your Immich account settings, then write a small Docker Compose file containing your server's address, that key, and the offline-cache settings.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

First, the API key. In the Immich web app, open your user menu (top-right) → **Account Settings** → **API Keys** → **New API Key**, name it `framelink-kiosk`, and copy the value it shows you (you only see it once). Kiosk uses this key to read your library through Immich's API; a key with default permissions is enough for read-only display.

Then the Compose file. Each setting:
1. `image: ghcr.io/damongolding/immich-kiosk:0.39.3` pins an exact, tested version rather than a moving `latest` tag.
2. `ports: "127.0.0.1:3000:3000"` binds Kiosk to localhost only, so the slideshow is reachable by the Pi's own browser but not exposed to the network.
3. `KIOSK_IMMICH_URL` / `KIOSK_IMMICH_API_KEY` point Kiosk at your server and authenticate it.
4. `KIOSK_OFFLINE_MODE_ENABLED: "true"` makes Kiosk download and cache assets into the `offline-assets` volume. (Two settings work together for offline use: this one *downloads and caches*; the kiosk SPA in [guide 10](10-spa.md) then requests the slideshow with `use_offline_mode=true` so Kiosk *serves from that cache* when Immich is unreachable.)
5. `KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS: "200"` caps the cache at 200 photos — plenty for a frame, modest on SD-card space.
6. The container runs as the non-root user `65532`, so the `offline-assets` directory must be owned by that UID or Kiosk cannot write its cache — hence the `chown`.

Set the first two lines below — `IMMICH_URL` and `IMMICH_KEY` — to your own Immich server URL and the API key you just copied, then run the whole block. The values are substituted into the Compose file as it is written; nothing is printed, so your key never appears on screen.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
IMMICH_URL="https://immich.example.com"
IMMICH_KEY="REPLACE_WITH_YOUR_IMMICH_API_KEY"
mkdir -p ~/immich-kiosk/offline-assets
sudo chown -R 65532:65532 ~/immich-kiosk/offline-assets
cat > ~/immich-kiosk/compose.yaml << EOF
services:
  immich-kiosk:
    image: ghcr.io/damongolding/immich-kiosk:0.39.3
    container_name: immich-kiosk
    restart: always
    ports:
      - "127.0.0.1:3000:3000"
    environment:
      KIOSK_IMMICH_URL: "${IMMICH_URL}"
      KIOSK_IMMICH_API_KEY: "${IMMICH_KEY}"
      KIOSK_OFFLINE_MODE_ENABLED: "true"
      KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS: "200"
    volumes:
      - ./offline-assets:/offline-assets
EOF
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. All four commands are silent on success — the directory is created, its owner set to 65532, and the Compose file written, with no terminal output.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

No output and no error means all four commands succeeded. A `chown: invalid user` error means the numeric UID was mistyped (it must be `65532`). If you forgot to change the first two lines, Kiosk will later fail to reach Immich — you can re-run this whole block with the corrected values to overwrite the Compose file.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Immich Kiosk is configured to read your photo library and cache recent photos locally. Nothing is running yet — that is the next step.

<a id="3-start-the-immich-kiosk-container"></a>
<img src="https://img.shields.io/badge/STEP_03-Start_the_Immich_Kiosk_container-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Start the Immich Kiosk container"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The configuration exists but no slideshow is being served. We need Kiosk running, and running again automatically after every reboot.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Start the container with Docker Compose in the background.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`docker compose up -d` reads the Compose file, pulls the pinned image from the registry the first time, and starts the container detached (`-d`, in the background). The `restart: always` policy written into the Compose file tells Docker to relaunch Kiosk after a crash or a reboot, so the slideshow comes back on its own with the rest of the system. On this first start, Kiosk also begins filling the offline cache.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker compose -f ~/immich-kiosk/compose.yaml up -d
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. First run shows image-pull progress lines for ghcr.io/damongolding/immich-kiosk:0.39.3, then "Container immich-kiosk Started".]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The run ends with `Container immich-kiosk Started` (or `Running`). `docker ps` should then list `immich-kiosk` with status `Up`. A registry pull failure means the Pi has no internet path to `ghcr.io`; an immediate exit means a Compose-file syntax error — `docker logs immich-kiosk` will show the reason.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Immich Kiosk is running and set to restart on every boot. It is reachable on the Pi at `http://127.0.0.1:3000`.

<a id="4-confirm-the-slideshow-is-serving"></a>
<img src="https://img.shields.io/badge/STEP_04-Confirm_the_slideshow_is_serving-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Confirm the slideshow is serving"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A running container is not proof of a working slideshow — Kiosk could be up but unable to reach Immich (wrong URL or key). We confirm it is actually serving the page and authenticated to your server before the SPA relies on it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the local Kiosk server for its page and read the container's recent log lines.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`curl` fetches the Kiosk page from `127.0.0.1:3000` and `-o /dev/null -w 'HTTP %{http_code}\n'` discards the HTML and prints just the HTTP status — `200` means Kiosk is serving. `docker logs --tail 20` shows the most recent log lines, where a healthy Kiosk reports connecting to your Immich URL and beginning to cache assets. The kiosk SPA in [guide 10](10-spa.md) embeds this same `http://127.0.0.1:3000` address in a full-screen iframe as the frame's default view.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:3000
docker logs --tail 20 immich-kiosk
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[Pending fresh-flash capture. curl prints "HTTP 200"; docker logs shows Kiosk's startup lines, including a successful connection to the configured Immich URL and offline-cache activity.]
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`HTTP 200` from `curl`, and log lines showing a successful connection to your Immich server. An HTTP `401` or `403`, or a log line about authentication, means the API key or URL is wrong — fix the first two lines in [step 2](#2-create-the-immich-kiosk-configuration), re-run that block, then `docker compose -f ~/immich-kiosk/compose.yaml up -d` again to recreate the container with the corrected values.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The slideshow is live at `http://127.0.0.1:3000` on the Pi and caching photos for offline display. The kiosk SPA built in [guide 10](10-spa.md) will embed it as the default screen, and if your Immich server later goes offline the cached photos keep the frame showing pictures.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`docker ps` lists `immich-kiosk` as `Up`, `curl http://127.0.0.1:3000` returns HTTP `200`, the container's logs show it connected to your Immich server, the `~/immich-kiosk/offline-assets` directory is filling with cached photos, and the container is set to restart on boot. When the SPA from [guide 10](10-spa.md) loads, this slideshow fills the screen; when your Immich server is unreachable, the cached photos keep it running.
