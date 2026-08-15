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

It also rewrites ``nextActions``, and that is the half worth being careful about. The list
used to be derived from the seven M0 capabilities and nothing else, so once M0 closed it had
exactly one thing left to say - "M0 is complete. Move to M1" - and it went on saying it
through M1, M2 and M2.5. A derived field with too small a vocabulary does not go quiet when
it runs out of things to know; it keeps answering, confidently, from the last question it
understood. So the derivation now reads the milestone ladder and the measured resource ledger
as well, and every entry it emits either names a command that can be pasted or names a
credential and what that credential unlocks.
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
from .progress import CONTROL_URL

_STATE_GLYPH = {
    "proven": "[x]",
    "unproven": "[ ]",
    "blocked": "[~]",
    "failed": "[!]",
}

_MILESTONE_GLYPH = {
    "done": "[x]",
    "in-progress": "[>]",
    "failing": "[!]",
    "not-started": "[ ]",
    "unknown": "[?]",
}

#: How a milestone arrived at its state, spelled out where a reader sees it. The distinction
#: is the point of the ladder: two of these mean a machine checked, one means a person looked.
_STATE_SOURCE = {
    "capabilities": "earned by harness commands",
    "resources": "measured from the repository",
    "witness": "witnessed on hardware, recorded with its evidence",
    "none": "nothing recorded",
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
    # Not a harness variable - no subcommand here reads it. It is probed anyway because the
    # work in front of this build needs a Fleet Manager running, and a session that discovers
    # the credential is missing only after starting one has already wasted the round trip.
    facts["FRAMELINK_OPERATOR_PASSWORD"] = (
        "set" if os.environ.get("FRAMELINK_OPERATOR_PASSWORD") else "NOT SET (Fleet Manager GUI locked)"
    )

    facts["mule"] = f"{MULE_USER}@{MULE_HOST} " + (
        "answers on tcp/22" if ssh.is_alive(MULE_HOST) else "SILENT on tcp/22"
    )
    facts["home assistant"] = f"{HA_URL} entity {HA_ENTITY}"
    facts["fleet manager"] = f"{CONTROL_URL} (dev; the mule's agent is pointed here)"

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
            f"{AGENT_PROJECT} is missing, so there is nothing to build or deploy.",
            "It is tracked, so this means an incomplete checkout rather than unwritten code.",
        ),
        (
            not os.environ.get("FRAMELINK_OPERATOR_PASSWORD"),
            "operator-password-unset",
            "FRAMELINK_OPERATOR_PASSWORD is not set, so the Fleet Manager cannot be logged into.",
            "Ask the user for it and supply it inline when starting src/FrameLink.Control. Without "
            "it the server still runs, but it serves the setup page and answers connecting frames "
            "'not-configured' - so nothing can be adopted and no reconcile can be watched.",
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
    """What a session with no memory should do next, in order.

    Derived, never authored - which is the correction to how this list went stale before. It
    used to enumerate only the seven M0 capabilities and end with "M0 is complete. Move to M1",
    and it kept saying that through the whole of M1, M2 and M2.5, because there was nothing
    else it could say. It now reads the milestone ladder and the resource ledger, so the
    frontier moves on its own and the credentials named are the ones this session is actually
    missing.

    Every entry either names a command that can be pasted, or names a credential and what it
    unlocks. Nothing here holds a credential value, ever.
    """
    actions: list[str] = []
    caps = data.get("capabilities", {})
    ledger = data.get("resources", {})

    def unproven(cap_id: str) -> bool:
        return caps.get(cap_id, {}).get("state") != "proven"

    in_flight = data.get("inFlight")
    if in_flight:
        actions.append(
            f"FIRST - a previous session died during `fl.py {in_flight.get('command')}` (started "
            f"{in_flight.get('startedUtc')}). Whatever it was changing is unverified; re-run that "
            "subcommand before trusting anything downstream of it."
        )

    actions.append(
        "Orient before touching anything: version2.md section 5.1 is the milestone ladder, "
        "reference/resource-catalog.md is the resource spec M3 is migrating, "
        "reference/v1-state-inventory.txt is the parity target, `git log --oneline -40` is the "
        "reasoning record. The 'orientation' block in this file says what each one is for."
    )

    # --- M0 upkeep. These only fire if something regressed; M0 is closed. ---------------
    if unproven("test-runner"):
        actions.append("python tools/harness/fl.py test  - prove the suite runs and its exit code propagates")
    if unproven("build-path"):
        if not (REPO_ROOT / AGENT_PROJECT).is_dir():
            actions.append(
                f"{AGENT_PROJECT} is missing from this checkout. The build path is wired and its "
                "container is built; the project is tracked, so restore it before building."
            )
        else:
            actions.append("python tools/harness/fl.py build  - produce the linux-arm64 AOT binary")

    actions.append(
        f"dotnet run --project {TEST_PROJECT}  - the only gate between a change and a release "
        "(version2.md section 7.2: no red test ships). Not `dotnet test`, which discovers zero "
        "tests under xunit.v3 on this SDK. `fl.py test` wraps it with telemetry off and a "
        "deadline."
    )

    # --- credentials, each named with what it unlocks and nothing else ------------------
    if not os.environ.get("FL_PW"):
        actions.append(
            "Ask the user for FL_PW and supply it inline (FL_PW='...' python tools/harness/fl.py "
            "<cmd>) to unlock deploy / collect / any SSH to the mule. Never store it."
        )
    else:
        if unproven("deploy"):
            actions.append("python tools/harness/fl.py deploy  - push the binary and the unit to the mule")
        if unproven("collect"):
            actions.append("python tools/harness/fl.py collect  - screenshot + journal tail")
        actions.append(
            "FL_PW='...' python tools/harness/fl.py build && ... deploy && ... collect  - the "
            "closed loop; `collect` is how you see the console stage on the panel."
        )
    if not os.environ.get("FRAMELINK_OPERATOR_PASSWORD"):
        actions.append(
            "Ask the user for FRAMELINK_OPERATOR_PASSWORD - the Fleet Manager's single operator "
            "credential. Nothing can be adopted or watched reconciling without it."
        )
    actions.append(
        "Start the dev Fleet Manager, which is down: FRAMELINK_OPERATOR_PASSWORD set, then "
        f"dotnet run --project src/FrameLink.Control -- --urls {CONTROL_URL.replace('10.20.30.200', '0.0.0.0')}. "
        f"The mule's agent is pointed at {CONTROL_URL} and is retrying forever; its journal is a "
        "wall of connection warnings until this is up."
    )
    if not os.environ.get("FL_HA_TOKEN"):
        actions.append(
            f"Ask the user for FL_HA_TOKEN (a long-lived token from {HA_URL}) to unlock "
            f"`fl.py power` on {HA_ENTITY}. Relay operations are cumulative mechanical wear."
        )
    elif unproven("power-cycle"):
        actions.append("python tools/harness/fl.py power state  - read the relay without switching it")

    # --- the frontier, derived from the ledger so it cannot go stale --------------------
    implemented, total = ledger.get("implemented"), ledger.get("catalogTotal")
    verified = ledger.get("hardwareVerifiedCount")
    if implemented is not None and total is not None:
        actions.append(
            f"THE FRONTIER (M3): {implemented} of {total} catalog resources are implemented and "
            f"only {verified} have ever converged on hardware. The first full provision of the "
            "whole catalog on a frame has not happened and is the largest unknown in the build. "
            "Do it against a freshly flashed card, and watch it on the panel."
        )
        if implemented < total:
            actions.append(
                f"Finish the catalog: {total - implemented} resources are still unimplemented. "
                "reference/resource-catalog.md is the spec; the count above is read from the "
                "assertion in tests/FrameLink.Tests/AgentResourceGraphTests.cs, which goes red "
                "when the graph and the catalog disagree."
            )
    actions.append(
        "Settle the WirePlumber question, which is one read-only command and has blocked the "
        "audio mixer resources' design since the catalog was written: list "
        "~/.local/state/wireplumber/ on the mule. If stored device state is there it is a second "
        "owner of every audio.mixer.* value and wins, because it applies after alsa-restore. See "
        "reference/resource-catalog.md, suspected-reverts item 4."
    )
    actions.append(
        "M2.5 is NOT done and cannot be finished at this desk: the generator is built and "
        "measured against the real base image, but its acceptance test is to flash a card and "
        "watch a row appear, and version2.md section 5.3 item 3 records that no SD card reader is "
        "attached. Ask the user for one."
    )
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

    current = data.get("currentMilestone")
    # "First row not done", not "what is being worked on". They differ right now and the
    # difference is informative: M2.5 cannot be finished at a desk with no SD card reader, so
    # M3 runs ahead of it. The evidence line under each row is where that is explained.
    ui.step(f"Milestones (version2.md section 5.1) - first row not done is {current}")
    for milestone in data.get("milestones", []):
        glyph = _MILESTONE_GLYPH.get(milestone.get("state", ""), "[?]")
        marker = " <- here" if milestone.get("id") == current else ""
        print(f"    {glyph} {str(milestone.get('id')):<5} {milestone.get('title', '')}{marker}")
        source = _STATE_SOURCE.get(milestone.get("stateFrom", ""), "")
        evidence = milestone.get("evidence") or {}
        if source and milestone.get("state") != "not-started":
            print(f"              {milestone.get('state')} - {source}")
        if evidence.get("what"):
            print(f"              {evidence['what']}")
        if evidence.get("notWitnessed"):
            print(f"              NOT covered: {evidence['notWitnessed']}")

    ledger = data.get("resources", {})
    if ledger:
        print()
        ui.step("Resources (M3 ledger - both counts measured from the repository on every write)")
        ui.kv(
            {
                "catalog total": f"{ledger.get('catalogTotal')}  ({ledger.get('catalogTotalReadFrom')})",
                "implemented": f"{ledger.get('implemented')}  ({ledger.get('implementedReadFrom')})",
                "hardware-verified": (
                    f"{ledger.get('hardwareVerifiedCount')}  "
                    f"{', '.join(ledger.get('hardwareVerified', []))}"
                ),
            }
        )
        ui.info("implemented means code with tests. hardware-verified means it converged on a frame.")

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
