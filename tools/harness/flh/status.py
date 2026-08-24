"""``fl.py status`` - where the build stands, and what this session can actually do.

Two halves, deliberately separate:

* **Recorded state** - read back from ``tools/harness/progress.json``. This is history: what
  has been proven, by which command, when.
* **Live probe** - re-checked now: is Docker up, is FL_PW present, does the mule answer, is
  there a Home Assistant token, and **what agent version is the Fleet Manager serving**. This
  is capability: what this particular session can do.

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
from . import feed, progress, ssh, ui
from .config import (
    HA_ENTITY,
    HA_URL,
    MULE_HOST,
    MULE_USER,
    PROGRESS_FILE,
    REPO_ROOT,
    RID,
    AGENT_PROJECT,
    TEST_PROJECT,
    HarnessError,
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


def _control_answers() -> bool:
    """Ask the dev Fleet Manager whether it is serving, right now.

    This used to be asserted rather than asked. The next-action list said flatly that the
    Fleet Manager "is down" and told every session to start one by hand - true when it was
    written, because starting it by hand was the only way to have one. The dev stack then
    moved into a Docker container that comes up on its own and stays up, and the assertion
    went on being emitted, wrongly, by the one command a resuming session is told to run
    first. A derived field with no way to check itself does not go quiet when the world
    moves; it keeps answering from the last question it understood. So: probe it.

    Anything that answers HTTP counts, including a 401 or a 404 - the question is whether
    something is serving on that address, not whether this process may talk to it.
    """
    import urllib.error
    import urllib.request

    try:
        with urllib.request.urlopen(CONTROL_URL + "/", timeout=5) as response:  # noqa: S310
            return response.status < 500
    except urllib.error.HTTPError:
        return True
    except (OSError, ValueError):
        return False


def _served_agent(control_answering: bool) -> tuple[dict[str, Any] | None, str]:
    """What the Fleet Manager serves for the runtime the fleet runs, or why that is unknown.

    This is the number that decides whether a deploy sticks. ``build/out`` says what this
    workstation last compiled; section 2.8 says the frame runs whatever its Fleet Manager
    serves, upgrade or downgrade, at the next handshake and hourly after. A status report
    that shows only the first is showing the half that stops being true first - the same
    shape of half-answer :mod:`flh.deploy` was fixed for.

    It goes through :mod:`flh.feed` rather than opening its own connection, deliberately:
    one client, one route, one set of failure messages, so what ``status`` reports and what
    ``deploy`` gates on can never drift apart.

    Returns ``(release, reason)``. Exactly one is filled. **A feed that could not be read
    yields no release and a reason**, never an empty agreement - an unread feed and a feed
    that agrees are the two answers this function most has to keep apart.
    """
    if not control_answering:
        # Skipped rather than attempted, so a silent server costs one timeout in this report
        # instead of two. The reason names what was actually observed.
        return None, f"UNCHECKED - nothing is answering at {CONTROL_URL}"
    try:
        return feed.served_release(RID, timeout=5.0), ""
    except HarnessError as exc:
        # Including the 404 that means "this Fleet Manager was built from a checkout with no
        # agent in it", which is a real state rather than a fault - and still not agreement.
        return None, f"UNCHECKED - {exc}"


def _served_agent_fact(
    release: dict[str, Any] | None, served: dict[str, Any] | None, reason: str
) -> str:
    """One line for the probe block: what is served, and whether it is what was built."""
    if served is None:
        return reason
    summary = f"{served['version']} {served.get('runtimeIdentifier', RID)} sha256 {str(served.get('sha256', ''))[:12]}..."
    if release is None:
        return f"{summary} - nothing in build/out to compare it against"
    try:
        local_sha, _ = build_mod.hash_file(build_mod.BINARY_PATH)
    except OSError as exc:
        return f"{summary} - build/out/{build_mod.BINARY_PATH.name} could not be hashed ({exc.__class__.__name__})"
    agrees, why = feed.compare(release, local_sha, served)
    return f"{summary} - " + ("agrees with build/out" if agrees else f"DIFFERS from build/out: {why}")


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
    control_answering = _control_answers()
    facts["fleet manager"] = f"{CONTROL_URL} " + (
        "answering (dev; the mule's agent is pointed here)"
        if control_answering
        else "SILENT (dev; the mule's agent is pointed here and is retrying forever)"
    )

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

    # Beside the built artifact rather than instead of it. These two being equal is the whole
    # difference between a deploy that holds and one the fleet erases on its next tick.
    served, reason = _served_agent(control_answering)
    facts["served agent"] = _served_agent_fact(release, served, reason)

    # Recorded as well as printed, because a session that reads progress.json instead of
    # running this command needs the same half of the answer. It carries its own timestamp and
    # its own `checked` flag, so it can never be read as a live reading or as agreement.
    progress.set_artifact(
        "servedAgent",
        {
            "feedUrl": feed.release_url(RID),
            "runtimeIdentifier": RID,
            "checked": served is not None,
            "version": served.get("version") if served else None,
            "sha256": served.get("sha256") if served else None,
            "sizeBytes": served.get("sizeBytes") if served else None,
            "reason": reason or None,
            "checkedUtc": progress.utcnow(),
        },
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
    if "answering" not in str(facts.get("fleet manager", "")):
        actions.append(
            "Start the dev Fleet Manager, which is not answering: docker compose -f "
            "deploy/fleet-manager/framelink.dev.yml up -d, with the credentials env file "
            "supplied as that stack expects. It used to be started by hand with dotnet run, "
            "and that still works for a one-off, but the container is what the frame has been "
            f"talking to. The mule's agent is pointed at {CONTROL_URL} and retries forever; "
            "its journal is a wall of connection warnings until this is up."
        )

    # The served build, not the built one. A frame runs what its Fleet Manager serves, so this
    # is the entry that decides whether the next deploy survives its own report.
    served_fact = str(facts.get("served agent", ""))
    if served_fact.startswith("UNCHECKED"):
        actions.append(
            "The served agent version is UNCHECKED and must not be read as agreement: "
            f"{served_fact.removeprefix('UNCHECKED - ')} Nothing here knows what the fleet is "
            f"converging on until {feed.release_url(RID)} answers. `fl.py deploy` says the same "
            "and continues anyway; `fl.py deploy --allow-feed-drift` is only for a binary you "
            "intend to watch."
        )
    elif "DIFFERS" in served_fact:
        actions.append(
            "The Fleet Manager serves a different agent than build/out holds, so a deploy of "
            "the local binary would be undone within the hour (version2.md section 2.8; "
            "measured at six seconds on 2026-08-23). The agent is baked into the image from "
            "build/out, so serving this build means rebuilding the image: bash "
            "deploy/fleet-manager/build-image.sh, then docker compose -p framelink -f "
            "deploy/fleet-manager/framelink.dev.yml up -d. docs/15-local-fleet-manager.md "
            "step 10 is the check."
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
        # The agent's graph is allowed to exceed the catalog - kiosk.config.albums is in the
        # graph and deliberately not in reference/resource-catalog.md - so this says which
        # number is which rather than subtracting one from the other and printing "80 of 79".
        #
        # It also stops short of saying "all N catalog resources are implemented", which was
        # false: pkg.git is in the catalog and deliberately not in the graph, so the excess is a
        # net of two-not-in-the-catalog less one-not-in-the-graph. The two numbers are stated and
        # the reader is sent to the assertion that names the memberships, rather than being given
        # a subtraction dressed up as a census.
        scope = (
            f"the agent's graph carries {implemented} resources against the catalog's {total} "
            f"({implemented - total} net beyond it; AgentResourceGraphTests names which)"
            if implemented > total
            else f"{implemented} of {total} catalog resources are implemented"
        )
        actions.append(
            f"THE FRONTIER (M3): {scope}, and {verified} of {implemented} can still be shown to "
            "have been reboot-verified on hardware. That is a floor, not a census - the mule has "
            "reached full convergence, and what is thin is the evidence retained for each row, "
            "not the convergence. Re-establish it against a freshly flashed card, and watch it "
            "on the panel."
        )
        if implemented < total:
            actions.append(
                f"Finish the catalog: {total - implemented} resources are still unimplemented. "
                "reference/resource-catalog.md is the spec; the count above is read from the "
                "assertion in tests/FrameLink.Tests/AgentResourceGraphTests.cs, which goes red "
                "when the graph and the catalog disagree."
            )
    # The WirePlumber question that stood here from the day the catalog was written is
    # answered and is not a next action any more. Decision 80 records it: WirePlumber is a
    # second owner of the ALSA mixer, measured on the frame - ~/.local/state/wireplumber/ held
    # only stream-properties until the first successful `wpctl set-volume`, after which
    # default-routes appeared, which is the second owner writing itself down. The consequences
    # shipped with it: every audio.mixer.* Observe sits behind the session gate, and
    # audio.wireplumber.playback-volume owns the value at the layer that sets it.
    actions.append(
        "M2.5 is NOT done, and what is missing is the flash rather than the hardware: the "
        "generator is built and measured against the real base image, but its acceptance test "
        "is to flash a card and watch a row appear, and nothing generated has ever been written "
        "to a card. A reader has been attached since 2026-08-23. Run `fl.py cards list` before "
        "touching anything - it is the register, it says which card is blank, which is in a "
        "frame and which must never be written, and it is maintained where this sentence is "
        "not. Flash the blank one, boot it, and watch the adoption queue."
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
    # difference is informative: M3 ran ahead of M2.5 because M2.5's acceptance test needs a
    # card flashed and booted and nobody has done it, not because anything blocks it. It was
    # blocked once - there was no SD card reader - and that reason outlived the condition by a
    # week in three places. The evidence line under each row is where the current one lives.
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
