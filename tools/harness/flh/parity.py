"""The parity harness's collector - the half that reaches a frame.

version2.md milestone Mn+3 grades a frame on being *"mechanically equal to the frozen v1
reference"*, and section 5.1's triple bar names a **state-diff versus the frozen v1
reference** as the first of the three. This module is that state diff's eyes; the judge is
``tools/FrameLink.Parity``.

The split, and why it is where it is
------------------------------------
Reaching the frame is paramiko's job (CLAUDE.md section 1.3) and therefore Python's. Deciding what
an observation *means* is a comparison against 929 package versions, twenty-nine captured
blocks and an expected-difference ledger - and the answer to "is this package version newer"
already exists, in ``src/FrameLink.Control/PackageDrift.cs``, where the Fleet Manager computes
drift from exactly these reports (decision 55). Writing a second version comparison here would
produce a second answer to that question, and the two would eventually disagree about some
frame with nothing to catch it.

So the judge is C#, it reuses that code, and it lives inside the ordinary test suite where
its whole verdict path is exercised against fixtures with no hardware anywhere near it. This
module runs the probes the judge hands it and writes down exactly what came back. It is the
same split ``tools/FrameLink.Upstream`` uses: the network half outside the suite, the
deciding half inside it.

**The probe list is not held here.** ``FrameLink.Parity probes`` emits it, this module runs
it. Two lists - one in the collector and one in the judge - is the shape where a facet gets
renamed on one side and silently stops being collected on the other, and a facet nobody
collects looks exactly like a facet with nothing wrong.

Read-only, and unprivileged by default
--------------------------------------
Every probe is an inspection command: ``cat``, ``grep``, ``dpkg-query``, ``systemctl
list-unit-files``, ``amixer``, ``findmnt``, ``ip``. Nothing here writes to the frame, so
CLAUDE.md section 1.8 needs no authorisation for any of it - and a parity check that changed the
thing it was measuring would be worthless anyway.

Two probes genuinely cannot be unprivileged: the array's firmware version is a privileged USB
control transfer (which is why the catalog's own Observe for it is written with ``sudo``), and
the agent's state directory is root-owned. Those are marked ``elevated`` by the judge and are
**skipped unless ``--elevate`` is given**, with the skip written into the artifact as the
reason it is. A default run therefore reports ``incomplete`` and names them, rather than
quietly calling a frame at parity on evidence it never looked at.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import progress, ssh, ui
from .config import REPO_ROOT, RUNS_DIR, HarnessError

#: The judge. A .NET project rather than a script for the same reason FrameLink.Upstream is
#: one: Directory.Build.props applies there too, so the code that decides whether a frame is
#: at parity is held to the same analyser bar as the two programs it judges.
JUDGE_PROJECT = "tools/FrameLink.Parity"

#: Seconds one probe may take. The slowest is `dpkg-query` over ~930 packages, which is well
#: under a second on a Pi 5; a probe still running after this is wedged, not slow.
PROBE_TIMEOUT_S = 120.0

#: Seconds the judge may take. It reads three files and compares maps - the only thing that
#: can make it slow is `dotnet run` deciding to rebuild.
JUDGE_TIMEOUT_S = 600.0

#: What `judge` exits with, and what each one means. Mirrors tools/FrameLink.Parity/Program.cs.
OUTCOMES = {
    0: "at parity",
    1: "the judge could not run",
    2: "differences found",
    3: "the comparison could not be completed",
}


def run_dir(label: str = "parity") -> Path:
    """A fresh timestamped directory under the gitignored runs/ tree."""
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    path = RUNS_DIR / f"{stamp}-{label}"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _dotnet(args: list[str], *, timeout: float, capture: bool) -> subprocess.CompletedProcess:
    """Run the judge project, with the SDK's telemetry off like every other harness command."""
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise HarnessError(
            "dotnet is not on PATH, so the parity judge cannot run.",
            exit_code=4,
            remedy=(
                "The judge is a .NET project (tools/FrameLink.Parity). Collection can still be "
                "done on its own with `fl.py parity --collect-only`, and judged later from the "
                "written observed.json with `fl.py parity --from <path>`."
            ),
        )

    import os

    env = dict(os.environ)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"

    return subprocess.run(  # noqa: S603 - argv built here
        [dotnet, "run", "--project", str(REPO_ROOT / JUDGE_PROJECT), "-c", "Release", "--", *args],
        cwd=str(REPO_ROOT),
        env=env,
        capture_output=capture,
        text=True,
        timeout=timeout,
        check=False,
    )


def probes() -> list[dict[str, Any]]:
    """Ask the judge which read-only commands answer which facet."""
    result = _dotnet(["probes"], timeout=JUDGE_TIMEOUT_S, capture=True)
    if result.returncode != 0:
        raise HarnessError(
            f"The parity judge would not list its probes (exit {result.returncode}).",
            exit_code=5,
            remedy=(result.stderr or result.stdout or "").strip()[:600] or None,
        )

    try:
        # `dotnet run` can prepend build output, so take the JSON document and nothing else.
        text = result.stdout
        document = json.loads(text[text.index("{"):])
    except (ValueError, json.JSONDecodeError) as exc:
        raise HarnessError(f"The probe list was not JSON: {exc}", exit_code=5) from exc

    listed = document.get("probes", [])
    if not listed:
        raise HarnessError("The probe list came back empty.", exit_code=5)
    return listed


def collect(*, elevate: bool = False, host: str | None = None) -> dict[str, Any]:
    """Run every probe on the frame and return the observation the judge reads.

    One SSH session for all of them, and each probe's exit status, stdout and stderr are
    recorded whether it succeeded or not. A probe that failed is evidence - it is what makes
    the run ``incomplete`` rather than letting a facet with nothing in it read as a facet with
    nothing wrong.
    """
    listed = probes()
    wanted = [probe for probe in listed if elevate or not probe.get("elevated")]
    skipped = [probe for probe in listed if not elevate and probe.get("elevated")]

    ui.step(f"Collecting {len(wanted)} read-only probes from the frame")
    if skipped:
        ui.warn(
            f"{len(skipped)} probe(s) need root and are being skipped: "
            + ", ".join(probe["facet"] for probe in skipped)
            + ". Pass --elevate to collect them; without them the verdict cannot be 'parity'."
        )

    observations: list[dict[str, Any]] = []

    with ssh.connect(**({"host": host} if host else {})) as mule:
        for probe in wanted:
            elevated = bool(probe.get("elevated"))
            result = (
                mule.run_privileged(probe["command"], timeout=PROBE_TIMEOUT_S)
                if elevated
                else mule.run(probe["command"], timeout=PROBE_TIMEOUT_S)
            )
            mark = "ok  " if result.exit_status == 0 else "FAIL"
            ui.info(f"{mark}  {probe['facet']}  ({len(result.stdout)} bytes)")
            observations.append(
                {
                    "facet": probe["facet"],
                    "command": result.command,
                    "exitStatus": result.exit_status,
                    "stdout": result.stdout,
                    "stderr": result.stderr,
                }
            )

        address = mule.host

    for probe in skipped:
        observations.append(
            {
                "facet": probe["facet"],
                "command": probe["command"],
                "exitStatus": 0,
                "stdout": "",
                "stderr": "",
                "skipped": (
                    "Needs root, and this run was unprivileged. Re-run with --elevate to compare "
                    "this facet."
                ),
            }
        )

    return {
        "schema": "framelink-parity-observation-1",
        "collector": "tools/harness/fl.py parity",
        "host": address,
        "collectedUtc": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "elevated": elevate,
        "observations": observations,
    }


def coverage() -> int:
    """Print what a state diff can and cannot answer. Needs no frame and no credential."""
    ui.step("Parity coverage - what the state diff reaches, and what it provably cannot")
    return _dotnet(["coverage"], timeout=JUDGE_TIMEOUT_S, capture=False).returncode


def judge(observed: Path, out: Path) -> int:
    """Hand an observation to the judge and let its verdict be this command's verdict."""
    ui.step("Judging against reference/v1-state-inventory.txt and the expected-difference ledger")
    result = _dotnet(
        ["judge", "--observed", str(observed), "--out", str(out)],
        timeout=JUDGE_TIMEOUT_S,
        capture=False,
    )
    return result.returncode


def run(*, elevate: bool = False, collect_only: bool = False,
        observed: Path | None = None, host: str | None = None) -> dict[str, Any]:
    """Collect, judge, and record the verdict. Returns a summary; the caller propagates ``exitCode``.

    ``observed`` re-judges a collection that already exists, which is how a ledger entry gets
    added and tested without touching the frame again - and how the artifact from a wipe-day
    session stays useful after the card has been reflashed.
    """
    directory = run_dir()
    path = observed

    if path is None:
        collection = collect(elevate=elevate, host=host)
        path = directory / "observed.json"
        path.write_text(json.dumps(collection, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")
        ui.ok(f"observation written to {path}")
        progress.set_artifact(
            "lastParityObservation",
            {"path": str(path), "host": collection["host"], "utc": collection["collectedUtc"],
             "elevated": elevate},
        )

    if collect_only:
        ui.info("--collect-only: not judging. Re-run with --from to judge this observation.")
        return {"exitCode": 0, "directory": str(directory), "observed": str(path), "judged": False}

    code = judge(path, directory)
    verdict = OUTCOMES.get(code, f"unrecognised exit {code}")
    ui.info(f"verdict: {verdict}")

    # Recorded as an artifact and not as a capability, deliberately. `capabilities` is M0's
    # ledger and M0's state is derived from *all* of it being proven, so a parity entry there
    # would demote a finished milestone every time a frame turned out not to be at parity -
    # which is a fact about the frame, not about the harness.
    # `subject` is read back out of the observation rather than assumed, because --from judges a
    # file somebody else wrote and a verdict recorded without naming what it was about is the
    # kind of record a later session reads as a claim about a frame.
    try:
        subject = json.loads(path.read_text(encoding="utf-8")).get("host")
    except (OSError, json.JSONDecodeError):
        subject = None

    progress.set_artifact(
        "lastParityRun",
        {
            "subject": subject,
            "observed": str(path),
            "directory": str(directory),
            "exitCode": code,
            "verdict": verdict,
            "elevated": elevate,
            "utc": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        },
    )

    return {
        "exitCode": code,
        "directory": str(directory),
        "observed": str(path),
        "judged": True,
        "verdict": verdict,
    }
