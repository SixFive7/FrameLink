# Software Build Guide 14 - Fleet Manager Deployment

Put the FrameLink Fleet Manager into production: build its container image on your workstation, ship it to your always-on Docker host, and run it there as a Portainer stack published at `https://framelink.huisman.io` through your existing Traefik, with the call server supervised inside it and alerting wired to Home Assistant. **This guide is for later, and nothing in it is a pending action.** Deployment onto the server waits until a release has been cut; until then the Fleet Manager runs on the workstation under Docker, and that is [guide 15](15-local-fleet-manager.md), the guide in daily use, and the one to read first. What follows is the procedure kept ready for the day the server deployment happens, with its reasoning intact so that none of it has to be worked out twice. Unlike every guide before it, **the target is your server, not the frame**: steps 2 and 3 run on your Windows workstation in Git Bash, and every other step runs over SSH on the Docker host. Five things must be true on that day before step 1: the name resolves to that host, Traefik is running there with a working certificate resolver, Authelia is available, Portainer manages that Docker endpoint, and the `/24` question of step 5 has been answered. Both candidates are already computed in `deploy/fleet-manager/stack.env.example`, so answering it is a minute's work rather than an investigation. Every value this guide takes from the server's own configuration (the entrypoint names, the certificate resolver, the reverse proxy's address, the firewall schema) was true when it was written and must be re-checked against that configuration on the day, because an estate moves and a stale value that looks confident is worse than a blank. **About the captured output:** every EXPECTED OUTPUT block below is real output from a real Docker host running exactly these files, but that host is the author's workstation, not the server, so image digests, container ids, generated keys, LiveKit node ids, the pinned container address and timestamps will differ line for line. Where a value must match rather than merely resemble, LOOK FOR says so.

---

<a id="1-create-the-volume-that-must-outlive-the-stack"></a>
<img src="https://img.shields.io/badge/STEP_01-Create_the_volume_that_must_outlive_the_stack-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 - Create the volume that must outlive the stack"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything the Fleet Manager knows about your frames (which ones you adopted, what you named them, and the secret that lets them make calls) lives in one small file. If that file is ever deleted, every frame in the house becomes a stranger the server has never met.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create the storage for that file **by hand, once, before anything else exists**, so that no later mistake with the stack can take it away with it.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The stack file declares this volume `external: true`, which means Compose will never create it, never adopt it and, the part that matters, never remove it. `docker compose down -v`, a Portainer "remove the stack" with the delete-volumes box ticked, and a stack redeployed under a different name all leave an external volume exactly where it was. The cost of that protection is this one command; everything else in the deployment is disposable and this is not.

What it holds is worth being precise about, because it is not simply "data". Identity in FrameLink is the keypair the agent generates on its own first boot, and adoption is the act of binding that key to a record ([guide 13](13-multi-device-deploy.md) covers the fleet side of the same idea). A Fleet Manager that has lost the record has not lost a row; it has lost the binding, and every frame it has ever adopted reappears in the pending queue as an unknown device that has to be adopted again by hand. The volume also carries the LiveKit API secret every call token in the fleet is signed with, and the `livekit-server` binary the server supervises.

`docker volume create` is idempotent by construction: run it twice and the second run prints the same name and changes nothing, which is why it is safe to put at the top of a procedure somebody may repeat.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker volume create framelink-data
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
framelink-data
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

One line, the volume's name, and nothing else. If Docker answers `Error response from daemon: create framelink-data: volume name must be ...` you have typed a character Docker will not accept in a name. If it prints the name on a second run too, that is correct and not a warning: the command is a no-op against a volume that already exists.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The one piece of storage that must survive every future mistake now exists, and it exists independently of the stack that is about to use it.

<a id="2-build-the-fleet-manager-image"></a>
<img src="https://img.shields.io/badge/STEP_02-Build_the_Fleet_Manager_image-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 - Build the Fleet Manager image"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Fleet Manager is source code in a repository, and a server can only run a finished, packaged program.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Run one script on your workstation. It compiles the server, packs it into a container image, starts that image, and refuses to call the build a success until the thing it built actually answers.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`deploy/fleet-manager/build-image.sh` drives a two-stage Docker build. The first stage is the .NET SDK image plus the Native AOT toolchain (the same `clang` / `zlib1g-dev` / `binutils` set the agent's build image installs), and it publishes the server as a single self-contained ELF. The second stage is `debian:trixie-slim` plus four packages: `ca-certificates` for HTTPS, `e2fsprogs` and `mtools` for the SD-image generator, and `curl` for the container's own health check. There is no .NET runtime in the delivered image at all, which is why it comes out at roughly 63 MB rather than several hundred.

**The architecture is `linux/amd64`, and that is a decision rather than a default.** The agent is built for arm64 because it runs on a Raspberry Pi; this does not. Your Docker host is amd64, so the build is native instead of emulated, and the image generator's whole affordability argument rests on an amd64 server writing an arm64 card image with no emulation in the path. Override with `FL_RID=linux-arm64 FL_PLATFORM=linux/arm64` if your server is an arm64 board; the pinned LiveKit release carries both Linux builds deliberately.

Three details in the output are worth understanding:

1. **`agent payload`** names the agent binary being baked into the image. The Fleet Manager serves that binary to every frame as its update feed, so an image built from a checkout that has never run `fl.py build` says `NONE` and serves no update. That is a valid image, not a broken one, but a fleet pointed at it can never self-update.
2. **`version=0.0.0+<sha>.dirty`** means the build was made from a working tree with uncommitted changes. A release is built from a clean tree and has no `.dirty`. It is a warning that this artifact is not reproducible, printed rather than enforced.
3. **The tag substitutes `-` for `+`**, because an OCI tag may not contain `+`. Both strings describe one artifact; the label inside the image keeps the real one.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cd /c/Source/SixFive7/FrameLink
bash deploy/fleet-manager/build-image.sh
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[image] repo=/c/Source/SixFive7/FrameLink
[image] version=0.0.0+a95958b.dirty  tag=framelink/fleet-manager:0.0.0-a95958b.dirty
[image] platform=linux/amd64  rid=linux-x64
[image] agent payload: linux-arm64 0.0.0+aa5b77f.dirty
[image] docker build ...
...
[image] smoke test: starting the image with no configuration (§3.2's unconfigured state)
[image] container 1f8b3e503524 on 127.0.0.1:32770
[image] unconfigured instance names its own variable, as §3.2 requires
[image] image framelink/fleet-manager:0.0.0-a95958b.dirty  63050331 bytes
[image] digest sha256:da69f07d90ddd68254e15503a994e90963746baf1f6d4f96dd2be1a7357398de
[image] done. Deploy with FRAMELINK_IMAGE=framelink/fleet-manager:0.0.0-a95958b.dirty
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `...` replaces several hundred lines of Docker build progress, which are noise unless something fails. What matters is the last four lines. `unconfigured instance names its own variable` is the smoke test passing: the script started the image it had just built, waited for `/healthz`, and confirmed the server told an anonymous caller which environment variable it is missing. A build that never reaches that line has produced an image that does not run, and the script exits 7 after printing the container's own last forty log lines. **Write down the tag on the final line**: it is what step 5 and the rollback in step 10 both need. If `agent payload` says `NONE`, run `python tools/harness/fl.py build` first and build the image again.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have a container image that has been proven to start and serve on the machine that built it, tagged with a name that will never mean anything else.

<a id="3-copy-the-image-to-the-docker-host"></a>
<img src="https://img.shields.io/badge/STEP_03-Copy_the_image_to_the_Docker_host-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 - Copy the image to the Docker host"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The image exists on your workstation and the server has never heard of it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Write the image to a single file, copy that file to the server the way you would copy any other file, and load it there.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`docker save` writes an image (every layer, its configuration and its tags) into one tar file that `docker load` on any other machine turns back into exactly the same image, digest included. It needs no registry, no account and no credentials, which is why it is the path this guide takes: a private registry is a second thing to run, secure and back up in order to move one file a few times a year.

`build-image.sh` will produce the tarball for you if you set `FL_SAVE_TO`, which saves the separate `docker save` below. The two commands are shown apart here because the interesting half is the second one, and because it is the half that runs somewhere else.

If you would rather push to a registry, nothing in the stack file cares: `FRAMELINK_IMAGE` is a plain image reference, so `ghcr.io/<you>/fleet-manager:<tag>` works exactly as a locally loaded tag does. What must not change is that the reference is **immutable**. `:latest` moves, and step 10's rollback is "run the previous tag", a question `:latest` cannot answer.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker save framelink/fleet-manager:0.0.0-a95958b.dirty -o fleet-manager.tar
scp fleet-manager.tar YOUR-SERVER:/tmp/fleet-manager.tar
ssh YOUR-SERVER docker load -i /tmp/fleet-manager.tar
ssh YOUR-SERVER rm /tmp/fleet-manager.tar
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Loaded image: framelink/fleet-manager:0.0.0-a95958b.dirty
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`docker save` and `scp` print nothing and a progress meter respectively; the line above is `docker load`'s and is the only one that matters. It must name the same tag you built. `Loaded image ID: sha256:...` **without** a tag means the tar was written from an untagged image and the stack will not find it, so rebuild and save again. The tar is around 63 MB, so a slow link is the only thing that makes this step take any time. Deleting it afterwards is tidiness, not safety.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The server now holds the exact image you built and tested, byte for byte, with no registry involved.

<a id="4-confirm-the-host-has-the-image-you-built"></a>
<img src="https://img.shields.io/badge/STEP_04-Confirm_the_host_has_the_image_you_built-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 - Confirm the host has the image you built"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A copy can go wrong quietly, and an image with the right name is not necessarily the right image.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the server what it actually has, and compare it with what your workstation said it made.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Three values, and each answers a different question. The **id** is the content digest of the image; if it matches the `digest` line step 2 printed, the artifact on the server is the artifact you tested and nothing on the way rewrote it. The **version label** is the real `0.0.0+<sha>` string with its `+` intact, which is what tells you which commit this was built from; the tag cannot carry it. The **size** is a sanity check against a truncated transfer.

This step exists because the alternative is finding out later. Every symptom of running the wrong image looks like a bug in the Fleet Manager: a fix that does not appear, a setting that is ignored, an agent binary that is a version behind. Sixty seconds here is cheaper than any of them.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker image inspect framelink/fleet-manager:0.0.0-a95958b.dirty --format '{{.Id}} {{index .Config.Labels "org.opencontainers.image.version"}} {{.Size}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
sha256:da69f07d90ddd68254e15503a994e90963746baf1f6d4f96dd2be1a7357398de 0.0.0+a95958b.dirty 63050331
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `sha256:` value **must be character-for-character the `digest` line from step 2**. This is the one value in this guide that has to match exactly rather than merely resemble. The version label must carry a `+`, not a `-`; a `-` there would mean the label was built from the tag rather than from the version, which no build in this repository does. `Error: No such image` means step 3's `docker load` ran somewhere other than where you are now looking.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have proved that the server is holding the image you tested, rather than something that shares its name.

<a id="5-deploy-the-stack"></a>
<img src="https://img.shields.io/badge/STEP_05-Deploy_the_stack-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 - Deploy the stack"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The image needs an address, a certificate, a password, storage and a set of network ports before it is a service rather than a program.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Paste one stack file into Portainer, type the site-specific values and the password into Portainer's own environment panel, and deploy.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

In Portainer: **Stacks → Add stack → Web editor**, paste `deploy/fleet-manager/framelink.stack.yml` verbatim, then fill the **Environment variables** panel from `deploy/fleet-manager/stack.env.example`. The RUN block below is the command-line equivalent of pressing Deploy, and is what to use if you would rather drive Compose directly; Portainer runs the same thing.

**The secrets are typed into Portainer and nowhere else.** `FRAMELINK_OPERATOR_PASSWORD` is the Fleet Manager's entire authentication story: one operator, one long password, from an environment variable only, minimum 24 characters, no accounts and no roles. Portainer keeps the variables in its own database and re-supplies them on every redeploy, so nothing has to be remembered and no file on the host holds a password. Compose's file-backed `secrets:` was deliberately not used: it requires writing the secret to a file on the host, which is precisely the thing this project forbids. Rotating the password later is "change it in Portainer, redeploy"; there is no password file and no hash on disk to undo, and frames are unaffected because the device route never sees a password.

**Two Traefik routers, one service, and the difference between them is the whole design.** The console router is protected by Authelia and the `/agent` router is not, because a frame is a machine with a keypair and no browser: an interactive sign-in in front of it would not make it safer, it would make the fleet unable to connect at all. The device route authenticates by keypair over the frozen handshake instead. Traefik's default priority is rule length, which already sorts the longer `/agent` rule first, but both priorities are stated explicitly, because a fleet's connectivity should not rest on a tie-break happening to go the right way.

**How that difference is expressed will surprise you, and getting it backwards is the single most expensive mistake in this guide.** Authelia here is not attached to individual routers. It is attached to the whole HTTPS *entrypoint*, so every router that answers on `:443` inherits it without asking, and a router that names its own middlewares *replaces* that inherited set rather than adding to it. The consequences invert what you would expect from reading Traefik's documentation: the console router carries **no middlewares label at all**, and that silence is what protects it; the `/agent` router carries an **explicitly empty** middlewares label, and that emptiness is what exempts it. Delete the empty label because it looks like a typo and every frame in the fleet starts being redirected to a sign-in page it has no browser to complete. Add a middlewares label to the console "to be safe" and you switch its protection off. Both are already correct in the stack file and neither is a variable you fill in, precisely because neither failure announces itself.

**The certificate resolver and the entrypoint names are already filled in.** They are `letsencrypt` and `https`. Note `https`, not the `websecure` that Traefik's own documentation uses for the same thing, because entrypoint names are chosen by whoever wrote the Traefik configuration and this one chose `http` and `https`. Neither needs to be typed into Portainer unless that configuration changes.

**What Traefik cannot carry is published directly, and the port numbers are load-bearing.** Call media is UDP; a reverse proxy terminates TCP. So `7881/tcp` and `50000-50059/udp` are published on the host one-to-one, exactly as the standalone LiveKit stack in [guide 7](7-livekit-server.md) did and [guide 8](8-webrtc-validation.md) validated. One-to-one is not a convention. The Fleet Manager tells LiveKit to advertise the address you put in `FRAMELINK_LIVEKIT_PUBLIC_URL`, so the candidates it offers a frame name a host every frame on your LAN can route to, but the *port* in each of those candidates is the one LiveKit bound inside the container, so a frame sent to that address on port `50000` reaches the call server only while host port `50000` is the one carrying it. Remap the range to different host ports and calls connect for nobody, with no error printed anywhere.

**The `/24` is the one value nobody can look up for you, and it is a judgement call rather than a lookup, but it is a judgement for the day this runs, not one to make in advance.** The registry that hands out subnets lists every stack alphabetically and numbers them in that order, so a stack's address depends on its *name*, and `framelink` sorts into the middle of a list that is already full. Taking the slot the rule actually points at means shifting every stack that sorts after it by one, two dozen of them, including the reverse proxy itself, which would move Traefik's own address and with it every firewall rule in the estate. Appending at the end instead costs nothing to deploy and leaves the list no longer sorted. `stack.env.example` sets out both options with every derived value already computed, from a reading of the registry as it stood when this guide was written; re-read the registry on the day, then choose, and the rest follows mechanically. Whatever is picked, the container takes `.100` in it, the gateway takes `.254`, and the pinned MAC is `02:42:` followed by the four address bytes written in hex, and that pinning is not decoration; it is what stops a recreated container coming back with an address its neighbours cannot deliver to. Compose refuses to render the stack file while any of the four is empty, so this cannot be skipped by accident.

Two more variables are worth extra care, and both now arrive already answered. `FRAMELINK_TRUSTED_PROXIES` is Traefik's address, and it matters because the registration endpoint is open to the internet and per-IP rate limiting is all that stands between it and unbounded noise: leave it empty and every client on earth shares one budget, set it too widely and the budget becomes forgeable. It works because the proxy reaches this stack by plain routing with the source address preserved, so what arrives really is Traefik's own address. One honest limitation: requests from inside your own house may arrive bearing your router's address rather than the real device's, which makes the limit coarse for local clients and leaves it exact for the internet clients it exists to defend against. `FRAMELINK_LIVEKIT_PUBLIC_URL` must be the Docker host's **LAN address**, not the public domain, because media bypasses the proxy and the frames must reach the host directly, and it does two jobs from that one value, since the Fleet Manager also hands that address to LiveKit as the one to advertise for media, so a name typed here would silently leave the media half advertising a container address instead.

**Check what already holds these host ports before deploying.** This stack publishes `7880`, `7881` and `50000`-`50059`, and anything already using them makes Docker refuse to start the second claimant. That is a good failure (loud, immediate, and it names the port), but it is easier to answer before than during, and what is running on that host is a question for the day rather than something this guide can know. `docker ps --format '{{.Names}} {{.Ports}}'` answers it. If an existing call server is to be kept, point the Fleet Manager at it with `FRAMELINK_LIVEKIT_URL`, `FRAMELINK_LIVEKIT_API_KEY` and `FRAMELINK_LIVEKIT_API_SECRET`, deleting the three `ports:` entries from the stack file since nothing in the container would then be listening on them; otherwise let the Fleet Manager supervise its own, which is what the rest of this guide assumes.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker compose -p framelink -f framelink.stack.yml --env-file stack.env up -d
docker compose -p framelink -f framelink.stack.yml --env-file stack.env ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Network framelink  Creating
 Network framelink  Created
 Container framelink  Creating
 Container framelink  Created
 Container framelink  Starting
 Container framelink  Started
NAME        IMAGE                                         STATUS
framelink   framelink/fleet-manager:0.0.0-a95958b.dirty   Up 13 seconds (health: starting)
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`health: starting` is correct for the first twenty seconds and becomes `healthy` after it; re-run the `ps` command to watch it change. The `IMAGE` column must be the immutable tag from step 2, never `latest`. Two failures are worth naming because their messages do not explain themselves: `network framelink: Pool overlaps with other one on this address space` means the subnet is already claimed by another stack and belongs to a different `/24` in `config/networks.yaml`; `external volume "framelink-data" not found` means step 1 was skipped or ran against a different Docker endpoint. If you deployed through Portainer rather than the command line, the equivalent of the first six lines is the deployment log Portainer shows you, and `ps` is the **Containers** view.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Fleet Manager is running on your server with its storage, its password, its network address and its media ports. Nothing has yet proved that anything outside the container can reach it.

<a id="6-prove-the-server-answers-where-traefik-reaches-it"></a>
<img src="https://img.shields.io/badge/STEP_06-Prove_the_server_answers_where_Traefik_reaches_it-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 - Prove the server answers where Traefik reaches it"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A container that is running is not the same as a service that can be reached, and when a web address does not work there is no way to tell which of the four things in the path is at fault.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask the Fleet Manager two questions from exactly where Traefik will ask them, so that anything still broken afterwards is provably Traefik's side and not the server's.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The commands below start a throwaway container on this stack's own bridge network and make two requests to the Fleet Manager. That is precisely the path Traefik takes, so a success here divides the world in two: everything from the bridge inwards works, and anything still failing at `https://framelink.huisman.io` is DNS, the certificate resolver, Authelia or the firewall, never the container.

Of those, the firewall is the one most likely to be at fault and the least likely to say so, because a container on one bridge reaching a container on another is not something that happens by default: it is an explicit rule, and in this estate that rule has two halves that must agree. The stack file declares its half: it will accept the reverse proxy on port 8080. The other half says the reverse proxy is allowed to make that connection, and it lives in the reverse proxy's own configuration, not here. A rule declared on one side only is rejected outright by the tooling that generates it, so this is not a silent-drop situation, but it does mean that if you manage your stacks in a repository, the Fleet Manager's arrival is an edit in two files rather than one, and the firewall layer only learns about this stack at all once a copy of it lives alongside the others there.

`/healthz` is the liveness probe the container's own health check uses; it answers `ok` and nothing else. `/api/status` is the more interesting one, and it is deliberately reachable with no session at all: it is what the browser asks before anybody has signed in, and on a server with no password it is the thing that names the missing variable. `"configured":true` therefore proves the password variable arrived from Portainer intact, and if it says `false`, the response carries the reason, which is either that the variable is unset or that its value is shorter than the 24-character floor.

Neither answer contains the password or any part of it. The credential is compared as a digest and never rendered.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker run --rm --network framelink curlimages/curl:latest -fsS http://framelink:8080/healthz
docker run --rm --network framelink curlimages/curl:latest -fsS http://framelink:8080/api/status
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
ok
{"configured":true,"variable":"FRAMELINK_OPERATOR_PASSWORD"}
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`ok` on the first line and `"configured":true` on the second. The word `framelink` in both commands is the container's own name, which Docker resolves to whatever address you pinned, so these two lines are correct whichever `/24` you chose; the two responses above were captured from the identical pair of requests addressed by that pinned address instead of by name. Traefik reaches this same port by the pinned address rather than by name, because it sits on a different bridge and routes to it. `"configured":false` arrives with a `problem` field saying exactly what is wrong (an unset variable or a password below the length floor), and it is not a crash: the server keeps running and every frame that connects is told the server is not set up yet, which is why a frame is often the first thing that tells you. `curl: (6) Could not resolve host` means the container is not on this network under this name; `curl: (7) Failed to connect` means it is not running. `curl: (28) Operation timed out` means the container is up but not listening, and `docker logs framelink` will say why. The first run of these commands also downloads the `curlimages/curl` image, which prints a few progress lines before the output above.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have proved the Fleet Manager is serving, configured, and reachable at the address Traefik will use, so the browser half is now the only thing that can still be wrong.

<a id="7-prove-the-call-server-is-supervised"></a>
<img src="https://img.shields.io/badge/STEP_07-Prove_the_call_server_is_supervised-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 - Prove the call server is supervised"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Video calling needs a second program running alongside the Fleet Manager, and the only way anybody normally discovers it is missing is by pressing the call button and getting nothing.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Read the server's own start-up log, where it says whether it fetched the call server and started it, and then confirm the network ports calls travel over are actually open.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

On its first start the Fleet Manager downloads the pinned `livekit-server` release from GitHub, refuses it unless it matches the checksum upstream publishes **and** the digest recorded in this repository, unpacks it into the data volume, generates its configuration with a freshly created API secret, and starts it as a child process. That is why the first start logs `installed` and every later one does not: the installer hashes what is already on disk and returns without fetching a byte. All of it happens after Kestrel is already listening, so a slow download delays calling and nothing else.

The `nodeIP` in LiveKit's own start-up line is the address you put in `FRAMELINK_LIVEKIT_PUBLIC_URL`, and it is worth checking rather than skimming, because it is the difference between a call that connects and a call that connects and carries nothing. External-IP discovery stays switched off: it asks a public server on the internet what this host's internet address is, which on a household network is both the wrong answer (the frames are on the same network and need the local address) and an outsider in the path of a call that never leaves the house. Left at that, LiveKit advertises the address it is genuinely on, which is this stack's own `/24` and unroutable from any frame; so the Fleet Manager writes the address you gave it into the generated configuration as `node_ip`, and LiveKit rewrites the media candidates it hands out to that address instead. The two settings only work in that order, which is why neither is a knob to turn: switching external-IP discovery on would have LiveKit work its own address out again and throw the configured one away, silently, leaving a file that looks correct.

`docker port` lists every published mapping. There should be 60 UDP entries, one per port in the range, plus the two TCP ports. **A smaller number is not automatically a fault:** a Docker host may have a handful of ports inside the range reserved by the operating system, and 58 of 60 is a fleet that calls perfectly. A number in the single digits means the range failed to publish, and calls will connect for nobody.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker logs framelink 2>&1 | head -8
docker port framelink | grep -c udp
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
info: fl-control[1000]
      FrameLink Fleet Manager starting with an operator password configured.
info: FrameLink.Control.LiveKit.LiveKitService[1809]
      LiveKit 1.13.5 installed at /var/lib/fl-control/livekit/livekit-server.
info: FrameLink.Control.LiveKit.LiveKitService[1810]
      LiveKit 1.13.5 started as pid 22, signalling on port 7880.
2026-08-16T02:11:13.517Z	INFO	livekit	routing/interfaces.go:181	using single-node routing
2026-08-16T02:11:13.658Z	INFO	livekit	service/server.go:292	starting LiveKit server	{"portHttp": 7880, "nodeID": "ND_fPeMV8yHGvre", "nodeIP": "172.16.20.10", "version": "1.13.5", "bindAddresses": ["0.0.0.0"], "rtc.portTCP": 7881, "rtc.portICERange": [50000, 50199]}
198
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`starting with an operator password configured`, then `installed at`, then `started as pid`, then LiveKit's own two lines with `rtc.portICERange: [50000, 50059]`. **The captured block above predates two changes and a current run contradicts it in both places. It is left exactly as it was captured rather than edited to match, because a rewritten capture is an invented one.** It was taken when the media range was two hundred ports wide, so it reads `[50000, 50199]` where a current run reads `[50000, 50059]`, and `198` below it where a current run counts `60`. And it was taken before the Fleet Manager was given an address to advertise, so its `nodeIP` is the container's own address on this stack's `/24`. That is superseded, and the paragraph above describes what replaces it: a current run prints the address from `FRAMELINK_LIVEKIT_PUBLIC_URL` there, and that is what you should see. The `nodeID` will not match either and is not meant to: it is generated fresh on every start. On any start after the first, the `installed at` line is **absent** and that is the correct, idempotent behaviour: the binary was already there and verified. A line reading `refused: the download does not match the checksum upstream published` means the release has been tampered with or the pin is stale, and the Fleet Manager will not run it; everything except calling keeps working. `could not be downloaded: this server has no route to GitHub` is the same outcome from a network problem. The port count is this host's and yours may differ by a few, for the reason given above; if it is `0`, the whole range failed to publish and something else on the host already holds those ports.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The call server has been fetched, checked against its published checksum, configured with a secret only this server holds, started, and given the network ports call audio and video need, which means the separate LiveKit of [guide 7](7-livekit-server.md) is now redundant and can be retired, if you had one running and did not have to retire it before step 5 to free these ports.

<a id="8-send-the-first-alert-to-home-assistant"></a>
<img src="https://img.shields.io/badge/STEP_08-Send_the_first_alert_to_Home_Assistant-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 08 - Send the first alert to Home Assistant"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A frame can stop working and nobody finds out for days. That is not a hypothetical: it is exactly what happened on 23 July 2026, when a credential quietly ran out and the first sign of trouble was a family member pressing the call button.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Give the Fleet Manager a web address in Home Assistant to shout at, then check that the shout arrives.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

In Home Assistant, create an automation with a **Webhook** trigger; it gives you a URL of the form `http://<home-assistant>:8123/api/webhook/<id>`. Put that in `FRAMELINK_ALERT_WEBHOOK` in Portainer and redeploy. A webhook is used rather than the `notify` service precisely because it needs no credential: the URL is the whole of it, so this deployment gains no new secret to store or rotate. Any receiver that accepts an HTTP POST works (ntfy, Gotify, a mail bridge, a shell script) because what is sent is a flat JSON object with a `subject` and a `detail` your automation can put straight into a notification.

**Four conditions are watched, and the shortness of the list is the design.** A frame that was in contact and has been silent for thirty minutes. A frame holding a call credential with under thirty days left. The call server not answering. A frame that has given up and stopped changing anything. Nothing else: no CPU graphs, no memory, no request rates. All of that is already on the console for anyone looking at it, and none of it is worth waking somebody for. The credential rule is the direct descendant of the July failure: the Fleet Manager now renews every frame's call token automatically, so a token getting anywhere near its expiry means the renewal is not reaching that frame, and thirty days is enough warning that the answer is still "fix it" rather than "drive over there".

**Each condition is delivered once when it starts and once when it ends.** A frame away for repair produces one message, not one every five minutes; and a resolution is sent as its own message, so a channel that has been quiet for a month has genuinely had nothing to say. If Home Assistant is unreachable when an alert fires, the alert is not lost; it stays open and is delivered again on the next pass, and the container log carries the full text in the meantime.

The `docker logs` line below is the check to run first, because it works whether or not the webhook is configured: alerts are written to the container log unconditionally, and that log is the fallback channel for a deployment that has not wired up anything else. The command is written to find nothing on a healthy fleet, which is the point, and the reason step 8 ends by making it find something.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker logs framelink 2>&1 | grep ALERT
docker exec framelink curl -fsS -X POST -H 'Content-Type: application/json' -d '{"probe":"framelink"}' "$FRAMELINK_ALERT_WEBHOOK"
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
warn: FrameLink.Control.Alerting.FleetWatch[1900]
      ALERT call-server-down opened [call-server-down]: No frame in this fleet can place a call — The bundled LiveKit call server is not running or is not configured, so every frame's call button will fail. Photos and everything else are unaffected. FRAMELINK_LIVEKIT_PUBLIC_URL is not set, so this Fleet Manager does not know which address frames should dial for calls. Set it to the LiveKit signalling URL frames can reach — for example ws://<this-server>:7880 on a home network, or wss://<your-domain> behind a reverse proxy.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

On a correctly configured server the first command prints **nothing at all**, and that is the healthy result. The block above is the output from a deliberately misconfigured server, one started without `FRAMELINK_LIVEKIT_PUBLIC_URL`, shown so you know what an alert looks like and can recognise one later; it is real captured output, not an illustration. Every alert line names the condition, its stable key, a one-line subject and the full detail, and the detail is written to be read by whoever is on call rather than by whoever wrote the code. The second command is the plumbing test: it POSTs a harmless probe to your webhook from inside the container, so a success proves the container can reach Home Assistant, and `curl: (7)` or `curl: (28)` proves it cannot, almost always the firewall. Worth knowing which rule to look at: Home Assistant commonly runs directly on the server rather than on a bridge of its own, in which case reaching it is not a container-to-container rule at all but a container-to-host one, and the stack file already asks for exactly that and nothing else. Watch your automation fire in Home Assistant's trace view to confirm the other end. The exact JSON an alert sends is `{"source":"framelink-fleet-manager","event":"opened","key":...,"kind":...,"severity":...,"subject":...,"detail":...,"openedUtc":...}`, with `"event":"cleared"` when the condition ends.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The failure that started this whole project, something expiring or going quiet with nobody watching, now reaches a phone, and you have seen with your own eyes that the path from the Fleet Manager to Home Assistant works.

<a id="9-back-up-the-fleet-database"></a>
<img src="https://img.shields.io/badge/STEP_09-Back_up_the_fleet_database-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 09 - Back up the fleet database"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

There is no backup button anywhere in FrameLink, and the one file that matters is inside a Docker volume you cannot simply copy from a file manager.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Stop the server for a few seconds, copy the volume's contents into a compressed file, and start it again. Keep that file wherever you keep everything else you would be upset to lose.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Stopping first is what makes the copy trustworthy. The database is SQLite in write-ahead-logging mode, so while it is running there are three files (`framelink.db`, `framelink.db-wal` and `framelink.db-shm`), and copying them one at a time while writes are in flight can produce a set that does not agree with itself. A clean shutdown folds the write-ahead log back into the database and deletes the other two, which is why the archive below contains exactly one file. The frames are unharmed by the pause: a frame that was green when contact dropped keeps showing photos and simply reconnects.

Two directories are excluded on purpose. `images/` holds generated SD-card images at roughly 3 GB each, and `livekit/` holds the 50 MB call-server binary. Both are reproducible (the images can be built again and the binary is re-fetched and re-verified against its pinned digest on the next start), so including them would turn a 3 kB backup into a multi-gigabyte one that is no more useful. What comes out is small enough to keep every copy you ever make.

Restoring is the same command with `tar xzf` and the volume mounted writable, against a stopped stack. And it is worth knowing what a *lost* backup actually costs, because it is less than it sounds: a replacement Fleet Manager at the same address sees every configured frame reappear in the adoption queue, and re-adopting them is a few clicks per frame. The backup saves you their names, their per-device settings and their call tokens, not the fleet itself.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker compose -p framelink -f framelink.stack.yml --env-file stack.env stop
docker run --rm -v framelink-data:/data:ro -v "$PWD":/backup debian:trixie-slim tar czf /backup/framelink-data.tar.gz -C /data --exclude=images --exclude=livekit .
docker run --rm -v "$PWD":/backup debian:trixie-slim tar tzvf /backup/framelink-data.tar.gz
docker compose -p framelink -f framelink.stack.yml --env-file stack.env start
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Container framelink  Stopping
 Container framelink  Stopped
drwxr-x--- 10001/10001       0 2026-08-16 02:11 ./
-rw-r--r-- 10001/10001  106496 2026-08-16 02:11 ./framelink.db
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Exactly one `framelink.db` in the listing, owned by `10001/10001`, the unprivileged user the container runs as. **A `framelink.db-wal` in the listing means the container had not finished stopping when the copy ran**; wait for the `Stopped` line before starting the `tar`. The database is around 100 kB on a new deployment and grows slowly; a month of a small fleet's history is still measured in megabytes. The `start` at the end prints two more lines that are not shown here. Copy `framelink-data.tar.gz` off this machine; a backup on the same disk as the thing it protects is not a backup.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You hold a small, consistent, restorable copy of every adopted frame's identity, name, settings and call credential.

<a id="10-redeploy-and-roll-back"></a>
<img src="https://img.shields.io/badge/STEP_10-Redeploy_and_roll_back-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 10 - Redeploy and roll back"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Sooner or later you will install a new version that behaves worse than the one it replaced, and you will want the old one back without losing anything.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Both directions are the same single action: change which image tag the stack names, and deploy it again. The storage is never touched by either.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Upgrading is steps 2, 3 and 4 with a new commit, then editing `FRAMELINK_IMAGE` in Portainer and pressing Deploy. Rolling back is the identical edit with the previous tag, which is why step 3 insisted the tag be immutable, and why `:latest` is refused throughout: "run the previous one" is a question a moving tag cannot answer.

**Redeploying is idempotent, and it is worth knowing exactly why**, because the answer is what makes it safe to do at three in the morning. Compose recreates the container and nothing else; the volume is external and untouched. The database schema is applied with `CREATE TABLE IF NOT EXISTS` on every start, so a start against an existing database adds whatever is new and changes nothing that exists. The call server binary is verified by digest and re-fetched only if it is missing or wrong. The LiveKit configuration file is rewritten only if its contents would differ. Every adopted frame reconnects on its own and is re-sent its complete settings, so nothing depends on a push having landed during the restart.

**Rolling back works because the schema only ever grows.** Tables and nullable columns are added; nothing is ever repurposed or removed. An older Fleet Manager started against a newer volume therefore finds every table and column it knows about exactly where it left them and simply never reads the ones added after it. That is why the rollback is one edit and not a database restore, and it is also the rule that keeps it true, so a future change that renames or drops a column is a change that breaks rollback and must say so.

A rollback across a version that *did* change a column's meaning would need step 9's archive restored first. Nothing in FrameLink has ever done that, and this is the property that has to be defended rather than assumed.

**The tag carries the agent too, so a rollback moves the fleet and not only the server.** Step 3's `agent payload` is inside the image, and a frame asks its Fleet Manager hourly what agent it should be running and then matches the answer in whichever direction that requires, so naming an older tag hands every frame the agent that was in `build/out` when *that* image was built, and they converge onto it within the hour, or within seconds of reconnecting after the recreate. That is the intended behaviour and it is why the agent is baked in rather than published separately: there is no way to end up with a server from one build talking to frames from another. Two consequences follow and both belong in the decision to roll back. A rollback aimed at a server-side bug takes the frames' agent with it whether or not that was wanted. And an older tag built from a checkout with nothing in `build/out` serves no agent at all: the frames keep running what they already have and are simply offered nothing, which is safe, and is not the same as being rolled back. `curl -fsS https://framelink.huisman.io/agent/release/linux-arm64` answers with the version actually being served and is the one-line way to tell those two apart; it needs no session, because a frame too old to be adopted must still be able to repair itself.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
sed -i 's|^FRAMELINK_IMAGE=.*|FRAMELINK_IMAGE=framelink/fleet-manager:0.0.0-be39871.dirty|' stack.env
docker compose -p framelink -f framelink.stack.yml --env-file stack.env up -d
docker compose -p framelink -f framelink.stack.yml --env-file stack.env ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Container framelink  Recreate
 Container framelink  Recreated
 Container framelink  Starting
 Container framelink  Started
NAME        IMAGE                                         STATUS
framelink   framelink/fleet-manager:0.0.0-be39871.dirty   Up 23 seconds (healthy)
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`Recreate` / `Recreated` rather than `Creating`: the second word is what tells you Compose replaced an existing container instead of finding none. The `IMAGE` column must show the tag you rolled back to, and `Up ... (healthy)` proves the older build read the newer database without complaint. If Portainer is your route, this is editing the variable and pressing **Update the stack**; tick nothing about volumes. `Container framelink  Recreated` followed within seconds by `Exited (139)` or a restart loop means the older image genuinely cannot read this database, which is the one case that needs step 9's archive restored before the rollback will hold. Frames reconnect by themselves within a minute and there is nothing to *do* on any frame, but they do not come through it unchanged, because the tag carries their agent as well: read `/agent/release/linux-arm64` afterwards and confirm the version it names is the one this tag was meant to bring back.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You can move the Fleet Manager forwards and backwards between versions with one edit, with the fleet's identities, names, settings and credentials untouched in either direction.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`https://framelink.huisman.io` loads the Fleet Manager console through Traefik and asks for Authelia, while a frame pointed at the same address connects, appears in the adoption queue and is never shown a login page. `docker logs framelink` shows `LiveKit 1.13.5 started as pid N` and `docker port framelink | grep -c udp` reports the media range published. `docker volume inspect framelink-data` shows a volume that no `docker compose down` has ever removed, and `docker compose ... up -d` with the previous image tag brings the previous version back, healthy, reading the same database.
