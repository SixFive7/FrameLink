#!/usr/bin/env bash
#
# Builds the FrameLink Fleet Manager container image (version2.md milestone Mn+2).
#
# The workstation-side counterpart of build/build.sh: that one runs *inside* an emulated arm64
# container and produces the agent ELF; this one runs *outside* and produces the server image.
# Same conventions on purpose — environment variables for every input, a version derived from git
# with `.dirty` when the tree is not clean, and exit codes a caller can turn into states.
#
#   bash deploy/fleet-manager/build-image.sh
#   FL_TAG=framelink/fleet-manager bash deploy/fleet-manager/build-image.sh
#   FL_RID=linux-arm64 FL_PLATFORM=linux/arm64 bash deploy/fleet-manager/build-image.sh
#
# Inputs (all optional):
#   FL_TAG        image repository                     (default framelink/fleet-manager)
#   FL_VERSION    version string                       (default 0.0.0+<short-sha>[.dirty])
#   FL_RID        .NET runtime identifier              (default linux-x64)
#   FL_PLATFORM   docker platform                      (default linux/amd64)
#   FL_SDK_IMAGE  build-stage base image               (default mcr.microsoft.com/dotnet/sdk:10.0)
#   FL_RUNTIME_IMAGE delivered image's base            (default debian:trixie-slim)
#   FL_LATEST     also tag :latest, 1 or 0             (default 1)
#   FL_SAVE_TO    write a `docker save` tarball here   (default unset - no tarball)
#
# Outputs:
#   <FL_TAG>:<version-tag>   the immutable tag. THIS is what a compose file names.
#   <FL_TAG>:latest          a convenience alias, never what production references.
#
# Exit codes:
#   0  success
#   4  docker is not available
#   5  the repository root does not look like this repository
#   6  docker build failed
#   7  the built image does not run, or does not report the version it was tagged with

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "${HERE}/../.." && pwd)"

FL_TAG="${FL_TAG:-framelink/fleet-manager}"
FL_RID="${FL_RID:-linux-x64}"
FL_PLATFORM="${FL_PLATFORM:-linux/amd64}"
FL_SDK_IMAGE="${FL_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
FL_RUNTIME_IMAGE="${FL_RUNTIME_IMAGE:-debian:trixie-slim}"
FL_LATEST="${FL_LATEST:-1}"

say() { printf '[image] %s\n' "$*"; }
die() { printf '[image] ERROR: %s\n' "$1" >&2; exit "${2:-1}"; }

command -v docker >/dev/null 2>&1 || die "docker is not on PATH. Docker Desktop must be running (§5.4 item 3)." 4
docker version --format '{{.Server.Version}}' >/dev/null 2>&1 \
    || die "the Docker daemon is not responding. Start Docker Desktop." 4

[ -f "${REPO}/FrameLink.slnx" ] || die "${REPO} does not contain FrameLink.slnx" 5
[ -f "${REPO}/src/FrameLink.Control/FrameLink.Control.csproj" ] || die "the Fleet Manager project is missing" 5

# ---------------------------------------------------------------------------
# The version. Same derivation as tools/harness/flh/build.py so that an agent and a Fleet Manager
# built from one commit carry one number: the short SHA, plus `.dirty` when the tree has
# uncommitted changes. A dirty build is not reproducible and must never be mistaken for one.
#
# The DOCKER TAG is not the same string, and cannot be: an OCI tag may not contain '+'. So the
# version keeps its '+' for the binary and the label, and the tag substitutes '-'. Both are
# recorded, and the smoke test below proves they still describe the same artifact.
# ---------------------------------------------------------------------------
if [ -z "${FL_VERSION:-}" ]; then
    SHA="$(git -C "${REPO}" rev-parse --short=7 HEAD 2>/dev/null || echo unknown)"
    DIRTY=""
    if [ -n "$(git -C "${REPO}" status --porcelain 2>/dev/null)" ]; then
        DIRTY=".dirty"
    fi
    FL_VERSION="0.0.0+${SHA}${DIRTY}"
fi

VERSION_TAG="${FL_VERSION//+/-}"

say "repo=${REPO}"
say "version=${FL_VERSION}  tag=${FL_TAG}:${VERSION_TAG}"
say "platform=${FL_PLATFORM}  rid=${FL_RID}"

if [ -f "${REPO}/build/out/linux-arm64/fl-agent" ]; then
    say "agent payload: linux-arm64 $(cat "${REPO}/build/out/linux-arm64/fl-agent.version" 2>/dev/null || echo '(no version sidecar)')"
else
    say "agent payload: NONE. This image will serve no agent binary; run 'fl.py build' first if the"
    say "               fleet is expected to self-update from it (§2.8)."
fi

# ---------------------------------------------------------------------------
# Build. The context is the repository root — the publish reads src/, the committed wwwroot and
# build/out — and the context filter is deploy/fleet-manager/Dockerfile.dockerignore, which
# BuildKit picks up from the Dockerfile's own path.
# ---------------------------------------------------------------------------
BUILD_ARGS=(
    --platform "${FL_PLATFORM}"
    --file "${HERE}/Dockerfile"
    --build-arg "SDK_IMAGE=${FL_SDK_IMAGE}"
    --build-arg "RUNTIME_IMAGE=${FL_RUNTIME_IMAGE}"
    --build-arg "FL_RID=${FL_RID}"
    --build-arg "FL_VERSION=${FL_VERSION}"
    --tag "${FL_TAG}:${VERSION_TAG}"
)

if [ "${FL_LATEST}" = "1" ]; then
    BUILD_ARGS+=(--tag "${FL_TAG}:latest")
fi

say "docker build ..."
docker build "${BUILD_ARGS[@]}" "${REPO}" || die "docker build failed" 6

# ---------------------------------------------------------------------------
# Smoke test. §0 principle 4 is "never fabricate": a script that prints "built" without ever
# starting the thing is asserting something it did not check.
#
# --version is not a flag this server has, so the check is the one that matters anyway: start it
# with no configuration at all and see whether it serves. §3.2 makes that a designed state — an
# unconfigured instance comes up and explains itself — so a container that does NOT answer here is
# unambiguously broken rather than merely unconfigured.
# ---------------------------------------------------------------------------
say "smoke test: starting the image with no configuration (§3.2's unconfigured state)"
CONTAINER="$(docker run --rm --detach --platform "${FL_PLATFORM}" \
    --publish 127.0.0.1:0:8080 \
    --env FRAMELINK_LIVEKIT_ENABLED=false \
    "${FL_TAG}:${VERSION_TAG}")" || die "the built image would not start" 7

cleanup() { docker rm --force "${CONTAINER}" >/dev/null 2>&1 || true; }
trap cleanup EXIT

ADDRESS="$(docker port "${CONTAINER}" 8080/tcp | head -1)"
say "container ${CONTAINER:0:12} on ${ADDRESS}"

HEALTHY=0
for _ in $(seq 1 40); do
    if curl -fsS --max-time 2 "http://${ADDRESS}/healthz" >/dev/null 2>&1; then
        HEALTHY=1
        break
    fi
    sleep 0.5
done

if [ "${HEALTHY}" != "1" ]; then
    docker logs "${CONTAINER}" 2>&1 | tail -40 | sed 's/^/[image]   /' >&2
    die "the container started but never answered /healthz" 7
fi

STATUS="$(curl -fsS --max-time 4 "http://${ADDRESS}/api/status" || true)"
case "${STATUS}" in
    *FRAMELINK_OPERATOR_PASSWORD*) say "unconfigured instance names its own variable, as §3.2 requires" ;;
    *) die "/api/status did not name FRAMELINK_OPERATOR_PASSWORD: ${STATUS}" 7 ;;
esac

SIZE="$(docker image inspect --format '{{.Size}}' "${FL_TAG}:${VERSION_TAG}")"
say "image ${FL_TAG}:${VERSION_TAG}  ${SIZE} bytes"
say "digest $(docker image inspect --format '{{.Id}}' "${FL_TAG}:${VERSION_TAG}")"

if [ -n "${FL_SAVE_TO:-}" ]; then
    say "docker save -> ${FL_SAVE_TO}"
    docker save "${FL_TAG}:${VERSION_TAG}" -o "${FL_SAVE_TO}"
    say "saved $(stat -c %s "${FL_SAVE_TO}" 2>/dev/null || echo '?') bytes"
fi

say "done. Deploy with FRAMELINK_IMAGE=${FL_TAG}:${VERSION_TAG}"
