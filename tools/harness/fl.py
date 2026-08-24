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
    parity    the first of milestone Mn+3's three bars: a state diff of a live frame against
              the frozen v1 reference, with every expected difference recorded and every
              gap in coverage named
    array     the bench measurement of the XVF3800's amplifier pin: stop the agent, connect
              the array, read X0D31 before anything writes to it, start the agent again
    cards     the SD card register: which of the three unmarked cards is where, what is on
              each one, and a check of that record against the card in the reader
    status    what has been proven, what this session can do, what to do next

Credentials
-----------
The mule password comes from ``FL_PW`` and nowhere else - never a file, a log, a shell
history, a config or a default (CLAUDE.md section 1.2), and there is deliberately no key-based
fallback. Every mule-touching subcommand fails in its first second with a named error when
``FL_PW`` is absent, rather than hanging on a prompt or quietly authenticating as someone
else. ``FL_HA_TOKEN`` is held to the same rule.

``FL_PW`` also answers ``sudo``. A stock Raspberry Pi OS Lite image has no NOPASSWD rule, so
`deploy` and `collect` - which must install a binary, write a unit and read /dev/fb0 - send
the password on the command's **stdin** (``sudo -S``), never in the command text. Whether a
password is needed is probed once per connection, so a frame that *has* been given NOPASSWD
works unchanged. The agent is unaffected either way: its unit runs as root and never calls
sudo.

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
    3  something expected does not exist, or a credential was refused (project, artifact,
       host unreachable, SSH login rejected, sudo refused on the mule)
    4  a required tool is missing (docker, dotnet, paramiko)
    5  a remote command or an HTTP call failed
    6  the build failed
    7  a relay safety guard refused the operation
    8  the relay did not confirm the state change
    9  wrong-entity abort: the relay switched but the frame stayed alive
   10  the Fleet Manager serves a different agent than the one being deployed, so the
       deploy would be converged away (section 2.8) - `deploy --allow-feed-drift` overrides
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
from flh import debuglog as debug_mod  # noqa: E402
from flh import cards as cards_mod  # noqa: E402
from flh import collect as collect_mod  # noqa: E402
from flh import deploy as deploy_mod  # noqa: E402
from flh import flash as flash_mod  # noqa: E402
from flh import parity as parity_mod  # noqa: E402
from flh import power as power_mod  # noqa: E402
from flh import progress, status, testrun, ui  # noqa: E402
from flh import xvf as xvf_mod  # noqa: E402
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
            "  FL_PW         mule SSH password, and the answer to sudo where the image\n"
            "                needs one       (required by deploy / collect)\n"
            "  FL_HA_TOKEN   Home Assistant token     (required by power)\n"
            "Optional overrides:\n"
            "  FL_HOST       mule address             (default 10.20.30.53)\n"
            "  FL_USER       mule username            (default framelink)\n"
            "  FL_CONTROL_URL Fleet Manager base URL  (default http://10.20.30.200:5199)\n"
            "  FL_HA_URL     Home Assistant base URL  (default http://10.20.30.250:8123)\n"
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
    parser.add_argument(
        "--debug",
        dest="debug",
        action="store_true",
        default=None,
        help=(
            "write an excessive, bounded record of this run - every remote command with both "
            "streams and its duration, every gate with the values it compared - to "
            "tools/harness/runs/<stamp>-<cmd>/debug.log on THIS machine. Never on the frame, "
            "never in the frame's journal: see flh/debuglog.py. FL_DEBUG=1 does the same."
        ),
    )
    parser.add_argument(
        "--no-debug",
        dest="debug",
        action="store_false",
        help="suppress the debug log even if FL_DEBUG is set (array flash forces it on regardless)",
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
            "Before any of that it reads the Fleet Manager's update feed and REFUSES a deploy "
            "the feed is going to undo. Section 2.8 has the agent match the served version - "
            "upgrade or downgrade - at its next handshake and hourly after, so installing a "
            "binary the Fleet Manager does not serve is erased, measured at six seconds. The "
            "refusal names which side is stale; --allow-feed-drift proceeds anyway.\n\n"
            "Idempotent: a second run with the same binary and unit uploads nothing, reloads "
            "nothing and restarts nothing. Requires FL_PW."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_deploy.add_argument("--force", action="store_true", help="re-upload, rewrite and restart even if unchanged")
    p_deploy.add_argument("--no-restart", action="store_true", help="install but do not restart the service")
    p_deploy.add_argument("--journal-lines", type=int, default=20, help="journal lines to show afterwards (default 20)")
    p_deploy.add_argument(
        "--allow-feed-drift",
        action="store_true",
        help="install even though the Fleet Manager serves a different agent, which it will converge away",
    )

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
            "that can see the agent's console stage on /dev/tty8. Requires FL_PW."
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

    # --- parity -------------------------------------------------------------
    p_parity = subparsers.add_parser(
        "parity",
        help="state-diff a live frame against the frozen v1 reference",
        description=(
            "The first of milestone Mn+3's three bars. Runs read-only, unprivileged probes over "
            "SSH, then hands the result to tools/FrameLink.Parity, which compares it against "
            "reference/v1-state-inventory.txt, against an expected-difference ledger, and against "
            "the resource catalog.\n\n"
            "Three kinds of difference are separated, because a flat diff proves nothing: "
            "something v1 had and this frame does not, something this frame has and v1 never did, "
            "and a value that moved - which for a package splits again into forward (a security "
            "update, the only drift this project tolerates, computed by the same code the Fleet "
            "Manager uses) and backward.\n\n"
            "Coverage is reported rather than assumed. Inventory sections nothing can compare are "
            "declared with their reason, and so are the catalog resources whose state the v1 "
            "capture never recorded - those cannot be verified by a state diff at all, and are "
            "what the other two bars exist for.\n\n"
            "One probe needs root and is skipped unless --elevate is given; a run without it "
            "reports 'incomplete' and names them rather than calling a frame at parity on "
            "evidence it never looked at. Nothing here writes to the frame. Requires FL_PW unless "
            "--coverage or --from is used.\n\n"
            "Exit 0 at parity, 2 differences found, 3 the comparison could not be completed."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_parity.add_argument(
        "--elevate",
        action="store_true",
        help="also run the probe that needs root (the array's firmware version over USB)",
    )
    p_parity.add_argument(
        "--collect-only",
        action="store_true",
        help="write the observation and do not judge it",
    )
    p_parity.add_argument(
        "--from",
        dest="from_observed",
        default=None,
        help="judge an observed.json that already exists instead of reaching a frame",
    )
    p_parity.add_argument(
        "--coverage",
        action="store_true",
        help="print what a state diff can and cannot answer; needs no frame and no credential",
    )

    # --- array --------------------------------------------------------------
    p_array = subparsers.add_parser(
        "array",
        help="bench measurement of the XVF3800 amplifier pin",
        description=(
            "Answers resource-catalog open question 13 - is the speaker amplifier on or off "
            "on the firmware a factory-fresh array ships with - by reading X0D31 before "
            "anything writes to it. Three actions, and the whole procedure:\n\n"
            "  1. fl.py array hold      stops fl-agent and proves it stopped\n"
            "  2. unplug the frame's own array, plug in the array to be measured\n"
            "  3. fl.py array read      the measurement\n"
            "  4. unplug it, plug the frame's own array back in\n"
            "  5. fl.py array read      optional: the same reading on the frame's own array,\n"
            "                           now with the connect order proved\n"
            "  6. fl.py array release   starts fl-agent, waits for the frame to say InSync\n\n"
            "Never connect an array before step 1 has said SAFE TO CONNECT. One array at a\n"
            "time, always.\n\n"
            "The ordering matters and is proved rather than trusted: `read` compares the "
            "kernel's enumeration timestamp for the array against systemd's stop timestamp "
            "for the unit, and refuses to certify a reading taken on an array the agent could "
            "have written to. It refuses outright if two arrays are attached, because "
            "xvf_host has no device selector and could not say which one answered.\n\n"
            "Only VERSION, GPO_READ_VALUES and the four BLD_* build-provenance reads "
            "(BLD_REPO_HASH, BLD_MSG, BLD_HOST, BLD_MODIFIED) are ever sent, and each one is "
            "checked against three independent gates rather than one allowlist. No GPO write, "
            "no dfu-util, no flashing path of any kind. The only writes to the frame are the "
            "stop and the start. Requires FL_PW.\n\n"
            "Exit 0 when the step did what it says, 2 when the reading could not be certified "
            "Exit 0 when the step did what it says, 2 when the reading could not be certified or the frame did not report InSync in time - never 0 for either of those.\n\n"
            "THE FLASH (decision 91). `array flash` is the attended bench write of the pinned\n"
            "DFU image, and by default it writes NOTHING: with no flags it evaluates every\n"
            "gate, reports each one, and resolves no writing program at all. Five independent\n"
            "conditions stand in front of a write and no two of them live in the same place -\n"
            "--write must be present; --authorise must name the pinned image's sha256; this\n"
            "harness's pin and the agent's must agree; the bytes on the frame must re-hash to\n"
            "the pin in the instant before dfu-util starts; and only alt 1, the Upgrade\n"
            "partition, is ever written. Alt 0 is the Factory partition and is refused by\n"
            "number. The agent must be held first, exactly as `read` requires.\n\n"
            "  fl.py array flash                    every gate, nothing run\n"
            "  fl.py array flash --simulate <what>  the whole system, with a generated stub in\n"
            "                                       place of dfu-util; no byte reaches the array\n"
            "  fl.py array flash --write --authorise <sha256>[:ticket]   the real write\n"
            "  fl.py array flash --list-dfu         dfu-util -l, which WRITES NOTHING\n"
            "  fl.py array flash --clear-marker     after a person has looked at the unit\n"
            "  fl.py array stage                    fetch and verify the pinned image\n"
            "  fl.py array clean                    remove the bench directory from the frame\n\n"
            "An interrupted write leaves a durable marker on the card and every later flash -\n"
            "agent or bench - is refused until a person removes it. That is deliberate: a\n"
            "cgroup kill, a power cut and a crash all leave the same array behind, so nothing\n"
            "knows how far that write got, and a state nothing can measure is one a person\n"
            "should look at. `array flash` writes an excessive debug log for every run and\n"
            "cannot be told not to.\n\n"
            "`array runbook` was removed on 2026-08-24 with this project's Safe Mode support.\n"
            "See reference/xvf3800-recovery-model.md, which keeps the measured record."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_array.add_argument(
        "action",
        choices=("hold", "read", "release", "flash", "stage", "clean"),
        help="what to do",
    )
    p_array.add_argument(
        "--authorise",
        default=None,
        metavar="SHA256[:TICKET]",
        help=(
            "authorise ONE write of the pinned target image. The digest before the colon must "
            f"equal the pinned target's sha256 ({flash_mod.TARGET.sha256}); anything after it "
            "is your own ticket text. This is the same string, in the same shape, that the "
            f"agent's {flash_mod.AUTHORISATION_KEY} fleet setting carries."
        ),
    )
    p_array.add_argument(
        "--write",
        action="store_true",
        help=(
            "PERFORM THE REAL WRITE. Without this, `array flash` evaluates every gate and "
            "resolves no writing program at all. Mutually exclusive with --simulate."
        ),
    )
    p_array.add_argument(
        "--simulate",
        default=None,
        choices=tuple(sorted(flash_mod.SIMULATIONS)),
        help=(
            "run the whole system around a flash with a generated stub in place of dfu-util. "
            "No byte reaches the array. The marker, the gates, the re-enumeration poll and "
            "the artifact are all real."
        ),
    )
    p_array.add_argument(
        "--clear-marker",
        action="store_true",
        help=(
            f"remove {flash_mod.MARKER_PATH} after a person has looked at the microphone "
            "unit. Nothing in the flash path can do this for you; that is the point of the latch."
        ),
    )
    p_array.add_argument(
        "--reenumeration-timeout",
        type=float,
        default=flash_mod.REENUMERATION_TIMEOUT_S,
        metavar="SECONDS",
        help=(
            f"how long the array is given to come back (default {flash_mod.REENUMERATION_TIMEOUT_S:.0f}, "
            "matching the agent's ArrayFirmwareFlash.ReEnumerationTimeout). Shortening it makes "
            "a bench run stop being a rehearsal of the fleet's path; the value used is recorded "
            "in the run artifact either way."
        ),
    )
    p_array.add_argument(
        "--list-dfu",
        action="store_true",
        help=(
            "run `dfu-util -l`, which enumerates alt settings and WRITES NOTHING. In Safe Mode "
            "it lists a third alt setting, which is the only proof Safe Mode was entered."
        ),
    )
    p_array.add_argument(
        "--repeats",
        type=int,
        default=xvf_mod.DEFAULT_REPEATS,
        help=f"GPO readbacks to take (default {xvf_mod.DEFAULT_REPEATS}); one cannot show a pin is stable",
    )
    p_array.add_argument(
        "--timeout",
        type=float,
        default=xvf_mod.CONVERGE_TIMEOUT_S,
        help=f"seconds `release` waits for InSync (default {xvf_mod.CONVERGE_TIMEOUT_S:.0f})",
    )

    # --- cards --------------------------------------------------------------
    p_cards = subparsers.add_parser(
        "cards",
        help="the SD card register: which unmarked card is where, and a check against reality",
        description=(
            "There are three physical microSD cards in this project and none of them is "
            "marked, so which card is where can only be answered from a record. "
            "tools/harness/cards.json is that record - committed, pointed at from "
            "progress.json's orientation block, and readable by a session that remembers "
            "nothing.\n\n"
            "  list      the whole register: position, contents, image, agent version,\n"
            "            handling, and whether the card can be recognised at all\n"
            "  check     read whatever is in the workstation's reader and compare it against\n"
            "            what the register CLAIMS is in there. This is the action that keeps\n"
            "            the record honest; every disagreement is loud and exits 2\n"
            "  identify  print what the workstation can actually see about the card in the\n"
            "            reader, and with --card, capture it as that card's fingerprint\n"
            "  record    record that a card moved. --why is required\n"
            "  label     propose the marker file that would make a card self-identifying.\n"
            "            Prints the exact bytes and writes nothing unless --write is given\n"
            "  gate      may a tool open the card in the reader as --card? Stricter than\n"
            "            `check`: the MBR signature, the capacity and the partition layout\n"
            "            must all be RECORDED and all AGREE, because a field the register\n"
            "            never captured is not weak evidence, it is none. --json emits the\n"
            "            whole answer for a script to read; exit 0 means a read may proceed\n"
            "  image     record an image taken OF a card, from the receipt the imaging script\n"
            "            wrote beside it (--receipt), or add a further copy of one already\n"
            "            recorded (--image <id> --add-copy '<where>'). The card's handling\n"
            "            text is derived from how many copies exist, so it stops claiming to\n"
            "            be the only copy at the moment that stops being true\n\n"
            "Read-only apart from `label --write`, which writes one text file into a mounted "
            "FAT volume and refuses unless `check` already agrees which card it is. Nothing "
            "here flashes, formats, partitions or opens a physical drive handle - `gate` exists "
            "precisely so that the one script which does open a handle, "
            "tools/harness/image-v1-card.ps1, does not carry its own drifting copy of these "
            "rules.\n\n"
            "Nothing readable through a USB card reader is unique to the physical card: the "
            "reader passes no SD CID through, the MBR signature belongs to the image and the "
            "filesystem serial to whoever formatted it. `check` therefore reports 'ambiguous' "
            "rather than choosing, and the register's identityFields block says what each "
            "field can and cannot prove - on Linux as well as on Windows.\n\n"
            "Exit 0 when the register and reality agree, 2 when they do not, 3 when there is "
            "no reader, no card or no such card id."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p_cards.add_argument(
        "action",
        nargs="?",
        default="list",
        choices=("list", "check", "identify", "record", "label", "gate", "image"),
        help="what to do (default list)",
    )
    p_cards.add_argument("--card", default=None, help="card id from the register")
    p_cards.add_argument(
        "--kind",
        choices=("reader", "frame", "desk", "unknown"),
        default=None,
        help="for `record`: what kind of place the card is now in; `reader` is the one `check` verifies",
    )
    p_cards.add_argument("--where", default=None, help="for `record`: where it is now, in words")
    p_cards.add_argument("--why", default="", help="for `record`: what the move was for; required")
    p_cards.add_argument(
        "--force",
        action="store_true",
        help="for `identify --card`: overwrite a fingerprint that disagrees, having decided which card this is",
    )
    p_cards.add_argument(
        "--write",
        action="store_true",
        help="for `label`: actually write the marker file. Without it the bytes are printed and nothing is written",
    )
    p_cards.add_argument(
        "--json",
        action="store_true",
        help="for `gate`: emit the whole verdict as JSON on stdout, for a script to read",
    )
    p_cards.add_argument(
        "--require",
        default=None,
        metavar="FIELDS",
        help=(
            "for `gate`: comma-separated identity fields that must be recorded and agree "
            f"(default {','.join(cards_mod.GATE_REQUIRED)})"
        ),
    )
    p_cards.add_argument(
        "--receipt",
        default=None,
        help="for `image`: the receipt JSON the imaging script wrote beside the image",
    )
    p_cards.add_argument(
        "--image",
        default=None,
        dest="image_id",
        help="for `image --add-copy`: which recorded image gained a copy",
    )
    p_cards.add_argument(
        "--add-copy",
        default=None,
        dest="add_copy",
        metavar="WHERE",
        help="for `image`: record that the image now also exists here, in words",
    )
    p_cards.add_argument(
        "--rehash",
        action="store_true",
        help="for `image --receipt`: re-read and re-hash the whole image file before recording it",
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

    # One debug log per invocation, installed before anything connects so that the very
    # first remote command is recorded. `array flash` forces its own on and opens it inside
    # its own run directory, so it is excluded here rather than given two.
    if not (args.command == "array" and getattr(args, "action", "") == "flash"):
        debug_mod.open_log(args.command or "fl", flag=getattr(args, "debug", None))

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
                    force=args.force,
                    restart=not args.no_restart,
                    journal_lines=args.journal_lines,
                    allow_feed_drift=args.allow_feed_drift,
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

        if args.command == "parity":
            if args.coverage:
                return parity_mod.coverage()
            with progress.activity("parity", elevated=args.elevate):
                outcome = parity_mod.run(
                    elevate=args.elevate,
                    collect_only=args.collect_only,
                    observed=Path(args.from_observed) if args.from_observed else None,
                )
            progress.log(
                "parity",
                outcome["exitCode"] == 0,
                f"{outcome.get('verdict', 'collected only')} - {outcome['directory']}",
            )
            return int(outcome["exitCode"])

        if args.command == "array":
            # Dispatched ahead of the progress.activity block because `flash` opens its own
            # debug log and writes its own artifact - wrapping it in a second one would give a
            # flash two records of itself that could disagree. A `runbook` action stood beside
            # this one and printed the Safe Mode procedure; it went on 2026-08-24 with the rest
            # of this project's Safe Mode support, and reference/xvf3800-recovery-model.md
            # keeps the measured record of what was dropped.
            if args.action == "flash" and args.clear_marker:
                with flash_mod.ssh.connect() as mule:
                    existing = flash_mod.clear_marker(mule)
                if existing is None:
                    ui.ok(f"No marker at {flash_mod.MARKER_PATH}. Nothing to clear.")
                else:
                    ui.ok(f"Removed {flash_mod.MARKER_PATH}, which held: {existing}")
                    ui.info(
                        "The agent latches this at construction, so a running agent that has "
                        "already seen it stays refusing until it restarts."
                    )
                progress.log("array", True, f"cleared marker: {existing or 'none present'}")
                return 0

            if args.action == "flash" and args.list_dfu:
                # Read-only: dfu-util -l enumerates and writes nothing.
                #
                # This still names Safe Mode, and that is deliberate rather than a leftover.
                # The product dropped Safe Mode support on 2026-08-24: no runbook, no on-screen
                # gesture, no wedged-board detection, and a board that has stopped enumerating
                # goes back to the maintainer. This file is the maintainer's bench tool, and the
                # maintainer is exactly who receives that board. Removing the one read-only
                # command that can tell them which mode it came up in would take the diagnostic
                # away from the only person left who is supposed to use it. What was dropped is
                # the product pretending it has a way back; the hardware still has one, and
                # reference/xvf3800-recovery-model.md is the record of how it works.
                with flash_mod.ssh.connect() as mule:
                    listing = flash_mod.list_dfu(mule)
                ui.block("dfu-util -l", listing)
                safe_mode = "DataPartition" in listing
                ui.info(
                    "Three alt settings including DataPartition means Safe Mode."
                    if safe_mode
                    else "Two alt settings means run-time mode, not Safe Mode."
                )
                progress.log("array", True, f"dfu-util -l, safe mode: {safe_mode}")
                return 0

            if args.action == "stage":
                with progress.activity("array", action="stage"):
                    outcome = flash_mod.stage()
                progress.log(
                    "array", True,
                    f"staged {len(outcome['staged'])} pinned images into {outcome['directory']}",
                )
                return 0

            if args.action == "clean":
                with progress.activity("array", action="clean"):
                    outcome = flash_mod.clean()
                progress.log("array", True, f"removed {outcome['removed']}")
                return 0

            if args.action == "flash":
                outcome = flash_mod.flash(
                    authorise=args.authorise,
                    write=args.write,
                    simulate=args.simulate,
                    reenumeration_timeout_s=args.reenumeration_timeout,
                )
                progress.log(
                    "array",
                    bool(outcome.get("succeeded", outcome.get("permitted", False))),
                    f"{outcome['mode']}: {outcome.get('outcome', 'no outcome recorded')}",
                )
                # A refusal is the command doing its job, not a harness fault - but it must
                # never exit 0, because an operator scripting around this has to be able to
                # tell "the write happened" from "the write was refused".
                if outcome.get("succeeded"):
                    return 0
                return 0 if outcome.get("permitted") and args.simulate is None and not args.write else 2

            with progress.activity("array", action=args.action):
                if args.action == "hold":
                    outcome = xvf_mod.hold()
                    summary = f"fl-agent {outcome['activeState']}, {outcome['arraysAttached']} array(s) attached"
                    succeeded = outcome["activeState"] in ("inactive", "failed")
                elif args.action == "read":
                    outcome = xvf_mod.read(repeats=args.repeats)
                    # The whole reading, in one line, in the file that is NOT gitignored.
                    # tools/harness/runs/ holds the verbatim capture and is swept; this is the
                    # record that survives, so it carries every value the question turns on.
                    summary = (
                        f"firmware {outcome['firmware']}, "
                        f"build {outcome['buildIdentity'] or 'not reported'}, "
                        f"X0D31={outcome['amplifierPin']} "
                        f"(mute {outcome['muteButton']}, LED {outcome['ledRing']}), "
                        f"stable={outcome['stable']}, ordering {outcome['ordering']}, "
                        f"serial {outcome['usb'].get('serial', '?')}"
                    )
                    succeeded = bool(outcome["certified"])
                else:
                    outcome = xvf_mod.release(timeout_s=args.timeout)
                    summary = f"fl-agent {outcome['activeState']}, {outcome['selfReport'] or 'no self-report'}"
                    succeeded = bool(outcome["converged"])
            progress.log("array", succeeded, summary)
            # A reading that could not be certified, or a frame that did not come back, is not
            # a harness failure - it is a result the operator has to see and decide about - so
            # it exits 2 rather than 0, and never masquerades as success.
            return 0 if succeeded else 2

        if args.command == "cards":
            with progress.activity("cards", action=args.action):
                if args.action == "list":
                    code = cards_mod.show()
                elif args.action == "check":
                    code = cards_mod.check()
                elif args.action == "identify":
                    code = cards_mod.identify(card_id=args.card, force=args.force)
                elif args.action == "record":
                    if not (args.card and args.kind and args.where):
                        raise HarnessError(
                            "`cards record` needs --card, --kind, --where and --why.",
                            exit_code=1,
                            remedy=(
                                "e.g. python tools/harness/fl.py cards record --card blank "
                                "--kind reader --where 'workstation USB reader' "
                                "--why 'staged for the M2.5 flash'"
                            ),
                        )
                    code = cards_mod.record(
                        card_id=args.card, kind=args.kind, where=args.where, why=args.why
                    )
                elif args.action == "gate":
                    if not args.card:
                        raise HarnessError(
                            "`cards gate` needs --card: it answers a question about one "
                            "named card and will not pick one.",
                            exit_code=1,
                            remedy="e.g. python tools/harness/fl.py cards gate --card v1 --json",
                        )
                    required = (
                        tuple(f.strip() for f in args.require.split(",") if f.strip())
                        if args.require
                        else cards_mod.GATE_REQUIRED
                    )
                    code = cards_mod.gate(
                        card_id=args.card, require=required, json_output=args.json
                    )
                elif args.action == "image":
                    code = cards_mod.image(
                        receipt=args.receipt,
                        image_id=args.image_id,
                        add_copy=args.add_copy,
                        rehash=args.rehash,
                    )
                else:
                    if not args.card:
                        raise HarnessError("`cards label` needs --card.", exit_code=1)
                    code = cards_mod.label(card_id=args.card, write=args.write)
            # A disagreement between the register and the card in the reader is not a harness
            # failure - it is the finding this subcommand exists to produce - so it exits 2 and
            # is logged as unsuccessful, which is what puts it in front of the next session.
            progress.log("cards", code == 0, f"{args.action} -> exit {code}")
            return code

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
    finally:
        debug_mod.close_log()

    parser.print_help()
    return 0


if __name__ == "__main__":
    # The progress file records that this subcommand exists at all, so `status` on a fresh
    # checkout shows the real shape of M0 rather than an empty file.
    sys.exit(main())
