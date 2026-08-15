"""Run the test suite and propagate its verdict.

Two things here are deliberate and both are documented at their source.

**`dotnet run`, not `dotnet test`.** ``tests/FrameLink.Tests/FrameLink.Tests.csproj`` spells
out why: an xunit v3 test project is an executable whose entry point *is* the runner, and
as of xunit.v3 4.0.0 on the .NET 10 SDK ``dotnet test`` negotiates over the
Microsoft.Testing.Platform v2 bridge and discovers nothing - it reports "Zero tests ran"
against an assembly that runs all of them directly. A green run that executed no tests is
the worst possible failure for the one gate between a change and a release (section 7.2), so the
harness takes the path that cannot produce it.

**`TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.** The Microsoft Testing Platform prints a telemetry
notice and phones home on every run. A project whose first governing principle is "no
rendezvous server, no account, no phone-home" (section 1.2.1) should not be shipping usage data
from its own test suite. ``DOTNET_CLI_TELEMETRY_OPTOUT`` covers the SDK for the same reason.

Output is streamed as it arrives rather than captured and printed at the end, because an
unattended run that goes quiet for minutes is indistinguishable from one that has hung.
"""

from __future__ import annotations

import os
import queue
import shutil
import subprocess
import threading
import time
from typing import Any

from . import progress, ui
from .config import REPO_ROOT, TEST_PROJECT, HarnessError

#: Wall-clock ceiling on one suite run. The harness is meant to run unattended, and a test
#: that waits forever on a socket or a console read turns the whole feedback loop into a
#: silent hang - the failure mode that is worst of all, because nothing reports it. Ten
#: minutes is far beyond any healthy run of this suite (measured: 344 ms for 12 tests) and
#: still finite. Override with --timeout when a genuinely long suite arrives.
DEFAULT_TIMEOUT_S = 600.0


def test_env() -> dict[str, str]:
    """Child environment for the test run."""
    env = dict(os.environ)
    env["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def run(*, project: str = TEST_PROJECT, configuration: str = "Debug",
        extra_args: list[str] | None = None, timeout: float = DEFAULT_TIMEOUT_S) -> dict[str, Any]:
    """Run the suite. Returns a summary; the caller propagates ``exitCode``."""
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise HarnessError("dotnet is not on PATH.", exit_code=4)

    project_dir = REPO_ROOT / project
    if not project_dir.is_dir():
        progress.mark("test-runner", "blocked", detail=f"{project} does not exist")
        raise HarnessError(f"Test project {project} does not exist.", exit_code=3)

    argv = [dotnet, "run", "--project", str(project_dir), "--configuration", configuration]
    if extra_args:
        # Everything after `--` goes to the test host, not to `dotnet run`.
        argv += ["--", *extra_args]

    ui.step(f"Running {project} ({configuration})")
    ui.info("TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1")

    # Suite blockers describe the most recent run and nothing else. Clearing them up front
    # means the file can never accumulate two contradictory verdicts from different runs -
    # "the suite hangs" and "the suite does not compile" cannot both be current, and a
    # resuming session should not have to work out which one is stale.
    progress.clear_blocker("test-suite-red")
    progress.clear_blocker("test-suite-hangs")

    captured: list[str] = []
    process = subprocess.Popen(  # noqa: S603 - argv built here
        argv,
        cwd=str(REPO_ROOT),
        env=test_env(),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )
    assert process.stdout is not None

    # Reading on a worker thread rather than iterating the pipe directly: a blocking
    # readline on a hung child cannot be interrupted, so the deadline has to be enforced
    # by a thread that is not the one blocked on I/O.
    lines: queue.Queue[str | None] = queue.Queue()

    def _pump() -> None:
        try:
            for raw in process.stdout:  # type: ignore[union-attr]
                lines.put(raw.rstrip("\n"))
        finally:
            lines.put(None)

    reader = threading.Thread(target=_pump, daemon=True)
    reader.start()

    deadline = time.monotonic() + timeout
    timed_out = False
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            timed_out = True
            break
        try:
            line = lines.get(timeout=min(remaining, 1.0))
        except queue.Empty:
            continue
        if line is None:
            break
        captured.append(line)
        print(f"    | {line}", flush=True)

    if timed_out:
        process.kill()
        process.wait(timeout=30)
        detail = f"no verdict within {timeout:.0f}s - killed"
        ui.fail(f"test run timed out after {timeout:.0f}s and was killed")
        progress.bump("testRuns")
        # The runner did its job: it started the suite, enforced the deadline and reported.
        # What it never received is a verdict to propagate, so the capability is blocked by
        # the suite rather than broken in the harness.
        progress.mark("test-runner", "blocked", detail=detail)

        # Name the culprit. The Microsoft Testing Platform prints a live "active:" line
        # naming the test it is currently inside, and on a hang that line is frozen on the
        # exact test that never returned - the single most useful fact in the whole run.
        stuck = ""
        for line in reversed(captured):
            marker = "active:" if "active:" in line else ("still running after" if "still running after" in line else "")
            if marker:
                stuck = line.split(marker, 1)[1].strip()
                break
        progress.add_blocker(
            "test-suite-hangs",
            f"No verdict within {timeout:.0f}s; killed."
            + (f" Last test still active: {stuck[:200]}" if stuck else ""),
            "That test never returns. Until it does, the suite has no verdict to propagate.",
        )
        # Whatever the previous run said about redness is now unknowable - the run that
        # would have said it never finished. A stale detail here is worse than none.
        progress.clear_blocker("test-suite-red")
        progress.log("test", False, detail, exitCode=None)
        raise HarnessError(
            f"The test suite produced no verdict within {timeout:.0f}s.",
            exit_code=6,
            remedy=(
                "A hung test is a stalled feedback loop, so the run was killed rather than "
                "left to wait. Raise the ceiling with --timeout if the suite has legitimately "
                "grown; otherwise the last line printed above is where it stopped."
            ),
        )

    exit_code = process.wait()

    summary_line = next(
        (ln for ln in reversed(captured) if "failed" in ln.lower() or "succeeded" in ln.lower()),
        "",
    ).strip()

    result = {"exitCode": exit_code, "project": project, "configuration": configuration,
              "summary": summary_line}

    progress.bump("testRuns")

    # What this capability claims is narrow and worth stating precisely: the harness can
    # run the suite and hand its verdict back unchanged. A red suite therefore *proves*
    # the runner - a correctly propagated non-zero is the harder half of the claim - and
    # says nothing about the harness. Only the cases where no verdict was ever produced
    # (the project would not compile, or the run had to be killed) leave the claim
    # untested. The suite's own redness is real news, so it is recorded where a resuming
    # session will see it: as a blocker, not as a broken capability.
    compile_failure = any("The build failed" in ln for ln in captured)

    if exit_code == 0:
        ui.ok(f"suite passed - {summary_line or 'exit 0'}")
        progress.prove(
            "test-runner",
            by="fl.py test",
            detail=f"{project} ({configuration}) exit 0 - {summary_line or 'no summary line'}",
        )
        progress.clear_blocker("test-suite-red")
        progress.clear_blocker("test-suite-hangs")
    elif compile_failure:
        first_error = next((ln.strip() for ln in captured if ": error " in ln), "")
        ui.fail(f"suite failed with exit {exit_code}")
        ui.warn("the test project did not compile - no verdict was produced")
        progress.mark(
            "test-runner",
            "blocked",
            detail=f"exit {exit_code}: {project} does not compile - {first_error[:220]}",
        )
        progress.add_blocker(
            "test-suite-red",
            f"{project} does not compile: {first_error[:220]}",
            "Fix the compile errors; until then no test result exists at all.",
        )
    else:
        ui.fail(f"suite failed with exit {exit_code}")
        ui.info("the runner propagated that exit code unchanged - the suite is red, not the harness")
        progress.prove(
            "test-runner",
            by="fl.py test",
            detail=f"{project} ({configuration}) produced a verdict and exit {exit_code} was propagated",
        )
        progress.add_blocker(
            "test-suite-red",
            f"The suite is failing: exit {exit_code} - {summary_line or 'see the run output'}",
            "Fix the failing tests. Project policy 7.2: no red test ships.",
        )
    progress.log("test", exit_code == 0, summary_line or f"exit {exit_code}", exitCode=exit_code)
    return result
