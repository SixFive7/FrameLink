"""``fl.py status`` - where the build stands, and what this session can actually do.

Two halves, deliberately separate:

* **Recorded state** - read back from ``tools/harness/progress.json``. This is history: what
  has been proven, by which command, when.
* **Live probe** - re-checked now: is Docker up, is FL_PW present, does the mule answer, is
  there a Home Assistant token. This is capability: what this particular session can do.

Keeping them apart is the point. "The deploy path was proven yesterday" and "this session
cannot deploy because FL_PW is unset" are both true at once, and a resuming session needs
to see both without confusing one for the other.

The probe writes its conclusions back as blockers, so the progress file always names the
current obstacles rather than requiring someone to re-derive them.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from typing import Any

from . import build as build_mod
from . import progress, ssh, ui
from .config import (
    HA_ENTITY,
    HA_URL,
    MULE_HOST,
    MULE_USER,
    PROGRESS_FILE,
    REPO_ROOT,
    AGENT_PROJECT,
    TEST_PROJECT,
)

_STATE_GLYPH = {
    "proven": "[x]",
    "unproven": "[ ]",
    "blocked": "[~]",
    "failed": "[!]",
}


def probe() -> dict[str, Any]:
    """Re-check everything the harness depends on, right now."""
    facts: dict[str, Any] = {}

    docker = shutil.which("docker")
    if docker:
        try:
            result = subprocess.run(  # noqa: S603
                [docker, "version", "--format", "{{.Server.Version}}"],
                capture_output=True, text=True, timeout=30, check=False,
            )
            facts["docker"] = (
                f"server {result.stdout.strip()}" if result.returncode == 0 else "installed, daemon not responding"
            )
        except (OSError, subprocess.SubprocessError) as exc:
            facts["docker"] = f"probe failed: {exc}"
    else:
        facts["docker"] = "not on PATH"

    dotnet = shutil.which("dotnet")
    if dotnet:
        try:
            result = subprocess.run(  # noqa: S603
                [dotnet, "--version"], capture_output=True, text=True, timeout=30, check=False
            )
            facts["dotnet sdk"] = result.stdout.strip() or "unknown"
        except (OSError, subprocess.SubprocessError):
            facts["dotnet sdk"] = "probe failed"
    else:
        facts["dotnet sdk"] = "not on PATH"

    try:
        import paramiko

        facts["paramiko"] = paramiko.__version__
    except ImportError:
        facts["paramiko"] = "NOT INSTALLED"

    facts["FL_PW"] = "set" if os.environ.get("FL_PW") else "NOT SET (mule commands unavailable)"
    facts["FL_HA_TOKEN"] = "set" if os.environ.get("FL_HA_TOKEN") else "NOT SET (power commands unavailable)"

    facts["mule"] = f"{MULE_USER}@{MULE_HOST} " + (
        "answers on tcp/22" if ssh.is_alive(MULE_HOST) else "SILENT on tcp/22"
    )
    facts["home assistant"] = f"{HA_URL} entity {HA_ENTITY}"

    facts["agent project"] = (
        "present" if (REPO_ROOT / AGENT_PROJECT).is_dir() else f"{AGENT_PROJECT} DOES NOT EXIST YET"
    )
    facts["test project"] = "present" if (REPO_ROOT / TEST_PROJECT).is_dir() else "MISSING"

    release = build_mod.current_release()
    facts["built artifact"] = (
        f"{release['version']} {release['runtimeIdentifier']} {release['sizeBytes']:,}B "
        f"sha256 {release['sha256'][:12]}..."
        if release
        else "nothing in build/out"
    )
    return facts


def _sync_blockers(facts: dict[str, Any]) -> None:
    """Turn the live probe into named blockers in the progress file."""
    checks = [
        (
            not os.environ.get("FL_PW"),
            "fl-pw-unset",
            "FL_PW is not set, so no command can reach the mule.",
            "Supply the mule password inline for this session: FL_PW='...' python tools/harness/fl.py <cmd>",
        ),
        (
            not os.environ.get("FL_HA_TOKEN"),
            "ha-token-unset",
            "FL_HA_TOKEN is not set, so the smart plug cannot be switched.",
            f"Create a long-lived access token at {HA_URL} and pass it as FL_HA_TOKEN.",
        ),
        (
            not (REPO_ROOT / AGENT_PROJECT).is_dir(),
            "agent-project-missing",
            f"{AGENT_PROJECT} does not exist yet, so there is nothing to build or deploy.",
            "Another workstream is writing it. Re-run `fl.py build` once it lands.",
        ),
        (
            "not responding" in str(facts.get("docker", "")) or "not on PATH" in str(facts.get("docker", "")),
            "docker-down",
            "The Docker daemon is not responding, so the arm64 build container cannot run.",
            "Start Docker Desktop (version2.md section 5.4 item 3).",
        ),
    ]
    for failing, blocker_id, what, needs in checks:
        if failing:
            progress.add_blocker(blocker_id, what, needs)
        else:
            progress.clear_blocker(blocker_id)


def _derive_next_actions(data: dict[str, Any], facts: dict[str, Any]) -> list[str]:
    """What a session with no memory should do next, in order."""
    actions: list[str] = []
    caps = data.get("capabilities", {})

    def unproven(cap_id: str) -> bool:
        return caps.get(cap_id, {}).get("state") != "proven"

    if unproven("test-runner"):
        actions.append("python tools/harness/fl.py test  - prove the suite runs and its exit code propagates")
    if unproven("build-path"):
        if not (REPO_ROOT / AGENT_PROJECT).is_dir():
            actions.append(
                f"WAIT: {AGENT_PROJECT} does not exist yet. The build path is wired and its container "
                "is built; it needs the agent project to compile."
            )
        else:
            actions.append("python tools/harness/fl.py build  - produce the linux-arm64 AOT binary")
    if not os.environ.get("FL_PW"):
        actions.append("Set FL_PW for this session to unlock deploy / collect")
    else:
        if unproven("deploy"):
            actions.append("python tools/harness/fl.py deploy  - push the binary and the unit to the mule")
        if unproven("collect"):
            actions.append("python tools/harness/fl.py collect  - screenshot + journal tail")
    if not os.environ.get("FL_HA_TOKEN"):
        actions.append("Set FL_HA_TOKEN to unlock power control")
    elif unproven("power-cycle"):
        actions.append("python tools/harness/fl.py power state  - read the relay without switching it")
    if not actions:
        actions.append("M0 is complete. Move to M1 (walking skeleton).")
    return actions


def show(*, json_output: bool = False) -> dict[str, Any]:
    """Print the status report and refresh the progress file's live sections."""
    facts = probe()
    _sync_blockers(facts)

    data = progress.load()
    actions = _derive_next_actions(data, facts)
    progress.set_next_actions(actions)
    data = progress.load()

    if json_output:
        import json

        print(json.dumps({"progress": data, "probe": facts}, indent=2))
        return data

    milestone = data.get("milestone", {})
    ui.step(f"Milestone {milestone.get('id')} - {milestone.get('title')}: {milestone.get('state')}")
    ui.info(milestone.get("doneWhen", ""))

    print()
    ui.step("Capabilities (recorded - earned by a command that actually ran)")
    for cap_id, cap in data.get("capabilities", {}).items():
        glyph = _STATE_GLYPH.get(cap.get("state", ""), "[?]")
        line = f"    {glyph} {cap_id:<14} {cap.get('title', '')}"
        print(line)
        detail = cap.get("detail")
        proven_by, proven_utc = cap.get("provenBy"), cap.get("provenUtc")
        if proven_by:
            print(f"                        proven by {proven_by} at {proven_utc}")
        if detail:
            print(f"                        {detail}")
        blocked_now = cap.get("currentlyUnrunnable")
        if blocked_now:
            print(f"                        not runnable in this session: {blocked_now}")

    print()
    ui.step("This session (probed now)")
    ui.kv(facts)

    counters = data.get("counters", {})
    if any(counters.values()):
        print()
        ui.step("Counters")
        ui.kv({k: v for k, v in counters.items()})
        if counters.get("relayOperations"):
            ui.info("relayOperations is cumulative mechanical wear on a real relay - keep it small")

    in_flight = data.get("inFlight")
    if in_flight:
        print()
        ui.warn(
            f"A previous session died during `{in_flight.get('command')}` "
            f"(started {in_flight.get('startedUtc')}). Whatever it was changing is unverified."
        )

    blockers = data.get("blockers", [])
    if blockers:
        print()
        ui.step("Blockers")
        for blocker in blockers:
            print(f"    - {blocker.get('id')}: {blocker.get('what')}")
            print(f"      needs: {blocker.get('needs')}")

    print()
    ui.step("Next actions")
    for action in actions:
        print(f"    - {action}")

    print()
    ui.info(f"progress file: {PROGRESS_FILE}")
    return data
