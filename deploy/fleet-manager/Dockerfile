# FrameLink Fleet Manager — the production container (version2.md §3.1, §3.8, milestone Mn+2).
#
# WHY THIS IS A SECOND DOCKERFILE AND NOT AN EXTENSION OF build/Dockerfile
# ------------------------------------------------------------------------
# build/Dockerfile is a *toolchain* image: the stock .NET SDK plus the Native AOT prerequisites,
# baked once so the emulated arm64 agent build does not re-pay an apt-get install on every run.
# It produces no artifact of its own; build/build.sh runs inside it against a bind-mounted
# checkout. This file is a *delivery* image: multi-stage, self-contained, and what comes out is
# the thing that gets deployed. Merging them would mean either shipping a 1 GB SDK to production
# or teaching a toolchain image to also be a runtime.
#
# What IS shared is the part that matters — the apt list below is the same clang / zlib1g-dev /
# binutils / ca-certificates set build/Dockerfile installs, for the same reasons, and the publish
# flags are the same ones build.sh passes. One build path, two packagings.
#
# WHY linux/amd64 AND NOT arm64
# -----------------------------
# The agent is arm64 because it runs on a Raspberry Pi. The server does not. Three reasons, in
# the order they decided it:
#
#   1. §3.9 already assumes it. "An amd64 Fleet Manager writes an arm64 image with no qemu,
#      no binfmt and no emulation anywhere in the path" is the sentence that moved image
#      generation out of v3, and it is a statement about this deployment's server.
#   2. The target is this operator's existing Docker host, which runs linux/amd64 — the same
#      host §3.7 records as currently running LiveKit as a separate stack.
#   3. The build is then native rather than emulated. The agent's arm64 build under QEMU is
#      minutes; this one is native and therefore is not, which matters because the Fleet Manager
#      is rebuilt far more often than the agent's target architecture changes.
#
# The pin in LiveKitReleasePin carries BOTH linux assets deliberately, so a self-hoster running
# this container on an arm64 board is an ordinary case: pass --build-arg FL_RID=linux-arm64 with
# --platform linux/arm64 and everything below works unchanged.
#
# BUILD IT WITH deploy/fleet-manager/build-image.sh, which passes the version, the labels and the
# RID consistently. Running `docker build` by hand works and produces an image whose version says
# 0.0.0-local, which is honest rather than wrong.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=debian:trixie-slim

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — publish Native AOT.
# ─────────────────────────────────────────────────────────────────────────────
FROM ${SDK_IMAGE} AS build

# clang        - Native AOT's linker driver on Linux.
# zlib1g-dev   - libz headers; the AOT runtime links zlib statically.
# binutils     - objcopy, used to split debug symbols out of the ELF.
# ca-certificates - NuGet over HTTPS from inside the container.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        clang \
        zlib1g-dev \
        binutils \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# §1.2 principle 1 is "no phone-home". A build container that reported usage to Microsoft on every
# run would be the project violating its own first principle in its own toolchain.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src
COPY . .

ARG FL_RID=linux-x64
ARG FL_VERSION=0.0.0-local

# -p:FrameLinkBuildGui=false, and it is not an optimisation. wwwroot is a committed build artifact
# (§3.1 ships one container with the Svelte output served by UseStaticFiles), there is no Node in
# this image, and GuiFreshnessTests is what decides whether the committed bundle is stale — a check
# that runs in the suite, on a machine that has npm, before anything is built here. Leaving the
# target enabled would produce a warning on every image build and change nothing.
#
# --artifacts-path keeps every intermediate off the copied source tree, matching build.sh. Here it
# buys tidiness rather than correctness (this is a COPY, not a bind mount), but it also guarantees
# that a stale obj/ carried in from a developer's checkout cannot be picked up — which is the
# NETSDK1047 failure build.sh documents, arriving by a different route.
RUN dotnet publish src/FrameLink.Control/FrameLink.Control.csproj \
        --configuration Release \
        --runtime "${FL_RID}" \
        --output /out/app \
        --artifacts-path /tmp/fl-artifacts \
        -p:PublishAot=true \
        -p:StripSymbols=true \
        -p:InvariantGlobalization=true \
        -p:FrameLinkBuildGui=false \
        -p:Version="${FL_VERSION%%+*}" \
        -p:InformationalVersion="${FL_VERSION}" \
    && test -x /out/app/fl-control

# The agent binaries this Fleet Manager serves to the fleet (§2.8) and writes into generated
# images (§3.9). They are produced by the *other* build path — an emulated arm64 container, via
# `fl.py build` — into build/out, which is gitignored, so a clean clone has none.
#
# That is why this is a shell loop and not a COPY: an image built from a checkout that has never
# run an agent build is a valid image with no agent in it, and AgentReleaseCatalog already answers
# "no release for that runtime" rather than failing. A COPY of a missing path would instead fail
# the whole image build, turning "you have not built the agent yet" into "the Fleet Manager cannot
# be packaged".
#
# `install -D` is what makes the destination layout right without a mkdir: the catalog reads
# <rid>/fl-agent and its .version sidecar, which is exactly what build.sh writes.
RUN set -eu; \
    mkdir -p /out/agent; \
    for rid in linux-arm64 linux-x64; do \
        if [ -f "build/out/${rid}/fl-agent" ]; then \
            install -D -m 0755 "build/out/${rid}/fl-agent" "/out/agent/${rid}/fl-agent"; \
            if [ -f "build/out/${rid}/fl-agent.version" ]; then \
                install -D -m 0644 "build/out/${rid}/fl-agent.version" "/out/agent/${rid}/fl-agent.version"; \
            fi; \
            echo "[image] staged agent ${rid} $(cat "build/out/${rid}/fl-agent.version" 2>/dev/null || echo '(no version sidecar)')"; \
        else \
            echo "[image] no agent binary for ${rid} in this checkout - the image will serve none"; \
        fi; \
    done

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — the delivered image.
# ─────────────────────────────────────────────────────────────────────────────
FROM ${RUNTIME_IMAGE} AS runtime

# ca-certificates - HTTPS to GitHub for the pinned LiveKit release (§3.7), and to whatever
#                   receives an alert webhook (§3.5).
# e2fsprogs       - debugfs and e2fsck. §3.9's image generator, measured at 1537 kB installed,
#                   unprivileged, editing a file rather than mounting one.
# mtools          - mcopy, the boot-partition half of the same job. 400 kB.
# curl            - the HEALTHCHECK below, and the one tool an operator wants when they exec into
#                   a container that is answering nothing.
#
# Deliberately absent: any .NET runtime. PublishAot produces a self-contained ELF, which is what
# lets the delivered image be Debian plus four packages rather than Debian plus a framework.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        e2fsprogs \
        mtools \
        curl \
    && rm -rf /var/lib/apt/lists/*

# A fixed, high, unprivileged uid. Fixed because a named volume inherits the ownership of the
# image directory it is first mounted over, so this number is what makes the data directory
# writable on a first deploy with no chown anywhere; and because a redeploy that renumbered the
# user would silently lose write access to the volume holding every adopted frame.
#
# Nothing here needs root. The AOT binary binds ports above 1024 only, §3.9 proved debugfs and
# mcopy work with no --privileged and no --cap-add, and livekit-server is an ordinary process
# listening on 7880/7881 and a high UDP range.
RUN groupadd --gid 10001 framelink \
    && useradd --uid 10001 --gid 10001 --home-dir /var/lib/fl-control --shell /usr/sbin/nologin framelink

COPY --from=build --chown=root:root /out/app /app
COPY --from=build --chown=root:root /out/agent /app/agent

# The volume mount points, created in the image and owned by the runtime user so that Docker
# propagates that ownership into a freshly created named volume. An operator re-using an existing
# root-owned volume has to chown it once; the deployment guide says so and gives the command.
#
# There are two because §3.9's generator wants three image-sized files where §3.1 budgeted a
# SQLite database, so both compose files give it a volume of its own and point FRAMELINK_IMAGE_DIR
# at /var/lib/fl-images. That path has to exist HERE for the same reason the data directory does:
# a named volume mounted over a path the image does not contain is created root-owned 0755, and
# this container runs as 10001 with every capability dropped — so the first Generate would fail
# creating the working directory, on a volume that looks perfectly correct in `docker inspect`.
#
# FRAMELINK_IMAGE_DIR is deliberately NOT set in the ENV block below. ControlOptions defaults it
# to DataDirectory/images so that a self-hoster who mounts one volume gets a working setup on it,
# and an image that overrode that default would take the choice away from the deployment that
# should be making it.
RUN install -d -o 10001 -g 10001 -m 0750 /var/lib/fl-control /var/lib/fl-images

USER 10001:10001
WORKDIR /var/lib/fl-control

# 8080  the GUI and /agent, which is what Traefik proxies.
# 7880  LiveKit signalling — HTTP and WebSocket, the half that CAN ride a reverse proxy.
# 7881  LiveKit's TCP media fallback, for a network that blocks UDP.
# 50000-50199/udp  where call media actually flows. A reverse proxy cannot carry any of it,
#                  which is why §3.7 calls the split unavoidable.
EXPOSE 8080 7880 7881
EXPOSE 50000-50199/udp

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    FRAMELINK_DATA_DIR=/var/lib/fl-control \
    DOTNET_gcServer=0 \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft=Warning

# Why the log level is set in the image rather than left to the framework's default: at
# Information, Microsoft.AspNetCore logs four lines per request, and the health check alone is
# 5,760 requests a day. §3.5's alerting writes every opened and cleared condition to this log and
# treats it as a delivery channel in its own right on a deployment with no webhook — so a log an
# operator cannot read is a channel that does not work. FrameLink's own categories stay at
# Information, which is where those alert lines are. Override either key to debug a request path.

# §3.2 makes an unconfigured instance a designed state: it starts, it serves, and it explains
# itself. So the health check asks whether the process is answering HTTP, not whether it is
# configured — a container marked unhealthy because nobody has set the password yet would be
# restarted forever by an orchestrator, and the setup page nobody could then read is the one
# thing that would have told them why.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS --max-time 4 http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["/app/fl-control"]

ARG FL_VERSION=0.0.0-local
LABEL org.opencontainers.image.title="FrameLink Fleet Manager" \
      org.opencontainers.image.description="FrameLink Fleet Manager: ASP.NET Core Native AOT, SQLite, embedded GUI, embedded agent feed, bundled LiveKit." \
      org.opencontainers.image.version="${FL_VERSION}" \
      org.opencontainers.image.source="https://github.com/SixFive7/FrameLink" \
      org.opencontainers.image.licenses="LicenseRef-FrameLink-FSL-1.1-MIT-5yr"
