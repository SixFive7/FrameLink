"""Workstation side of the build path - version2.md section 5.2.

Native AOT cannot cross-compile from Windows and building on the mule pollutes the target
that gets wiped repeatedly, so the binary is produced inside an **emulated linux/arm64
container** on the workstation. This module drives that container; ``build/build.sh`` is
what runs inside it.

Two images, one mechanism
-------------------------
``framelink-build:arm64`` (from ``build/Dockerfile``) is the stock .NET SDK image plus the
Native AOT toolchain, baked once and cached. ``--stock-image`` instead runs
``mcr.microsoft.com/dotnet/sdk:10.0`` verbatim - the invocation recorded as proven in
section 5.2 - where build.sh apt-installs that toolchain at runtime on every build. Both produce
the same artifact; the first is minutes faster from the second build onwards.

Why the harness calls docker.exe directly rather than through a shell
---------------------------------------------------------------------
Git Bash mangles container-absolute paths (``/src/build/build.sh`` becomes a Windows path)
unless ``MSYS_NO_PATHCONV=1`` is set. That translation is done by the MSYS runtime when it
launches a native Windows process, so invoking ``docker.exe`` from Python with an argument
list never goes near it. ``MSYS_NO_PATHCONV=1`` is set in the child environment anyway, as
insurance for anyone who later wraps this in a shell script.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path
from typing import Any

from . import progress, ui
from .config import (
    AGENT_PROJECT,
    BINARY_NAME,
    BUILD_IMAGE,
    BUILD_OUT,
    BUILD_PLATFORM,
    BUILD_SCRIPT_IN_CONTAINER,
    DOCKERFILE,
    NUGET_CACHE_VOLUME,
    REPO_ROOT,
    RID,
    STOCK_SDK_IMAGE,
    HarnessError,
)

RELEASE_JSON = BUILD_OUT / "agent-release.json"
BINARY_PATH = BUILD_OUT / BINARY_NAME


def _docker() -> str:
    exe = shutil.which("docker")
    if not exe:
        raise HarnessError(
            "docker is not on PATH.",
            exit_code=4,
            remedy="Docker Desktop must be running (version2.md section 5.4 item 3).",
        )
    return exe


def _child_env() -> dict[str, str]:
    import os

    env = dict(os.environ)
    env["MSYS_NO_PATHCONV"] = "1"
    env["DOCKER_CLI_HINTS"] = "false"
    return env


def _run(argv: list[str], *, timeout: float, what: str) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(  # noqa: S603 - argv is constructed here, never user text
            argv,
            capture_output=True,
            text=True,
            timeout=timeout,
            env=_child_env(),
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise HarnessError(f"{what} timed out after {timeout:.0f}s", exit_code=6) from exc


def require_docker_running() -> str:
    """Fail early and clearly if the Docker daemon is not up."""
    docker = _docker()
    probe = _run([docker, "version", "--format", "{{.Server.Version}}"], timeout=30, what="docker version")
    if probe.returncode != 0:
        raise HarnessError(
            "The Docker daemon is not responding.",
            exit_code=4,
            remedy=(
                "Start Docker Desktop. version2.md section 5.4 already records this as the one "
                "prerequisite that was found switched off."
            ),
        )
    return probe.stdout.strip()


def version_string(explicit: str | None = None) -> str:
    """Derive the version stamped into the binary and into ``agent-release.json``.

    ``AgentRelease.Version`` is what the whole fleet converges on, so it has to change
    whenever the code does. Git's short SHA does that for free; ``.dirty`` marks a build
    made from uncommitted changes, which must never be mistaken for a reproducible one.
    """
    if explicit:
        return explicit
    try:
        sha = subprocess.run(  # noqa: S603
            ["git", "-C", str(REPO_ROOT), "rev-parse", "--short=7", "HEAD"],
            capture_output=True, text=True, timeout=20, check=False,
        )
        dirty = subprocess.run(  # noqa: S603
            ["git", "-C", str(REPO_ROOT), "status", "--porcelain"],
            capture_output=True, text=True, timeout=20, check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return "0.0.0+unknown"
    if sha.returncode != 0 or not sha.stdout.strip():
        return "0.0.0+unknown"
    suffix = ".dirty" if dirty.stdout.strip() else ""
    return f"0.0.0+{sha.stdout.strip()}{suffix}"


def ensure_image(*, rebuild: bool = False) -> None:
    """Build ``framelink-build:arm64`` if it is not already in the layer store.

    Idempotent by construction: ``docker build`` with an unchanged Dockerfile is a cache
    hit. The first run pays one apt-get install under emulation; every later run is free.
    """
    docker = _docker()
    if not rebuild:
        exists = _run([docker, "image", "inspect", BUILD_IMAGE], timeout=60, what="image inspect")
        if exists.returncode == 0:
            ui.info(f"build image {BUILD_IMAGE} present")
            return

    ui.step(f"Building {BUILD_IMAGE} (first run installs the AOT toolchain under emulation)")
    result = _run(
        [
            docker, "build",
            "--platform", BUILD_PLATFORM,
            "-t", BUILD_IMAGE,
            "-f", str(DOCKERFILE),
            str(DOCKERFILE.parent),
        ],
        timeout=1800,
        what="docker build",
    )
    if result.returncode != 0:
        ui.block("docker build stderr", result.stderr[-4000:])
        raise HarnessError(f"docker build failed (exit {result.returncode})", exit_code=6)
    ui.ok(f"{BUILD_IMAGE} ready")


def build(
    *,
    project: str = AGENT_PROJECT,
    rid: str = RID,
    configuration: str = "Release",
    version: str | None = None,
    stock_image: bool = False,
    rebuild_image: bool = False,
) -> dict[str, Any]:
    """Produce the Native AOT binary and its update-feed metadata.

    Returns the ``AgentRelease``-shaped dict that was written to
    ``build/out/agent-release.json``.
    """
    docker = _docker()
    server = require_docker_running()
    ui.info(f"docker server {server}")

    project_dir = REPO_ROOT / project
    if not project_dir.is_dir():
        # The agent project is being written concurrently. This is an expected state, not
        # a harness fault, so it gets a diagnosis and a distinct exit code rather than a
        # container round trip that would fail three minutes later with the same news.
        listing = sorted(p.name for p in (REPO_ROOT / "src").glob("*")) if (REPO_ROOT / "src").is_dir() else []
        progress.mark(
            "build-path",
            "blocked",
            detail=f"{project} does not exist yet; src/ holds {listing or 'nothing'}",
        )
        raise HarnessError(
            f"Project {project} does not exist in this checkout.",
            exit_code=3,
            remedy=(
                f"src/ currently holds: {', '.join(listing) or '(nothing)'}\n"
                "The agent project is written by another workstream. Build again once it lands, "
                "or point elsewhere with --project."
            ),
        )

    if not stock_image:
        ensure_image(rebuild=rebuild_image)
    image = STOCK_SDK_IMAGE if stock_image else BUILD_IMAGE

    BUILD_OUT.mkdir(parents=True, exist_ok=True)
    # Clear stale metadata first: a failed build must not leave the previous run's
    # agent-release.json sitting there looking current.
    RELEASE_JSON.unlink(missing_ok=True)

    resolved_version = version_string(version)

    argv = [
        docker, "run", "--rm",
        "--platform", BUILD_PLATFORM,
        "-v", f"{REPO_ROOT.as_posix()}:/src",
        "-v", f"{BUILD_OUT.as_posix()}:/out",
        "-v", f"{NUGET_CACHE_VOLUME}:/root/.nuget/packages",
        "-e", f"FL_PROJECT={project}",
        "-e", f"FL_RID={rid}",
        "-e", f"FL_CONFIG={configuration}",
        "-e", f"FL_BINARY_NAME={BINARY_NAME}",
        "-e", f"FL_VERSION={resolved_version}",
        "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        "-e", "DOTNET_NOLOGO=1",
        image,
        "bash", BUILD_SCRIPT_IN_CONTAINER,
    ]

    ui.step(f"Building {project} for {rid} in {image} ({BUILD_PLATFORM}, emulated)")
    ui.info(f"version {resolved_version}")
    result = _run(argv, timeout=3600, what="docker run (build)")

    if result.stdout:
        ui.block("build.sh", result.stdout[-8000:])
    if result.returncode != 0:
        if result.stderr:
            ui.block("build.sh stderr", result.stderr[-4000:])
        detail = f"build.sh exited {result.returncode}"
        # A compiler error in the project under construction is not a fault in the build
        # path - the container ran, restored and invoked the compiler, which is the whole
        # of what this capability claims. Recording it as `failed` would send the next
        # session debugging the harness when the news is "the agent does not compile yet",
        # so those cases are `blocked` and carry the compiler's own first error line.
        first_error = next(
            (ln.strip() for ln in result.stdout.splitlines() if ": error " in ln), ""
        )
        if result.returncode in (3, 4, 5):
            progress.mark(
                "build-path",
                "blocked",
                detail=f"{detail} - project-side, not harness-side: {first_error or 'see build output'}",
            )
        else:
            progress.mark("build-path", "failed", detail=detail)
        raise HarnessError(
            detail,
            exit_code=6,
            remedy={
                3: "The project directory is missing inside the container - check the /src mount.",
                4: "No .csproj in the project directory.",
                5: "dotnet publish failed. The compiler output above is the diagnosis, and it is "
                   "a fault in the project being built, not in the build path.",
                6: "Publish produced no ELF. PublishAot may have silently fallen back.",
            }.get(result.returncode),
        )

    if not RELEASE_JSON.is_file() or not BINARY_PATH.is_file():
        progress.mark("build-path", "failed", detail="build.sh reported success but produced no artifact")
        raise HarnessError(
            f"build.sh exited 0 but {BINARY_PATH.name} / {RELEASE_JSON.name} are missing from build/out.",
            exit_code=6,
        )

    release: dict[str, Any] = json.loads(RELEASE_JSON.read_text(encoding="utf-8"))

    # Verify the container's hash against a local recomputation. The Fleet Manager serves
    # exactly these two numbers (AgentRelease.Sha256 / SizeBytes) and an agent refuses a
    # binary that does not match, so a wrong value here would break every frame's update.
    local_sha, local_size = hash_file(BINARY_PATH)
    if local_sha != release.get("sha256") or local_size != release.get("sizeBytes"):
        progress.mark("build-path", "failed", detail="agent-release.json disagrees with the binary on disk")
        raise HarnessError(
            "agent-release.json does not describe the binary next to it "
            f"(json {release.get('sha256')}/{release.get('sizeBytes')}B vs file {local_sha}/{local_size}B).",
            exit_code=6,
        )

    ui.ok(f"{BINARY_NAME}  {local_size:,} bytes  sha256 {local_sha}")

    artifact = {
        "path": str(BINARY_PATH.relative_to(REPO_ROOT)).replace("\\", "/"),
        "version": release["version"],
        "runtimeIdentifier": release["runtimeIdentifier"],
        "sha256": local_sha,
        "sizeBytes": local_size,
        "image": image,
        "builtUtc": progress.utcnow(),
    }
    progress.set_artifact("agentBinary", artifact)
    progress.bump("builds")
    progress.prove(
        "build-path",
        by="fl.py build",
        detail=f"{release['version']} {release['runtimeIdentifier']} {local_size} bytes in {image}",
    )
    return release


def hash_file(path: Path) -> tuple[str, int]:
    """Lowercase hex SHA-256 and byte size - the two fields the update feed serves."""
    import hashlib

    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
            size += len(chunk)
    return digest.hexdigest(), size


def current_release() -> dict[str, Any] | None:
    """The last built release metadata, or None if nothing has been built."""
    if not RELEASE_JSON.is_file() or not BINARY_PATH.is_file():
        return None
    try:
        return json.loads(RELEASE_JSON.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
