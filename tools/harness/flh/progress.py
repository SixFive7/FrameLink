"""The resumable progress file - version2.md section 5.5.

    "Long runs lose context, so progress is written to disk continuously and any session
     can resume mid-milestone."

This module owns ``tools/harness/progress.json``. That file is deliberately **not**
gitignored (``.gitignore`` covers ``tools/harness/runs/`` only): it is the record a fresh
session with no memory of anything reads first to learn exactly where the build stands.

Design rules the shape follows
------------------------------
1. **Machine-readable first, human-readable anyway.** Plain JSON, two-space indent, one
   fact per key, and a canonical key order rewritten on every save, so a git diff shows what
   actually changed rather than where a key happened to be appended.
2. **States are earned, never asserted.** A capability becomes ``proven`` only when the
   command that proves it actually ran and succeeded; the harness writes that transition,
   nobody edits it in by hand. ``provenBy`` names the command and ``provenUtc`` the moment.
3. **Continuous, not end-of-run.** Every subcommand writes an ``inFlight`` marker *before*
   doing the work and clears it after. A file still holding an ``inFlight`` marker is the
   signature of a session that died mid-command, which is exactly what the resuming
   session needs to know and cannot otherwise discover.
4. **Atomic.** Written to a temp file in the same directory and ``os.replace``d, so a kill
   between two writes can never leave truncated JSON - the one failure that would make the
   resume mechanism worse than useless.
5. **Blockers are first-class.** Anything the harness cannot do, and what it would need in
   order to do it, is listed rather than implied by an absence.

What rule 2 could not carry, and what was done about it
-------------------------------------------------------
Rule 2 was written when M0 was the whole world, and M0 is the one milestone every part of
which some ``fl.py`` subcommand can perform. M1, M2 and M2.5 are not like that. A frame
appearing in the adoption queue, an operator pressing **Adopt**, nine resources converging
and staying converged across a real reboot, a console stage legible on a physical panel -
no command in this harness can perform any of those, so under rule 2 alone they can never
be recorded at all, and the file's answer to "where does the build stand" stays frozen at
the last thing the harness itself happened to do. That is exactly how this file came to
report M0 as the frontier while three later milestones were already built.

So there are now **two** ways a state gets here, and they are labelled rather than blended:

* ``proven`` - earned by a harness command. Only :func:`prove` writes it, from the code path
  that ran the command. Unchanged, and still the only word that means "this harness did it".
* ``witnessed`` - observed on real hardware by a person, and recorded **in this module** with
  its provenance: what was seen, when, on which host, where the durable evidence lives, and
  what the observation does *not* cover. It lives in code and not in the JSON because the JSON
  is a generated artifact - :func:`save` rewrites the derived sections on every write, so a
  hand-edit there survives until the next ``fl.py`` invocation and no longer. In ``_MILESTONES``
  it is a reviewable diff with the evidence attached, which is the thing a bare assertion in a
  data file can never be.

A third kind is derived rather than recorded at all: the M3 ledger below counts what the
repository itself says, on every save, from the two files that are authoritative for those
numbers. A count that is measured cannot go stale, and going stale is the failure this whole
module exists to prevent.

Why the schema identifier did not move
--------------------------------------
This revision changes the shape substantially - a milestone ladder instead of one milestone,
a resource ledger, an orientation block - and still leaves ``SCHEMA`` alone. That is
deliberate. ``load`` treats an unrecognised schema as a file to step around, and a longer-
running subcommand holds *its* copy of this module in memory for the whole run: the shape was
revised while a ``fl.py build`` was thirty minutes into an emulated container build. Bumping
the identifier would have made that already-running process treat the new file as foreign at
its ``finally``, which is the one moment the resume mechanism must not be the thing that
loses the record. The keys added here are additive, an older writer preserves every one of
them, and the two fields it does overwrite (``readMeFirst``, and the legacy singular
``milestone`` it re-creates) are repaired by :func:`_refresh` on the next save. ``load`` now
migrates rather than blanks, so the next bump is free.
"""

from __future__ import annotations

import json
import os
import re
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any

from .config import HA_ENTITY, HA_URL, MULE_HOST, MULE_USER, PROGRESS_FILE, REPO_ROOT

SCHEMA = "framelink.harness.progress/1"

#: Bound on the event log. Enough to see a whole working session, small enough that the
#: file stays readable and diffable.
LOG_LIMIT = 200

#: The dev Fleet Manager the mule is pointed at. Not in ``config.py`` because no harness
#: subcommand talks to it - it is here because a resuming session cannot find it anywhere
#: else, and the mule's journal is otherwise the only record that this address is the one.
CONTROL_URL = "http://10.20.30.200:5199"

_READ_ME_FIRST = [
    "This is the FrameLink v2 progress file. It is written continuously by "
    "tools/harness/fl.py and is the authoritative answer to 'where does the build stand' "
    "for a session that remembers nothing. Read it top to bottom: nextActions and blockers "
    "are what to do, milestones is where the build is, capabilities is what this harness "
    "has been proven able to do, resources is the M3 ledger, orientation names the other "
    "durable artifacts, environment names the hosts and the credentials.",
    "Run `python tools/harness/fl.py status` first. It re-probes this session live (docker, "
    "dotnet, paramiko, the mule on tcp/22, which credentials are present) and rewrites "
    "blockers and nextActions from what it finds, so a stale obstacle cannot survive the "
    "one command a resuming session is told to run.",
    "Two words mean different things and are never blended. 'proven' is earned: some fl.py "
    "subcommand ran, succeeded, and wrote that transition itself - see provenBy and "
    "provenUtc. 'witnessed' is observed on real hardware by a person and recorded in "
    "tools/harness/flh/progress.py with its evidence, because no command in this harness "
    "can perform an adoption or read a physical panel. Nothing here is ever true because "
    "somebody typed it into the JSON: save() rewrites every derived section, so a hand-edit "
    "lasts until the next fl.py invocation and no longer.",
    "To record a new hardware observation, add it to _MILESTONES or "
    "_HARDWARE_VERIFIED_RESOURCES in tools/harness/flh/progress.py with what was seen, when, "
    "on which host and where the evidence lives. That is a reviewable commit, which is the "
    "point. Do not write it into this file.",
    "If inFlight is not null a previous session died in the middle of that command and "
    "whatever it was changing is unverified - which matters most for `deploy` and `power`, "
    "the two subcommands that change something outside this repository.",
    "Credentials are supplied inline per session and never stored, logged or defaulted "
    "(CLAUDE.md section 1.2). The environment block names which variable unlocks what; it "
    "holds no values and must never be made to.",
]

#: The six pieces M0 is made of, plus the acceptance condition that binds them.
#: version2.md section 5.1: "A code change reaches the mule and is verified with no human help:
#: build path, deploy script, power-cycle control, screenshot + journal collection,
#: resumable progress file, test runner."
_CAPABILITIES: list[tuple[str, str, str]] = [
    (
        "build-path",
        "Native AOT linux-arm64 binary built in an emulated container",
        "fl.py build",
    ),
    (
        "test-runner",
        "Test suite runs and propagates its exit code",
        "fl.py test",
    ),
    (
        "progress-file",
        "Progress is written to disk continuously and survives a dead session",
        "any fl.py subcommand",
    ),
    (
        "deploy",
        "Binary and systemd unit reach the mule idempotently and the service restarts",
        "fl.py deploy",
    ),
    (
        "power-cycle",
        "Smart plug switches under harness control with wrong-entity and wear guards",
        "fl.py power",
    ),
    (
        "collect",
        "Screenshot and journal tail come back from the mule",
        "fl.py collect",
    ),
    (
        "closed-loop",
        "A code change reaches the mule and is verified with no human help (M0 done)",
        "fl.py build && fl.py deploy && fl.py collect",
    ),
]

#: version2.md section 5.1's ladder, verbatim in ``doneWhen`` and in order. ``stateFrom``
#: says how each row's state is arrived at and is the whole honesty mechanism:
#:
#:   capabilities  derived from the seven M0 capabilities below. Earned, never written here.
#:   resources     derived from the repository on every save. Measured, never written here.
#:   witness       observed on hardware and recorded here with its evidence, because no
#:                 command in this harness could ever perform it.
#:   none          nothing has happened yet.
#:
#: A ``witness`` row carries ``evidence`` with five fields, and the fifth is the one that
#: makes it worth trusting: ``notWitnessed`` states what the observation did *not* cover, so
#: the record cannot quietly grow into a bigger claim than the thing somebody actually saw.
_MILESTONES: list[dict[str, Any]] = [
    {
        "id": "M0",
        "title": "Autonomy harness",
        "doneWhen": (
            "A code change reaches the mule and is verified with no human help: build path, "
            "deploy script, power-cycle control, screenshot + journal collection, resumable "
            "progress file, test runner."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "capabilities",
    },
    {
        "id": "M1",
        "title": "Walking skeleton",
        "doneWhen": (
            "Agent connects -> appears pending -> adopted in the GUI -> reconciles one trivial "
            "resource -> self-updates from the Fleet Manager. Every integration risk retired at "
            "once."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "witness",
        "state": "done",
        "evidence": {
            "how": "observed-on-hardware",
            "what": (
                "The agent on the mule connected to the dev Fleet Manager, appeared in the "
                "adoption queue as a pending device, was adopted from the GUI under the name "
                "'Mule' (device T1RJ-6JCQ-9HN8-3920, hardware serial 19aa037e525b27b6), went "
                "online, and streamed live reconcile telemetry that the GUI rendered."
            ),
            "whenUtc": "2026-08-15",
            "where": (
                f"mule {MULE_USER}@{MULE_HOST}, Fleet Manager running on the workstation at "
                f"{CONTROL_URL}"
            ),
            "recordedIn": (
                "git log (commits 7e41cb2 control console, 43dfc51 protocol, 68ffbdf GUI); the "
                "device identity is visible in every journal capture under tools/harness/runs/ "
                "(gitignored, local only); the adoption row itself lives in the Fleet Manager's "
                "SQLite database outside this repository"
            ),
            "notWitnessed": (
                "The self-update leg was not separately re-observed in that session. The update "
                "path is wired and running - the mule's journal shows repeated update checks "
                "against /agent/release/linux-arm64 - but they are failing right now because the "
                "dev Fleet Manager is not up, so a successful binary swap has not been watched."
            ),
        },
    },
    {
        "id": "M2",
        "title": "Reconciler engine",
        "doneWhen": (
            "DAG, status vocabulary, retry/backoff, reboot-verified apply, escalation ladder, "
            "live telemetry, console and browser narration."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "witness",
        "state": "done",
        "evidence": {
            "how": "observed-on-hardware",
            "what": (
                "Three separate observations, all on the mule. (1) 2026-08-15: nine resources "
                "converged and each was verified after a real reboot, not after a service "
                "restart, and the console stage rendered on the physical DSI panel and read "
                "correctly. (2) 2026-08-16: the console stage's own terminal and the handover "
                "both ways - fl-agent holds /dev/tty8 while agetty keeps /dev/tty1, "
                "/sys/class/tty/tty0/active reads tty8 with no compositor up, getty@tty1 is "
                "still active so 5.5's physical login survives, and the EIO line 874823b removed "
                "does not appear on this boot; with a compositor up the log carries 'The screen "
                "is now the product's - tty1 is in front'. PASS on 874823b's own criteria. "
                "(3) 2026-08-16: 2.5 rung 3's operator retry used for the first time in "
                "production, over POST /api/devices/{id}/retry/{resource} - six presses, all "
                "HTTP 200 'sent', each one reaching the frame's journal as 'the attempt budget "
                "was reset by the Fleet Manager'. boot.autologin.getty-tty1 left Escalated and "
                "its twelve dependents left Blocked; session.bash-profile-exec-labwc went "
                "straight to InSync from the retry alone with no reboot, which is what a "
                "resource that was never actually broken looks like when the ladder lets go of "
                "it. The frame reached 72 of 79 in-sync."
            ),
            "whenUtc": "2026-08-16",
            "where": f"mule {MULE_USER}@{MULE_HOST}, physical panel on card0-DSI-2",
            "recordedIn": (
                "git log (011cf3a reconciler engine, 28a5264 agent seams and honest console, "
                "9eedf91 telemetry payloads, 6d77144 console stage, 874823b console stage on "
                "tty8, 97862c6 and a95958b the two halves of retry, fe00f40 the retry button and "
                "its bundle, 74cdedf the session-readiness gate); the panel was photographed by "
                "`fl.py collect` - the tty8 observation's screenshot is "
                "runs/20260816T021133Z-collect/screenshot.png, which shows the console stage "
                "painting the catalog on the physical panel"
            ),
            "notWitnessed": (
                "The retry presses were made through the API, not through the GUI button "
                "committed in fe00f40 - the button, its client method and its rendered bundle "
                "are covered by GuiFreshnessTests but no operator has clicked one on a screen. "
                "The 409 'offline' branch was never exercised against a real disconnected frame; "
                "every press landed on a live socket. tool.xvf-host.installed remains Escalated "
                "and was deliberately not retried. Reboot-verification is claimed for 35 "
                "resources and not for all 72 that are in-sync - see the comment on "
                "_HARDWARE_VERIFIED_RESOURCES for why that is an evidence limit."
            ),
        },
    },
    {
        "id": "M2.5",
        "title": "Image generation",
        "doneWhen": (
            "A card flashed from a Fleet-Manager-generated image boots, starts the agent "
            "unattended, and appears in the adoption queue (section 3.9)."
        ),
        "specRef": "version2.md section 5.1 and section 3.9",
        "stateFrom": "witness",
        "state": "in-progress",
        "evidence": {
            "how": "measured-against-the-real-base-image",
            "what": (
                "The generator is built (src/FrameLink.Control/Imaging) and its whole premise was "
                "measured against the real pinned 2026-06-18-raspios-trixie-arm64-lite.img in a "
                "plain debian:trixie-slim container with no --privileged, no --cap-add and no "
                "device mapping: debugfs writes the binary and sets mode and owner, debugfs "
                "symlink does what `systemctl enable` does, mcopy writes the boot-partition file, "
                "and `e2fsck -fn` calls the result clean. `mount -o loop` fails in that same "
                "container, which is what proves loopback was never involved."
            ),
            "whenUtc": "2026-08-15",
            "where": "workstation, debian:trixie-slim container",
            "recordedIn": (
                "git log commit ba5a873 carries the full measurement and the debugfs-exits-0-on-"
                "failure finding; decision 52 supersedes 32 in version2.md Appendix A; the tests "
                "are tests/FrameLink.Tests/ControlImageGenerationTests.cs"
            ),
            "notWitnessed": (
                "The milestone's own acceptance test - flash a card, watch a row appear - has not "
                "happened and cannot yet: version2.md section 5.3 item 3 records that no SD card "
                "reader is attached. Nothing generated has been written to a card, booted, or seen "
                "in the adoption queue. This milestone is NOT done."
            ),
        },
    },
    {
        "id": "M3",
        "title": "Resource migration",
        "doneWhen": (
            "Guide by guide, lowest-risk first, firmware DFU last. Each group passes the triple "
            "bar: state-diff vs the frozen v1 reference, checkpoint assertions, validation battery "
            "on the mule."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "resources",
    },
    {
        "id": "Mn+1",
        "title": "Bundled LiveKit",
        "doneWhen": (
            "Fleet Manager supervises LiveKit and mints tokens at adoption; guide 7 obsolete."
        ),
        "specRef": "version2.md section 5.1 and section 3.7",
        "stateFrom": "none",
    },
    {
        "id": "Mn+2",
        "title": "Production Fleet Manager",
        "doneWhen": (
            "Deployed as a PortainerCompose stack behind Traefik at framelink.huisman.io, with "
            "alerting."
        ),
        "specRef": "version2.md section 5.1 and section 3.8",
        "stateFrom": "none",
    },
    {
        "id": "Mn+3",
        "title": "Parity",
        "doneWhen": (
            "Stock image -> adopt -> fully green frame, mechanically equal to the frozen v1 "
            "reference. Deep, triple-checked verification. Only then do guides retire to the "
            "minimum set (section 8)."
        ),
        "specRef": "version2.md section 5.1 and section 8",
        "stateFrom": "none",
    },
]

#: The only resources that have ever converged on real hardware, each verified after a real
#: reboot rather than after a service restart. Everything the catalog has gained beyond these
#: is code with tests, which is a different and much weaker claim - keeping the two apart is
#: the single most load-bearing distinction in this file, because "implemented" reads like
#: "working" to a session that has forgotten which is which.
#:
#: Two witnessed sessions, kept in one list because the claim is identical:
#:
#:   2026-08-15, the M2 acceptance nine (first block below).
#:   2026-08-16, twenty-six more (second block), on mule T1RJ-6JCQ-9HN8-3920 during the first
#:     provision of the whole 79-resource catalog. The membership test was mechanical rather
#:     than eyeballed: a resource is listed only if the Fleet Manager's device-event history
#:     recorded a `boot` event for it - "Booted, and came back to verify X", which the loop
#:     emits only after a real reboot - AND it was `in-sync` in the live reconcile report at
#:     sequence 787. Either half alone is weaker: the boot event alone does not say the verify
#:     passed, and in-sync alone does not say a reboot was ever involved.
#:
#: THE LIST IS A FLOOR, NOT A CENSUS, and the gap is evidence and not doubt. 72 of 79 were
#: in-sync at sequence 787; only these 35 can be shown to have been reboot-verified from the
#: evidence held, because /api/devices/{id}/events returns the last 50 events and the rest of
#: this frame's history had already rolled off before it was read. The other 37 in-sync
#: resources are very probably in the same condition and are deliberately not claimed.
_HARDWARE_VERIFIED_RESOURCES: list[str] = [
    # 2026-08-15 - M2's acceptance set.
    "boot.config.dtoverlay-waveshare-panel",
    "boot.cmdline.fbcon-rotate",
    "journal.storage-persistent",
    "agent.adoption",
    "agent.device-name",
    "identity.hostname",
    "unit.cpu-performance.content",
    "unit.cpu-performance.enabled",
    "cpu.governor.performance",
    # 2026-08-16 - the first provision of the full catalog, evidence as described above.
    "agent.keypair",
    "app.config.identity",
    "app.config.immich-kiosk-url",
    "app.config.livekit-token",
    "app.config.room",
    "apt.unattended-upgrades.allowed-origins",
    "boot.autologin.getty-tty1",
    "boot.config.dtoverlay-vc4-kms-v3d-noaudio",
    "camera.pipewire-node.framelink-cam",
    "kiosk.binary.pinned-release",
    "kiosk.config.immich-api-key",
    "kiosk.config.immich-url",
    "kiosk.config.offline-asset-count",
    "kiosk.config.offline-mode-enabled",
    "kiosk.listen-address",
    "kiosk.offline-cache.dir",
    "labwc.autostart.executable",
    "labwc.rc-xml.touch-map",
    "portal.permission-store.camera",
    "session.bash-profile-exec-labwc",
    "unit.chromium-kiosk.content",
    "unit.chromium-kiosk.enabled",
    "unit.framelink-camera.content",
    "unit.framelink-camera.enabled",
    "unit.xdg-desktop-portal.dropin-desktop",
    "wireplumber.conf.camera-monitors-disabled",
]

#: The durable artifacts a session starting from nothing needs, and what each one is *for*.
#: The progress file is deliberately not the specification; it is the pointer to it.
_ORIENTATION: dict[str, str] = {
    "specification": (
        "version2.md - the build specification. Section 5.1 is the milestone ladder mirrored "
        "below, section 5.5 is why this file exists, Appendix A preserves every decision with "
        "its reasoning."
    ),
    "resourceSpec": (
        "reference/resource-catalog.md - the enumeration of all 79 device settings extracted "
        "from build guides 3-12, one block per resource with its Observe, Act and Verify. This "
        "is what M3 is migrating, and it is the spec each new resource is written against."
    ),
    "parityTarget": (
        "reference/v1-state-inventory.txt - the frozen v1 frame's state: packages, units, "
        "config contents and hashes, mixer values, firmware versions. This is what Mn+3 "
        "measures 'at parity' against, and the catalog's cross-check."
    ),
    "parityHarness": (
        "tools/harness/fl.py parity - the first of Mn+3's three bars. It runs read-only probes "
        "over SSH, then tools/FrameLink.Parity judges the result against the parity target, an "
        "expected-difference ledger (tools/FrameLink.Parity/expected-differences.json) and the "
        "catalog. `fl.py parity --coverage` needs no frame and prints what a state diff can and "
        "cannot answer. Exit 0 at parity, 2 differences found, 3 the comparison could not be "
        "completed."
    ),
    "reasoningRecord": (
        "git log - every commit message carries why, not what. `git log --oneline` is the "
        "cheapest orientation available and the only place some findings exist at all (the "
        "debugfs-exits-0-on-failure trap is ba5a873; the StartLimitIntervalSec section trap is "
        "bebf34c)."
    ),
    "repoRules": (
        "CLAUDE.md - binding operational rules. Section 1.2 on credentials is absolute, "
        "section 1.8 requires explicit per-class authorisation before any mutation of the mule."
    ),
    "harness": (
        "tools/harness/fl.py - this harness. `--help` on any subcommand explains what it does "
        "and what it refuses to do; flh/config.py holds every host, path and default in one "
        "place."
    ),
    "openItems": (
        "TODO.md is v1's outstanding work and is gitignored; version2.md Appendix B holds v2's "
        "open items."
    ),
}


def utcnow() -> str:
    """Timestamp in the one format the whole file uses: RFC 3339, UTC, second precision."""
    return datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _read_number(path: str, pattern: str) -> tuple[int | None, str]:
    """Pull one integer out of a repository file. Returns the value and how it was obtained.

    Never guesses. A file that cannot be read, or that no longer contains the pattern, yields
    ``None`` and a sentence saying so - which is a useful fact in itself, because it means the
    thing that used to be authoritative for that number has moved.
    """
    target = REPO_ROOT / path
    try:
        text = target.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return None, f"{path} could not be read ({exc.__class__.__name__})"
    match = re.search(pattern, text)
    if not match:
        return None, f"{path} no longer contains the pattern this count is read from"
    return int(match.group(1)), path


def _resource_ledger() -> dict[str, Any]:
    """The M3 ledger, measured from the repository rather than recorded.

    Two numbers, each read from the file that is authoritative for it:

    * the catalog total, from the Counts table in ``reference/resource-catalog.md``;
    * how many are implemented, from the assertion in ``AgentResourceGraphTests.cs`` that
      pins ``graph.Count`` - which is not a proxy for the count, it is the count, enforced by
      a test that goes red the moment the catalog and the graph disagree.

    Measuring rather than recording is the point. A frozen number would be wrong within a day
    of a workstream landing resources, and wrong in the direction that matters least visibly:
    it would keep claiming less progress than exists, and nobody re-checks a number that only
    ever understates.
    """
    total, total_source = _read_number(
        "reference/resource-catalog.md",
        r"\|\s*\*\*Total\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|",
    )
    implemented, implemented_source = _read_number(
        "tests/FrameLink.Tests/AgentResourceGraphTests.cs",
        r"Assert\.Equal\(\s*(\d+)\s*,\s*graph\.Count\s*\)",
    )
    verified = list(_HARDWARE_VERIFIED_RESOURCES)
    ledger: dict[str, Any] = {
        "catalogTotal": total,
        "catalogTotalReadFrom": total_source,
        "implemented": implemented,
        "implementedReadFrom": implemented_source,
        "remaining": (total - implemented) if (total is not None and implemented is not None) else None,
        "hardwareVerifiedCount": len(verified),
        "hardwareVerified": verified,
        "meaning": {
            "implemented": (
                "The resource exists in the agent's catalog with tests. It has never necessarily "
                "run on a frame."
            ),
            "hardwareVerified": (
                "The resource converged on the mule and was verified after a real reboot. Only "
                "the M2 nine have ever done this."
            ),
        },
        "gap": (
            "The first full provision of the whole catalog on hardware has not happened. That is "
            "the largest single unknown in the build: every resource beyond the nine is code that "
            "has never met a real device."
        ),
    }
    return ledger


def _environment() -> dict[str, Any]:
    """Hosts, credentials by name, and the measurements other timeouts are set against.

    Credential **values** never appear here, in any form, ever (CLAUDE.md section 1.2). What
    appears is the variable name, what it unlocks and where to get one, which is what a
    session that has just started actually lacks.
    """
    return {
        "mule": {
            "address": f"{MULE_USER}@{MULE_HOST}",
            "what": (
                "The development mule: the existing frame, repurposed. Raspberry Pi 5, Raspberry "
                "Pi OS Lite (Trixie), 800x1280 DSI panel on card0-DSI-2, on a controllable smart "
                "plug."
            ),
            "agentUnit": "fl-agent.service, running as root, binary at /usr/local/bin/fl-agent",
            "override": "FL_HOST / FL_USER",
        },
        "fleetManager": {
            "address": CONTROL_URL,
            "what": (
                "The dev Fleet Manager, run on the workstation from src/FrameLink.Control. The "
                "mule's agent is pointed here and retries forever while it is down, which is why "
                "a journal capture taken with the server stopped is a wall of connection "
                "warnings rather than a fault."
            ),
            "startedBy": (
                "dotnet run --project src/FrameLink.Control -- --urls http://0.0.0.0:5199, with "
                "FRAMELINK_OPERATOR_PASSWORD set and FRAMELINK_DATA_DIR pointing at the operator "
                "data directory outside this repository."
            ),
            "agentPointedBy": "--control-url on `fl-agent install`, or the FL_CONTROL_URL variable",
        },
        "homeAssistant": {
            "address": HA_URL,
            "entity": HA_ENTITY,
            "what": (
                "Switches the mule's power for `fl.py power`. Port 8123 was verified against the "
                "live instance; 8086 on the same host is a different service that answers 404 for "
                "every path, which once read exactly like 'that entity does not exist'."
            ),
            "override": "FL_HA_URL / FL_HA_ENTITY",
        },
        "credentials": [
            {
                "variable": "FL_PW",
                "unlocks": "fl.py deploy, fl.py collect - and it is also the answer to sudo on the mule",
                "howToObtain": "Ask the user. Supplied inline per session; there is deliberately no key-based fallback.",
            },
            {
                "variable": "FL_HA_TOKEN",
                "unlocks": "fl.py power",
                "howToObtain": f"A long-lived access token from the Home Assistant profile page at {HA_URL}",
            },
            {
                "variable": "FRAMELINK_OPERATOR_PASSWORD",
                "unlocks": "the Fleet Manager GUI - the single operator credential of version2.md section 3.2",
                "howToObtain": (
                    "Ask the user. An instance started without it still runs and serves a setup "
                    "page naming the variable, and answers connecting frames 'not-configured'."
                ),
            },
        ],
        "credentialRule": (
            "CLAUDE.md section 1.2, absolute: supplied in-session by environment variable only. "
            "Never a file, a log, a shell history, a config, a keychain or a default. Never "
            "echoed, never summarised back, never written into this file."
        ),
        "measurements": {
            "rebootToSshReadySeconds": 22.3,
            "rebootMeasurement": (
                "Measured twice on the mule 2026-08-15 with loss of tcp/22 confirmed in between. "
                "Every wait ceiling in the harness is a margin over this number, not an estimate "
                "of it - power.on(wait_s=120) is roughly five boots."
            ),
            "eepromPowerOffOnHalt": (
                "POWER_OFF_ON_HALT=1 is set in this Pi's EEPROM, so `halt` genuinely cuts power. A "
                "silent frame on a live relay has three explanations, not two: booting, hung, or "
                "halted and drawing nothing."
            ),
        },
    }


def _blank() -> dict[str, Any]:
    return {
        "schema": SCHEMA,
        "generatedBy": "tools/harness/fl.py",
        "updatedUtc": utcnow(),
        "readMeFirst": list(_READ_ME_FIRST),
        "currentMilestone": None,
        "inFlight": None,
        "nextActions": [],
        "blockers": [],
        "milestones": [],
        "capabilities": {
            cap_id: {
                "title": title,
                "state": "unproven",
                "provenBy": None,
                "provenUtc": None,
                "provenCommand": command,
                "detail": None,
            }
            for cap_id, title, command in _CAPABILITIES
        },
        "resources": {},
        "orientation": dict(_ORIENTATION),
        "environment": _environment(),
        "artifacts": {
            "agentBinary": None,
            "deployed": None,
            "lastCollection": None,
        },
        "counters": {
            "builds": 0,
            "deploys": 0,
            "testRuns": 0,
            "collections": 0,
            "relayOperations": 0,
        },
        "log": [],
    }


#: Canonical key order, rewritten on every save. Ordered by what a session with no memory
#: needs first, not by what the harness happens to update most: what to do, then what is in
#: the way, then where the build stands, and the event log - the longest section by far - last.
_KEY_ORDER = [
    "schema",
    "generatedBy",
    "updatedUtc",
    "readMeFirst",
    "currentMilestone",
    "inFlight",
    "nextActions",
    "blockers",
    "milestones",
    "capabilities",
    "resources",
    "orientation",
    "environment",
    "artifacts",
    "counters",
    "log",
]

#: Keys carried across a schema change. Everything else in a foreign file is derived and will
#: be rebuilt on the first save, so carrying it would only preserve a stale copy of it.
_MIGRATED_KEYS = ["inFlight", "capabilities", "artifacts", "counters", "blockers", "log"]


def load() -> dict[str, Any]:
    """Read the progress file, creating a blank one if it does not exist yet.

    A file written under a different schema is **migrated**, not stepped around: the earned
    state - capabilities, artifacts, counters, blockers, the log - is carried across and
    everything derived is rebuilt on the next save. The previous behaviour moved the whole
    file under ``previousSchema`` and started blank, which meant the one moment the format
    changed was also the moment the record of what had been proven stopped being read.
    """
    if not PROGRESS_FILE.exists():
        return _blank()
    try:
        data = json.loads(PROGRESS_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fresh = _blank()
        fresh["blockers"] = [
            {
                "id": "progress-file-unreadable",
                "what": f"The previous progress file could not be parsed: {exc}",
                "needs": "Nothing - a blank file has taken over. Prior history is lost.",
                "since": utcnow(),
            }
        ]
        return fresh

    if data.get("schema") != SCHEMA:
        fresh = _blank()
        for key in _MIGRATED_KEYS:
            if key in data:
                fresh[key] = data[key]
        fresh["migratedFrom"] = {"schema": data.get("schema"), "utc": utcnow()}
        data = fresh

    # Tolerate a file written by an older run that predates a capability being added.
    for cap_id, title, command in _CAPABILITIES:
        data.setdefault("capabilities", {}).setdefault(
            cap_id,
            {
                "title": title,
                "state": "unproven",
                "provenBy": None,
                "provenUtc": None,
                "provenCommand": command,
                "detail": None,
            },
        )
    return data


def save(data: dict[str, Any]) -> None:
    """Atomically write the progress file.

    Temp file in the same directory then ``os.replace``, which is atomic on both NTFS and
    ext4. A half-written progress file would be worse than none at all, because a resuming
    session would trust it.
    """
    data["updatedUtc"] = utcnow()
    data["log"] = data.get("log", [])[-LOG_LIMIT:]
    # Derived on every write, never authored. Anything that could be set by hand would
    # eventually disagree with the state beneath it, and the disagreement would be invisible.
    _refresh(data)

    PROGRESS_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = PROGRESS_FILE.with_suffix(".json.tmp")
    # newline="\n" because .gitattributes pins the whole repository to LF in the working
    # tree on every OS, and this file is tracked. Without it Python would translate to
    # CRLF on Windows and every harness run would show as a whole-file diff.
    tmp.write_text(
        json.dumps(_ordered(data), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    os.replace(tmp, PROGRESS_FILE)


def _ordered(data: dict[str, Any]) -> dict[str, Any]:
    """Rebuild the mapping in canonical key order, keeping anything unrecognised at the end.

    Python preserves insertion order, so without this a key added by a later revision lands
    wherever it was first written - which for an existing file is the very bottom, under a
    two-hundred-entry log. Rewriting the order on every save is what keeps the diff of this
    file about its content.
    """
    ordered = {key: data[key] for key in _KEY_ORDER if key in data}
    ordered.update({key: value for key, value in data.items() if key not in ordered})
    return ordered


def prove(cap_id: str, *, by: str, detail: str) -> None:
    """Mark a capability proven. Called only from the code path that actually proved it."""
    data = load()
    cap = data["capabilities"].setdefault(cap_id, {"title": cap_id})
    cap["state"] = "proven"
    cap["provenBy"] = by
    cap["provenUtc"] = utcnow()
    cap["detail"] = detail
    # The command just ran, so whatever previously made it unrunnable no longer does. Without
    # this the note outlives its cause: `fl.py status` goes on printing "not runnable in this
    # session" under a capability that was re-proven minutes ago, and a resuming session has no
    # way to tell that obstacle from a current one.
    cap.pop("currentlyUnrunnable", None)
    save(data)


def mark(cap_id: str, state: str, *, detail: str) -> None:
    """Record a non-proven capability state: ``blocked``, ``failed`` or back to ``unproven``.

    A capability that was proven earlier is not demoted by a later failure of an unrelated
    kind; it is demoted only by ``failed``, which means the proving command ran and lost.
    """
    data = load()
    cap = data["capabilities"].setdefault(cap_id, {"title": cap_id})
    if state == "blocked" and cap.get("state") == "proven":
        # Already proven once. Being unable to re-run it right now (no FL_PW in this
        # session, say) is not evidence against the earlier proof, so the proof stands and
        # the obstruction is noted beside it. Stored as its own field rather than appended
        # to the detail string: appending grew without bound across repeated runs, and the
        # tenth copy of the same note is not ten times the information.
        cap["currentlyUnrunnable"] = detail
    else:
        cap["state"] = state
        cap["detail"] = detail
        cap.pop("currentlyUnrunnable", None)
        if state == "failed":
            cap["provenBy"] = None
            cap["provenUtc"] = None
    save(data)


def add_blocker(blocker_id: str, what: str, needs: str) -> None:
    """Record something the harness cannot do, and what it would take. Idempotent by id."""
    data = load()
    blockers = [b for b in data.get("blockers", []) if b.get("id") != blocker_id]
    blockers.append({"id": blocker_id, "what": what, "needs": needs, "since": utcnow()})
    data["blockers"] = blockers
    save(data)


def clear_blocker(blocker_id: str) -> None:
    """Remove a blocker that no longer applies."""
    data = load()
    before = data.get("blockers", [])
    after = [b for b in before if b.get("id") != blocker_id]
    if len(after) != len(before):
        data["blockers"] = after
        save(data)


def set_artifact(name: str, value: dict[str, Any] | None) -> None:
    """Record a produced artifact - the built binary, what is deployed, last collection."""
    data = load()
    data.setdefault("artifacts", {})[name] = value
    save(data)


def bump(counter: str, amount: int = 1) -> int:
    """Increment a counter and return the new value.

    ``relayOperations`` is the one that matters most: it makes hardware wear a visible,
    accumulating number rather than something nobody notices until 350 operations later.
    """
    data = load()
    counters = data.setdefault("counters", {})
    counters[counter] = counters.get(counter, 0) + amount
    total = counters[counter]
    save(data)
    return total


def set_next_actions(actions: list[str]) -> None:
    """Replace the list of what a resuming session should do next."""
    data = load()
    data["nextActions"] = actions
    save(data)


def log(command: str, ok: bool, summary: str, **extra: Any) -> None:
    """Append one bounded event to the log."""
    data = load()
    entry: dict[str, Any] = {"utc": utcnow(), "command": command, "ok": ok, "summary": summary}
    entry.update(extra)
    data.setdefault("log", []).append(entry)
    save(data)


@contextmanager
def activity(command: str, **context: Any):
    """Bracket a subcommand with an ``inFlight`` marker.

    Written before the work starts and cleared after it ends, so a session killed
    mid-command leaves the marker behind. That marker is the only way a resuming session
    can tell "this never ran" apart from "this ran and its result is unknown" - which
    matters most for `deploy` and `power`, the two subcommands that change something
    outside this repository.
    """
    data = load()
    data["inFlight"] = {"command": command, "startedUtc": utcnow(), **context}
    save(data)
    try:
        yield
    finally:
        data = load()
        data["inFlight"] = None
        # A marker that was written, survived a whole subcommand and was then cleared IS
        # the demonstration that the resume mechanism works. Proving it here rather than
        # asserting it in the seed data keeps the "states are earned" rule intact for the
        # one capability that would otherwise have nothing to earn it.
        capability = data["capabilities"].setdefault("progress-file", {"title": "progress-file"})
        if capability.get("state") != "proven":
            capability["state"] = "proven"
            capability["provenBy"] = f"fl.py {command}"
            capability["provenUtc"] = utcnow()
            capability["detail"] = (
                f"inFlight marker written, survived `{command}`, and was cleared; "
                f"file written atomically to {PROGRESS_FILE.name}"
            )
        save(data)


def _milestone_state_from_capabilities(data: dict[str, Any]) -> str:
    """M0's state, derived from the seven capabilities and from nothing else."""
    states = [cap.get("state") for cap in data.get("capabilities", {}).values()]
    if states and all(state == "proven" for state in states):
        return "done"
    if any(state == "failed" for state in states):
        return "failing"
    if any(state == "proven" for state in states):
        return "in-progress"
    return "not-started"


def _milestone_state_from_resources(ledger: dict[str, Any]) -> tuple[str, dict[str, Any]]:
    """M3's state, derived from the measured ledger. Returns the state and its evidence."""
    total = ledger.get("catalogTotal")
    implemented = ledger.get("implemented")
    verified = ledger.get("hardwareVerifiedCount", 0)

    if implemented is None or total is None:
        return "unknown", {
            "how": "derived-from-the-repository",
            "what": (
                "The counts this state is derived from could not be read - see "
                "resources.catalogTotalReadFrom and resources.implementedReadFrom."
            ),
        }

    if implemented >= total and verified >= total:
        state = "done"
    else:
        state = "in-progress" if implemented else "not-started"

    return state, {
        "how": "derived-from-the-repository",
        "what": (
            f"{implemented} of {total} catalog resources are implemented in the agent; "
            f"{verified} of {total} have ever converged on real hardware. Both numbers are "
            "measured on every write, not recorded."
        ),
        "notWitnessed": (
            "The triple bar this milestone is graded on - state-diff against the frozen v1 "
            "reference, checkpoint assertions, validation battery on the mule - has not been run "
            "for any group. The state-diff harness now exists (`fl.py parity`, judged by "
            "tools/FrameLink.Parity) and is tested against fixtures, but it has never been run "
            "against a frame: no observation has ever been collected and no parity verdict has "
            "ever been issued. The other two bars have no harness."
        ),
    }


def _refresh(data: dict[str, Any]) -> None:
    """Rebuild every derived section. Never authored, never hand-edited.

    Constants that belong to the code rather than to accumulated state - the milestone ladder,
    the orientation pointers, the environment contract, the read-me - are rewritten from the
    templates above on every save. Otherwise a file created weeks ago keeps quoting a spec line
    that has since been reworded, and the record slowly stops describing the thing it records.
    """
    data["schema"] = SCHEMA
    data["generatedBy"] = "tools/harness/fl.py"
    data["readMeFirst"] = list(_READ_ME_FIRST)
    data["orientation"] = dict(_ORIENTATION)
    data["environment"] = _environment()

    ledger = _resource_ledger()
    data["resources"] = ledger

    milestones: list[dict[str, Any]] = []
    for template in _MILESTONES:
        milestone = {
            "id": template["id"],
            "title": template["title"],
            "doneWhen": template["doneWhen"],
            "specRef": template["specRef"],
            "state": "not-started",
            "stateFrom": template["stateFrom"],
        }
        if template["stateFrom"] == "capabilities":
            milestone["state"] = _milestone_state_from_capabilities(data)
            milestone["evidence"] = {
                "how": "earned-by-harness-commands",
                "what": (
                    "Every one of the seven capabilities below was proven by the fl.py subcommand "
                    "that performs it. Nothing here was recorded by hand."
                ),
            }
        elif template["stateFrom"] == "resources":
            milestone["state"], milestone["evidence"] = _milestone_state_from_resources(ledger)
        elif template["stateFrom"] == "witness":
            milestone["state"] = template["state"]
            milestone["evidence"] = dict(template["evidence"])
        milestones.append(milestone)

    data["milestones"] = milestones
    data["currentMilestone"] = next(
        (m["id"] for m in milestones if m["state"] != "done"),
        milestones[-1]["id"] if milestones else None,
    )

    # The singular `milestone` key was this file's whole vocabulary when M0 was the whole
    # world. It is gone, and it is popped rather than merely not written because an older
    # copy of this module still running in another process re-creates it at its own next
    # save. Leaving it would put a stale M0 header above a ladder that disagrees with it.
    data.pop("milestone", None)
