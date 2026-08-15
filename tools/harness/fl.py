#!/usr/bin/env python3
"""FrameLink autonomy harness - one entry point for the whole M0 feedback loop.

    python tools/harness/fl.py <subcommand> [options]

version2.md section 5 states the hard requirement this exists to satisfy: a **closed feedback
loop** - change code, build, deploy, observe, judge, repeat - with no human in it. The
six subcommands are the six pieces section 5.1 names, plus `status`, which is how a session that
has lost its context finds out where the build stands.

    build     produce the Native AOT linux-arm64 agent binary in an emulated container,
              and emit the SHA-256 and byte size the Fleet Manager's update feed serves
    test      run the test suite and propagate its exit code
    deploy    push the binary and the systemd unit to the mule, idempotently, and verify
              the arrival using the mule's own sha256sum
    collect   the two allowlisted diagnostics (section 3.6): screenshot and journal tail
    power     Home Assistant control of the smart plug, with wrong-entity and wear guards
    status    what has been proven, what this session can do, what to do next

Credentials
-----------
The mule password comes from ``FL_PW`` and nowhere else - never a file, a log, a shell
history, a config or a default (CLAUDE.md section 1.2), and there is deliberately no key-based
fallback. Every mule-touching subcommand fails in its first second with a named error when
``FL_PW`` is absent, rather than hanging on a prompt or quietly authenticating as someone
else. ``FL_HA_TOKEN`` is held to the same rule.

State
-----
``tools/harness/progress.json`` is written continuously by every subcommand and is not
gitignored. It is the answer to "where does this stand" for a session with no memory
(section 5.5). ``tools/harness/runs/`` holds screenshots, journal captures and the relay wear
ledger, and is gitignored.

Exit codes
----------
    0  success
    1  a harness error with no more specific code
    2  a required environment variable is missing (FL_PW, FL_HA_TOKEN)
    3  something expected does not exist (project, artifact, host unreachable)
    4  a required tool is missing (docker, dotnet, paramiko)
    5  a remote command or an HTTP call failed
    6  the build failed
    7  a relay safety guard refused the operation
    8  the relay did not confirm the state change
    9  wrong-entity abort: the relay switched but the frame stayed alive
    n  for `test`, the test suite's own exit code is propagated unchanged
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

# No .pyc files. tools/harness/ is a tracked directory and __pycache__ is not in
# .gitignore, so bytecode caching would drop untracked litter into the repository on every
# invocation. A CLI this small recompiles in milliseconds.
sys.dont_write_bytecode = True

sys.path.insert(0, str(Path(__file__).resolve().parent))

from flh import build as build_mod  # noqa: E402
from flh import collect as collect_mod  # noqa: E402
from flh import deploy as deploy_mod  # noqa: E402
from flh import power as power_mod  # noqa: E402
from flh import progress, status, testrun, ui  # noqa: E402
from flh.config import AGENT_PROJECT, HA_ENTITY, RID, TEST_PROJECT, HarnessError  # noqa: E402


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="fl.py",
        description=(
            "FrameLink autonomy harness (M0). Closes the change -> build -> deploy -> observe "
            "-> judge loop against the development mule with no human step."
        ),
        epilog=(
            "Credentials come from the environment only and are never stored:\n"
            "  FL_PW         mule SSH password        (required by deploy / collect)\n"
            "  FL_HA_TOKEN   Home Assistant token     (required by power)\n"
            "Optional overrides:\n"
            "  FL_HOST       mule address             (default 10.20.30.53)\n"
            "  FL_USER       mule username            (default framelink)\n"
            "  FL_HA_URL     Home Assistant base URL  (default http://10.20.30.250:8086)\n"
            f"  FL_HA_ENTITY  plug entity id           (default {HA_ENTITY})\n"
            "\n"
            "Typical loop:\n"
            "  python tools/harness/fl.py test\n"
            "  python tools/harness/fl.py build\n"
            "  FL_PW='...' python tools/harness/fl.py deploy\n"
            "  FL_PW='...' python tools/harness/fl.py collect\n"
            "  python tools/harness/fl.py status\n"
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    subparsers = parser.add_subparsers(dest="command", metavar="<subcommand>")

    # --- build --------------------------------------------------------------
    p_build = subparsers.add_parser(
        "build",
        help="build the Native AOT linux-arm64 agent binary",
        description=(
            "Builds src/FrameLink.Agent for linux-arm64 inside an emulated arm64 container and "
            "writes build/out/fl-agent plus build/out/agent-release.json - the frozen "
            "AgentRelease shape (version, runtimeIdentifier, sha256, sizeBytes, url) that the "
            "Fleet Manager's update feed serves verbatim.\n\n"
            "The default image is framelink-build:arm64, the stock .NET SDK image plus clang, "
            "zlib1g-dev and binutils, which the stock image does NOT carry and Native AOT "
            "requires. It is built on first use and cached thereafter."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_build.add_argument("--project", default=AGENT_PROJECT, help=f"project directory (default {AGENT_PROJECT})")
    p_build.add_argument("--rid", default=RID, help=f"runtime identifier (default {RID})")
    p_build.add_argument("--configuration", default="Release", help="build configuration (default Release)")
    p_build.add_argument("--version", default=None, help="override the version string (default 0.0.0+<git sha>)")
    p_build.add_argument(
        "--stock-image",
        action="store_true",
        help="use mcr.microsoft.com/dotnet/sdk:10.0 verbatim; build.sh then installs the AOT toolchain at runtime",
    )
    p_build.add_argument("--rebuild-image", action="store_true", help="rebuild framelink-build:arm64 from scratch")

    # --- test ---------------------------------------------------------------
    p_test = subparsers.add_parser(
        "test",
        help="run the test suite and propagate its exit code",
        description=(
            "Runs `dotnet run --project tests/FrameLink.Tests` - not `dotnet test`, which "
            "discovers zero tests under xunit.v3 4.0.0 on the .NET 10 SDK (the csproj explains "
            "why). TESTINGPLATFORM_TELEMETRY_OPTOUT=1 and DOTNET_CLI_TELEMETRY_OPTOUT=1 are set: "
            "a project whose first principle is no phone-home should not ship usage data from "
            "its own test suite. The suite's exit code becomes this command's exit code."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_test.add_argument("--project", default=TEST_PROJECT, help=f"test project (default {TEST_PROJECT})")
    p_test.add_argument("--configuration", default="Debug", help="build configuration (default Debug)")
    p_test.add_argument(
        "--timeout",
        type=float,
        default=testrun.DEFAULT_TIMEOUT_S,
        help=f"seconds before a silent suite is killed (default {testrun.DEFAULT_TIMEOUT_S:.0f})",
    )
    p_test.add_argument("args", nargs="*", help="extra arguments passed through to the test host")

    # --- deploy -------------------------------------------------------------
    p_deploy = subparsers.add_parser(
        "deploy",
        help="push the built binary and systemd unit to the mule",
        description=(
            "Uploads build/out/fl-agent over SFTP to a staging path, verifies its hash on the "
            "mule, installs it with an atomic rename (a running ELF cannot be written in place "
            "but can be renamed over), installs fl-agent.service if its content differs, enables "
            "and restarts the service, then proves the deploy by comparing the mule's own "
            "sha256sum against what was built.\n\n"
            "Idempotent: a second run with the same binary and unit uploads nothing, reloads "
            "nothing and restarts nothing. Requires FL_PW."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_deploy.add_argument("--force", action="store_true", help="re-upload, rewrite and restart even if unchanged")
    p_deploy.add_argument("--no-restart", action="store_true", help="install but do not restart the service")
    p_deploy.add_argument("--journal-lines", type=int, default=20, help="journal lines to show afterwards (default 20)")

    # --- collect ------------------------------------------------------------
    p_collect = subparsers.add_parser(
        "collect",
        help="screenshot and journal tail from the mule",
        description=(
            "The complete diagnostics allowlist from version2.md section 3.6. Output lands in a "
            "timestamped directory under tools/harness/runs/.\n\n"
            "Screenshot has two paths and picks whichever can work. `grim` needs a live Wayland "
            "session and grim installed (v1 used it in docs/8-webrtc-validation.md step 7; it is "
            "not a base-image package). The framebuffer path reads /dev/fb0 and encodes the PNG "
            "on the workstation - it needs nothing installed on the mule and is the only path "
            "that can see the agent's console stage on /dev/tty1. Requires FL_PW."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_collect.add_argument("--unit", default="fl-agent.service", help="unit to tail (default fl-agent.service)")
    p_collect.add_argument("--system", action="store_true", help="tail the whole system journal instead of a unit")
    p_collect.add_argument("--lines", type=int, default=200, help="journal lines (default 200)")
    p_collect.add_argument("--boot", action="store_true", help="restrict the journal to the current boot")
    p_collect.add_argument("--since", default=None, help="journalctl --since expression, e.g. '10 min ago'")
    p_collect.add_argument("--no-screenshot", action="store_true", help="journal only")
    p_collect.add_argument("--no-journal", action="store_true", help="screenshot only")
    p_collect.add_argument(
        "--method", choices=("auto", "grim", "framebuffer"), default="auto", help="screenshot path (default auto)"
    )
    p_collect.add_argument(
        "--pixel-format",
        choices=("bgrx", "rgbx", "rgb565"),
        default="bgrx",
        help="framebuffer pixel order (default bgrx, the DRM fbdev emulation default)",
    )
    p_collect.add_argument(
        "--rotate",
        type=int,
        choices=(0, 90, 180, 270),
        default=0,
        help="rotate the framebuffer capture; v1 booted with fbcon=rotate:1 so 0 may not be upright",
    )

    # --- power --------------------------------------------------------------
    p_power = subparsers.add_parser(
        "power",
        help="smart-plug control for the mule",
        description=(
            "Controls the Home Assistant switch feeding the frame. Safety rules are on by "
            "default and cannot be disabled piecemeal:\n"
            "  * a minimum interval between relay operations, and a ceiling per rolling hour, "
            "enforced from a ledger that persists across sessions;\n"
            "  * already being in the requested state performs no relay operation at all;\n"
            "  * the relay must confirm its new state through Home Assistant;\n"
            "  * if the frame still answers seconds after relay-off, the wrong entity is being "
            "switched - the harness restores power immediately and aborts loudly.\n\n"
            "Requires FL_HA_TOKEN. `state` performs no relay operation and needs no guard."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_power.add_argument("action", choices=("on", "off", "cycle", "state"), help="what to do")
    p_power.add_argument("--reason", default="", help="recorded with the operation; say why the relay moved")
    p_power.add_argument("--off-seconds", type=float, default=None, help="how long to stay dark during a cycle")
    p_power.add_argument("--wait", type=float, default=180.0, help="seconds to wait for the frame after power-on")
    p_power.add_argument(
        "--i-accept-wear", action="store_true", help="proceed past the per-hour operation ceiling"
    )
    p_power.add_argument(
        "--skip-liveness-check",
        action="store_true",
        help="DANGEROUS: skip the wrong-entity check after relay-off",
    )

    # --- status -------------------------------------------------------------
    p_status = subparsers.add_parser(
        "status",
        help="where the build stands and what this session can do",
        description=(
            "Prints the recorded progress (what has been proven, by which command, when) "
            "alongside a live probe of this session's capabilities, and refreshes the blockers "
            "and next actions in tools/harness/progress.json. This is the first thing a session "
            "with no memory should run."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_status.add_argument("--json", action="store_true", help="emit the progress file and probe as JSON")

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)

    if not args.command:
        parser.print_help()
        return 0

    try:
        if args.command == "build":
            with progress.activity("build", project=args.project, rid=args.rid):
                release = build_mod.build(
                    project=args.project,
                    rid=args.rid,
                    configuration=args.configuration,
                    version=args.version,
                    stock_image=args.stock_image,
                    rebuild_image=args.rebuild_image,
                )
            progress.log("build", True, f"{release['version']} {release['sizeBytes']}B {release['sha256'][:12]}")
            ui.kv(
                {
                    "version": release["version"],
                    "runtimeIdentifier": release["runtimeIdentifier"],
                    "sizeBytes": f"{release['sizeBytes']:,}",
                    "sha256": release["sha256"],
                    "url": release["url"],
                }
            )
            return 0

        if args.command == "test":
            with progress.activity("test", project=args.project):
                result = testrun.run(
                    project=args.project,
                    configuration=args.configuration,
                    extra_args=args.args,
                    timeout=args.timeout,
                )
            return int(result["exitCode"])

        if args.command == "deploy":
            with progress.activity("deploy"):
                outcome = deploy_mod.deploy(
                    force=args.force, restart=not args.no_restart, journal_lines=args.journal_lines
                )
            changed = outcome["binaryChanged"] or outcome["unitChanged"]
            progress.log(
                "deploy",
                True,
                f"{outcome['version']} -> {outcome['host']} ({'changed' if changed else 'already current'})",
            )
            return 0

        if args.command == "collect":
            with progress.activity("collect"):
                result = collect_mod.collect(
                    unit=None if args.system else args.unit,
                    lines=args.lines,
                    boot=args.boot,
                    since=args.since,
                    want_screenshot=not args.no_screenshot,
                    want_journal=not args.no_journal,
                    method=args.method,
                    pixel_format=args.pixel_format,
                    rotate=args.rotate,
                )
            ui.info(f"artifacts in {result['directory']}")
            progress.log("collect", not result["problems"], f"{result['directory']}")
            return 1 if result["problems"] else 0

        if args.command == "power":
            if args.action == "state":
                power_mod.status()
                return 0
            with progress.activity("power", action=args.action, reason=args.reason):
                if args.action == "off":
                    outcome = power_mod.off(
                        reason=args.reason,
                        accept_wear=args.i_accept_wear,
                        skip_liveness_check=args.skip_liveness_check,
                    )
                elif args.action == "on":
                    outcome = power_mod.on(
                        reason=args.reason, accept_wear=args.i_accept_wear, wait_s=args.wait
                    )
                else:
                    outcome = power_mod.cycle(
                        reason=args.reason,
                        accept_wear=args.i_accept_wear,
                        off_s=args.off_seconds,
                        wait_s=args.wait,
                    )
            power_mod.prove_capability(outcome)
            progress.log("power", True, f"{args.action} - {outcome}")
            return 0

        if args.command == "status":
            status.show(json_output=args.json)
            return 0

    except HarnessError as exc:
        ui.fail(str(exc))
        if exc.remedy:
            for line in exc.remedy.splitlines():
                print(f"      {line}", file=sys.stderr)
        progress.log(args.command, False, str(exc)[:300])
        return exc.exit_code
    except KeyboardInterrupt:
        ui.fail("interrupted")
        return 130

    parser.print_help()
    return 0


if __name__ == "__main__":
    # The progress file records that this subcommand exists at all, so `status` on a fresh
    # checkout shows the real shape of M0 rather than an empty file.
    sys.exit(main())
