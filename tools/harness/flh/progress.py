"""The resumable progress file - version2.md section 5.5.

    "Long runs lose context, so progress is written to disk continuously and any session
     can resume mid-milestone."

This module owns ``tools/harness/progress.json``. That file is deliberately **not**
gitignored (``.gitignore`` covers ``tools/harness/runs/`` only): it is the record a fresh
session with no memory of anything reads first to learn exactly where the build stands.

Design rules the shape follows
------------------------------
1. **Machine-readable first, human-readable anyway.** Plain JSON, two-space indent, one
   fact per key, stable key order so a git diff shows what actually changed.
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
"""

from __future__ import annotations

import json
import os
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any

from .config import PROGRESS_FILE

SCHEMA = "framelink.harness.progress/1"

#: Bound on the event log. Enough to see a whole working session, small enough that the
#: file stays readable and diffable.
LOG_LIMIT = 200

_READ_ME_FIRST = (
    "You are looking at the FrameLink autonomy harness progress file. It is written "
    "continuously by tools/harness/fl.py and is the authoritative answer to 'where does "
    "the build stand'. Read 'milestone', then 'capabilities' (state is earned by a "
    "command actually succeeding, never hand-edited), then 'blockers' and 'nextActions'. "
    "If 'inFlight' is not null, a previous session died in the middle of that command and "
    "the state it was changing is unverified. Run 'python tools/harness/fl.py status' for "
    "a live re-probe of the environment."
)

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


def utcnow() -> str:
    """Timestamp in the one format the whole file uses: RFC 3339, UTC, second precision."""
    return datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _blank() -> dict[str, Any]:
    return {
        "schema": SCHEMA,
        "generatedBy": "tools/harness/fl.py",
        "updatedUtc": utcnow(),
        "readMeFirst": _READ_ME_FIRST,
        "milestone": {
            "id": "M0",
            "title": "Autonomy harness",
            "doneWhen": (
                "A code change reaches the mule and is verified with no human help: build "
                "path, deploy script, power-cycle control, screenshot + journal collection, "
                "resumable progress file, test runner."
            ),
            "specRef": "version2.md section 5.1",
            "state": "in-progress",
        },
        "inFlight": None,
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
        "blockers": [],
        "nextActions": [],
        "log": [],
    }


def load() -> dict[str, Any]:
    """Read the progress file, creating a blank one if it does not exist yet.

    A file whose schema does not match is not silently migrated - it is kept under
    ``previousSchema`` so nothing is lost, and a blank current-schema file takes over.
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
        fresh["previousSchema"] = data
        return fresh

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
    # Derived on every write, never authored. A milestone state that could be set by hand
    # would eventually disagree with the capabilities beneath it, and the disagreement
    # would be invisible.
    _refresh_milestone(data)

    PROGRESS_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = PROGRESS_FILE.with_suffix(".json.tmp")
    # newline="\n" because .gitattributes pins the whole repository to LF in the working
    # tree on every OS, and this file is tracked. Without it Python would translate to
    # CRLF on Windows and every harness run would show as a whole-file diff.
    tmp.write_text(
        json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n"
    )
    os.replace(tmp, PROGRESS_FILE)


def prove(cap_id: str, *, by: str, detail: str) -> None:
    """Mark a capability proven. Called only from the code path that actually proved it."""
    data = load()
    cap = data["capabilities"].setdefault(cap_id, {"title": cap_id})
    cap["state"] = "proven"
    cap["provenBy"] = by
    cap["provenUtc"] = utcnow()
    cap["detail"] = detail
    _refresh_milestone(data)
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
    _refresh_milestone(data)
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


def _refresh_milestone(data: dict[str, Any]) -> None:
    """Derive milestone state from capability states. Never set by hand.

    The milestone's other fields - id, title, doneWhen, specRef - are constants belonging
    to the code, not accumulated state, so they are rewritten from the template on every
    save. Otherwise a file created weeks ago keeps quoting a spec line that has since been
    reworded, and the record slowly stops describing the thing it records.
    """
    template = _blank()["milestone"]
    milestone = data.setdefault("milestone", {})
    for key, value in template.items():
        if key != "state":
            milestone[key] = value
    data["readMeFirst"] = _READ_ME_FIRST

    caps = data.get("capabilities", {})
    states = {cap_id: cap.get("state") for cap_id, cap in caps.items()}
    if all(state == "proven" for state in states.values()):
        data["milestone"]["state"] = "done"
    elif any(state == "failed" for state in states.values()):
        data["milestone"]["state"] = "failing"
    elif any(state == "proven" for state in states.values()):
        data["milestone"]["state"] = "in-progress"
    else:
        data["milestone"]["state"] = "not-started"
