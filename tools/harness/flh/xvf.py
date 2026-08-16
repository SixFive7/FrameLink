"""The one-shot amplifier-default measurement on an XVF3800 microphone array.

What this answers
-----------------
``reference/resource-catalog.md`` open question 13: **on the firmware a factory-fresh
reSpeaker XVF3800 ships with (2.0.6), does the speaker amplifier boot on or off?**

``X0D31`` is the amplifier-enable pin and it is active-low, so ``0`` means the amplifier is
enabled. The agent's ``audio.xvf3800.gpo-x0d31-amp-enable`` resource asserts it - but only
behind ``firmware.xvf3800.version``, which is behind ``tool.xvf-host.installed``, which
fetches six files from GitHub. A frame that cannot reach GitHub therefore never asserts the
pin, and the answer decides what such a frame *is*: merely untuned if the amplifier boots
on, completely silent if it boots off. Nothing in the agent depends on the answer - Observe
reads the pin rather than assuming it - which is exactly why the question has survived
unmeasured, and why the measurement has to be taken deliberately.

The hazard this module is shaped around
---------------------------------------
**A running frame changes the array before it can be read.** Two resources can write to it:

* ``audio.xvf3800.gpo-x0d31-amp-enable`` runs ``xvf_host GPO_WRITE_VALUE 31 0`` whenever it
  observes the pin at anything other than ``0``. That is the one that destroys *this*
  measurement, and it destroys it silently - afterwards the pin reads ``0`` and looks like a
  factory default. It only runs when its dependency ``firmware.xvf3800.version`` is in sync,
  so on a 2.0.6 array it is ``Blocked(dependency)`` and never observes at all
  (``ReconcileLoop`` checks ``Blocker`` *before* Observe). That is a fact about today's
  catalog, not a safety property to lean on: a fresh array that happens to ship at 2.0.10
  clears the dependency immediately and the write lands on the first pass.
* ``firmware.xvf3800.version`` runs ``dfu-util -R -e -a 1 -D <image>`` and pins every array
  to 2.0.10, which would destroy the *opportunity* irreversibly. It refuses unless a fleet
  setting ``audio.firmwareFlashAuthorised`` holds exactly ``2.0.10`` for this device **and**
  the DFU image exists on the frame. Measured on the mule 2026-08-16: the setting is absent
  from the agent's settings (revision 10, seven keys) and no
  ``respeaker_xvf3800_usb_dfu_firmware*`` file exists anywhere on the root filesystem - the
  installer fetches the six ``host_control`` files and no firmware image - so both locks are
  shut. Neither is a reason to leave the agent running: a hot-plugged 2.0.6 array puts
  ``firmware.xvf3800.version`` into drift, which spends its attempt budget, escalates, and
  under 2.5 rung 4 stops the product on a frame that was showing photographs.

So the procedure is: stop the agent, *then* connect the array, and prove that order held
rather than trusting it. :func:`read` proves it from two clocks the operator does not
control - systemd's ``InactiveEnterTimestampMonotonic`` for the unit, and the kernel's own
printk timestamp for the USB enumeration - and refuses to certify a reading it cannot show
was taken on an array the agent never saw.

Read-only, and structurally so
------------------------------
:data:`READ_COMMANDS` is the complete set of device commands this module can send, and
:func:`_invocation` raises on anything else. ``GPO_WRITE_VALUE`` and ``dfu-util`` are named
nowhere in this file except in prose. The only writes to the frame are ``systemctl stop`` and
``systemctl start`` of ``fl-agent.service``, which are :func:`hold` and :func:`release` and
nothing else.

Why the array cannot simply be added alongside the existing one
---------------------------------------------------------------
``xvf_host`` has no device selector: its ``--help`` lists a protocol (``-u``), a command map
and a range-check bypass, and nothing that names a device. Its USB backend
(``libdevice_usb.so``) imports ``libusb_get_device_list``, ``libusb_get_device_descriptor``
and ``libusb_open`` and nothing that reads a serial number - measured with ``nm -D`` on the
mule 2026-08-16 - so with two arrays attached it talks to whichever one enumeration hands it
first and says nothing about which that was. Two arrays therefore cannot be addressed
individually, and :func:`read` refuses outright when it sees more than one.
"""

from __future__ import annotations

import json
import re
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import ssh, ui
from .config import RUNS_DIR, HarnessError

#: The agent's unit. The only thing on the frame this module starts or stops.
UNIT = "fl-agent.service"

#: The array's USB identity. ``2886:001a`` is the retail Seeed vendor/product pair, the same
#: one ``audio.modprobe.snd-usb-audio-index`` pins the ALSA card index by.
VENDOR = "2886"
PRODUCT = "001a"
VID_PID = f"{VENDOR}:{PRODUCT}"

#: Where a pinned, agent-installed ``xvf_host`` lives, then where guide 4's clone puts one.
#: Same order and same two candidates as ``XvfHost.Roots``, so this measures what the agent
#: would have used rather than some other copy that happens to be on the frame.
TOOL_DIRECTORIES = (
    "/var/lib/fl-agent/xvf3800/host_control/rpi_64bit",
    "$HOME/xvf3800/host_control/rpi_64bit",
)

#: Every device command this module may send. Both are pure reads of the DSP's own state.
#: Anything not in this tuple raises in :func:`_invocation` - the allowlist is the mechanism,
#: not the comment above it.
READ_COMMANDS = ("VERSION", "GPO_READ_VALUES")

#: The five pins ``GPO_READ_VALUES`` answers with, in the firmware's fixed order. Mirrors
#: ``XvfHost.GpoPins``; the parse below is the same shape as ``XvfHost.GpoValues`` for the
#: same reason the parity collector runs the judge's own probes - two parsers of one reply
#: is how a rename stops being noticed.
GPO_PINS = ("X0D11", "X0D30", "X0D31", "X0D33", "X0D39")

#: Index of the amplifier-enable pin in that order, and of the two diagnostics that travel
#: with it: the hardware Mute button and the LED ring rail.
AMPLIFIER_INDEX = 2
MUTE_INDEX = 1
LED_INDEX = 3

#: How many times ``GPO_READ_VALUES`` is taken. One reading cannot show it is stable, and a
#: pin that flickers between reads is a different finding from a pin that sits where it sits.
DEFAULT_REPEATS = 3

#: How long :func:`release` waits for the frame to say it is converged again.
CONVERGE_TIMEOUT_S = 480.0

#: How often it asks.
CONVERGE_POLL_S = 15.0


# --- small helpers ---------------------------------------------------------
def _run_dir() -> Path:
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    path = RUNS_DIR / f"{stamp}-array"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _show(mule: ssh.Mule, *properties: str) -> dict[str, str]:
    """``systemctl show`` for the agent's unit, as a dict. Needs no elevation."""
    flags = " ".join(f"-p {name}" for name in properties)
    result = mule.run(f"systemctl show {UNIT} {flags}", timeout=30)
    values: dict[str, str] = {}
    for line in result.stdout.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            values[key] = value
    return values


def _invocation(directory: str, command: str) -> str:
    """The exact argument vector the agent itself uses, for one allowlisted read command.

    ``XvfHost.RunAsync`` builds ``env -C <dir> LD_LIBRARY_PATH=<dir> <dir>/xvf_host <cmd>``
    because the binary loads its sibling ``.so`` files relative to where it is run from. The
    same vector is used here deliberately: a measurement taken through a different invocation
    is a measurement of a different thing.
    """
    if command not in READ_COMMANDS:
        raise HarnessError(
            f"'{command}' is not one of the read-only device commands this harness can send "
            f"({', '.join(READ_COMMANDS)}).",
            remedy=(
                "This module measures; it does not converge. Writing a GPO pin or flashing "
                "firmware belongs to the agent's own resources, behind their own authorisation."
            ),
        )
    return f"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/xvf_host {command}"


def _tool_directory(mule: ssh.Mule) -> str:
    """The first candidate directory holding an executable ``xvf_host``.

    One ``test -x`` per candidate rather than a shell loop, because ``run_privileged``
    prefixes ``sudo`` to the command text and a shell keyword cannot be sudo'd - ``sudo … for
    d in …; do … done`` is a syntax error whose only symptom is empty output, which reads
    exactly like "the tool is not installed". Measured on the mule 2026-08-16, on the first
    run of this module.
    """
    home = mule.run('printf %s "$HOME"', timeout=30).stdout.strip()
    candidates = [
        directory.replace("$HOME", home) if home else directory
        for directory in TOOL_DIRECTORIES
        if "$HOME" not in directory or home
    ]

    for candidate in candidates:
        # /var/lib/fl-agent is 0700 root, so even the existence test needs elevation.
        if mule.run_privileged(f"test -x {candidate}/xvf_host", timeout=30).ok:
            return candidate

    raise HarnessError(
            "xvf_host is not installed on this frame, so the array cannot be asked anything.",
            exit_code=3,
            remedy=(
                "It is installed by the agent's tool.xvf-host.installed resource into "
                f"{TOOL_DIRECTORIES[0]}. Looked in: {', '.join(TOOL_DIRECTORIES)}."
            ),
        )
    return directory[0]


def parse_version(output: str) -> str | None:
    """The firmware version out of a ``VERSION`` reply, in the tool's spelling (``2 0 10``)."""
    for raw in output.splitlines():
        line = raw.strip()
        if not line.startswith("VERSION"):
            continue
        rest = line[len("VERSION"):].strip()
        if rest:
            return " ".join(rest.split())
    return None


def parse_gpo(output: str) -> list[int] | None:
    """The five pin values out of a ``GPO_READ_VALUES`` reply, or None.

    Takes the *last* line that is five integers, with or without the command name in front,
    which is what ``XvfHost.GpoValues`` does - the banner the tool prints first is not five
    integers and cannot be mistaken for the answer.
    """
    found: list[int] | None = None
    for raw in output.splitlines():
        line = raw.strip()
        if line.startswith("GPO_READ_VALUES"):
            line = line[len("GPO_READ_VALUES"):].strip()
        tokens = line.split()
        if len(tokens) != len(GPO_PINS):
            continue
        try:
            found = [int(token) for token in tokens]
        except ValueError:
            continue
    return found


def ordering(
    connect_us: float | None,
    stopped_us: float,
    started_us: float,
) -> tuple[str, str]:
    """Whether the array can be shown to have been connected *after* the agent stopped.

    All three arguments are microseconds on the boot's monotonic clock: when the array last
    enumerated (from the kernel's printk timestamp), when the unit last entered ``inactive``,
    and when it last entered ``active``. Zero means systemd has no such timestamp for this
    boot.

    Pure and separate from :func:`read` so every branch can be exercised without a frame -
    including the two that matter most and that a bench run cannot produce on demand: an
    array connected after the stop (``proven``) and an agent that never ran at all.
    """
    if started_us == 0:
        return "proven", f"{UNIT} has not run at all since this boot."
    if connect_us is None:
        return "unproven", (
            "The kernel log holds no enumeration line for this array on this boot, so when it "
            "was connected cannot be established."
        )
    if stopped_us == 0:
        return "unproven", f"systemd reports no stop timestamp for {UNIT} on this boot."
    if connect_us > stopped_us:
        return "proven", (
            f"The array enumerated at {connect_us / 1e6:.3f} s after boot; {UNIT} had stopped "
            f"at {stopped_us / 1e6:.3f} s. The agent never saw this array."
        )
    return "violated", (
        f"The array enumerated at {connect_us / 1e6:.3f} s after boot, which is BEFORE {UNIT} "
        f"stopped at {stopped_us / 1e6:.3f} s. The agent was running while this array was "
        "attached, so the pin may already have been written."
    )


def _usb_connect_monotonic_us(mule: ssh.Mule) -> tuple[float | None, str]:
    """When the array last enumerated on this boot, from the kernel's own printk clock.

    The line is ``[    1.039617] usb 1-1: New USB device found, idVendor=2886, ...`` and its
    bracketed timestamp shares systemd's monotonic base, which is what makes the comparison
    against ``InactiveEnterTimestampMonotonic`` meaningful. Returns the last occurrence: an
    array plugged in twice was last plugged in at the second one.
    """
    result = mule.run_privileged(
        f"dmesg | grep -a 'idVendor={VENDOR}, idProduct={PRODUCT}'", timeout=60
    )
    text = result.stdout.strip()
    stamp: float | None = None
    for line in text.splitlines():
        match = re.match(r"^\[\s*(\d+\.\d+)\]", line.strip())
        if match:
            stamp = float(match.group(1)) * 1_000_000
    return stamp, text


def _usb_identity(mule: ssh.Mule) -> tuple[list[dict[str, str]], str]:
    """Every attached array, straight out of sysfs, with the fields that identify a unit.

    ``bcdDevice`` is worth the extra field: the XVF3800 encodes its firmware version there
    (``2.0a`` is 2.0.10), so it is a second, unprivileged reading of the number ``VERSION``
    answers with - and one that does not need the control tool at all.
    """
    script = (
        "for d in /sys/bus/usb/devices/*/; do "
        f'[ "$(cat $d/idVendor 2>/dev/null)" = "{VENDOR}" ] || continue; '
        f'[ "$(cat $d/idProduct 2>/dev/null)" = "{PRODUCT}" ] || continue; '
        'echo "path=$(basename $d) '
        'bcdDevice=$(cat $d/bcdDevice 2>/dev/null) '
        'serial=$(cat $d/serial 2>/dev/null) '
        'manufacturer=$(cat $d/manufacturer 2>/dev/null) '
        'product=$(cat $d/product 2>/dev/null)"; '
        "done"
    )
    result = mule.run(script, timeout=30)
    devices: list[dict[str, str]] = []
    for line in result.stdout.strip().splitlines():
        fields: dict[str, str] = {}
        for token in line.split():
            key, _, value = token.partition("=")
            fields[key] = value
        if fields:
            devices.append(fields)
    return devices, result.stdout.strip()


# --- the three operator commands -------------------------------------------
def hold() -> dict[str, Any]:
    """Stop the agent and prove it is stopped, so an array may be connected safely.

    Idempotent: stopping an already-stopped unit changes nothing, and the timestamps printed
    are then the earlier stop's, which is still the number :func:`read` needs.
    """
    with ssh.connect() as mule:
        ui.step(f"Stopping {UNIT} on {mule.host}")
        mule.run_privileged(f"systemctl stop {UNIT}", timeout=120).check(f"systemctl stop {UNIT}")

        state = _show(
            mule,
            "ActiveState",
            "SubState",
            "InactiveEnterTimestamp",
            "InactiveEnterTimestampMonotonic",
        )
        active = state.get("ActiveState", "?")
        if active not in ("inactive", "failed"):
            raise HarnessError(
                f"{UNIT} is still {active} after being asked to stop.",
                exit_code=5,
                remedy="Do not connect an array. Investigate with `systemctl status fl-agent`.",
            )

        stray = mule.run("pgrep -x xvf_host || true", timeout=30).stdout.strip()
        devices, _ = _usb_identity(mule)

        ui.ok(f"{UNIT} is {active} as of {state.get('InactiveEnterTimestamp', '?')}")
        ui.kv(
            {
                "arrays attached now": str(len(devices)),
                "xvf_host processes": stray or "none",
                "stopped at (monotonic us)": state.get("InactiveEnterTimestampMonotonic", "?"),
            }
        )
        if stray:
            ui.warn("An xvf_host process is still running. Wait for it to exit before connecting.")

        ui.info("")
        ui.info("SAFE TO CONNECT. In this order:")
        ui.info(f"  1. unplug the array that is attached now ({len(devices)} attached)")
        ui.info("  2. plug in the array to be measured")
        ui.info("  3. run:  python tools/harness/fl.py array read")
        ui.info("")
        ui.info("The frame's photographs stop while the agent is stopped; `array release` brings")
        ui.info("them back. Nothing restarts the agent on its own - Restart=always does not")
        ui.info("apply to a unit an operator stopped.")

        return {
            "activeState": active,
            "inactiveEnterTimestamp": state.get("InactiveEnterTimestamp", ""),
            "inactiveEnterTimestampMonotonic": state.get("InactiveEnterTimestampMonotonic", ""),
            "arraysAttached": len(devices),
            "xvfHostProcesses": stray,
        }


def read(*, repeats: int = DEFAULT_REPEATS) -> dict[str, Any]:
    """Read the amplifier pin, and everything needed to know what the reading means.

    Refuses rather than guesses in three cases, because a measurement that cannot be trusted
    is worse than none: the agent is running, no array is attached, or more than one is.
    """
    if repeats < 1:
        raise HarnessError("repeats must be at least 1.")

    raw: list[tuple[str, str]] = []

    def capture(label: str, text: str) -> None:
        raw.append((label, text))

    with ssh.connect() as mule:
        ui.step(f"Measuring the array on {mule.host}")

        # 1. The agent must be stopped. This is the check that makes the rest meaningful.
        state = _show(
            mule,
            "ActiveState",
            "SubState",
            "InactiveEnterTimestamp",
            "InactiveEnterTimestampMonotonic",
            "ActiveEnterTimestampMonotonic",
        )
        active = state.get("ActiveState", "?")
        if active not in ("inactive", "failed"):
            raise HarnessError(
                f"{UNIT} is {active}. The agent asserts the amplifier pin, so a reading taken "
                "while it runs cannot be a factory default.",
                exit_code=3,
                remedy="Run `python tools/harness/fl.py array hold` first, then connect the array.",
            )
        capture("systemctl show fl-agent.service", "\n".join(f"{k}={v}" for k, v in state.items()))

        # 2. Exactly one array, because xvf_host cannot be told which one to talk to.
        devices, devices_text = _usb_identity(mule)
        capture("attached arrays (sysfs)", devices_text or "(none)")
        lsusb = mule.run(f"lsusb -d {VID_PID}", timeout=30).stdout.strip()
        capture(f"lsusb -d {VID_PID}", lsusb or "(none)")

        if not devices:
            raise HarnessError(
                f"No {VID_PID} array is attached to {mule.host}.",
                exit_code=3,
                remedy="Connect the array to be measured, then run this command again.",
            )
        if len(devices) > 1:
            raise HarnessError(
                f"{len(devices)} arrays are attached. xvf_host has no device selector, so it "
                "would talk to whichever one enumerated first and could not say which.",
                exit_code=3,
                remedy="Unplug every array but the one to be measured, then run this again.",
            )

        # 3. Was this array ever exposed to a running agent on this boot?
        connect_us, dmesg_text = _usb_connect_monotonic_us(mule)
        capture("dmesg (array enumeration)", dmesg_text or "(no matching line)")

        order, order_why = ordering(
            connect_us,
            float(state.get("InactiveEnterTimestampMonotonic") or 0),
            float(state.get("ActiveEnterTimestampMonotonic") or 0),
        )

        # 4. The measurement itself. VERSION first: the reading is only interpretable next to
        #    the firmware it was taken on, and that is the whole point of the question.
        directory = _tool_directory(mule)
        ui.info(f"xvf_host: {directory}")

        version_result = mule.run_privileged(_invocation(directory, "VERSION"), timeout=60)
        version_text = version_result.stdout + version_result.stderr
        capture("xvf_host VERSION", version_text.strip())
        version = parse_version(version_text)

        readings: list[list[int] | None] = []
        for index in range(repeats):
            result = mule.run_privileged(_invocation(directory, "GPO_READ_VALUES"), timeout=60)
            text = result.stdout + result.stderr
            capture(f"xvf_host GPO_READ_VALUES ({index + 1} of {repeats})", text.strip())
            readings.append(parse_gpo(text))

        cards = mule.run("cat /proc/asound/cards", timeout=30).stdout.strip()
        capture("/proc/asound/cards", cards)

    # --- interpretation, on the workstation ---------------------------------
    parsed = [values for values in readings if values is not None]
    stable = bool(parsed) and len(parsed) == len(readings) and all(v == parsed[0] for v in parsed)
    values = parsed[0] if parsed else None

    if values is None:
        verdict = "the array did not report its pins"
        amplifier = None
    else:
        amplifier = values[AMPLIFIER_INDEX]
        # "at boot" only where the connect order was proved. The verdict string is the line
        # most likely to be quoted somewhere far away from the run that produced it, so it
        # must not carry a claim the run did not establish.
        when = "at boot" if order == "proven" else "as read now, not proved to be the boot value"
        state_word = "ON" if amplifier == 0 else "OFF"
        verdict = f"AMPLIFIER {state_word} {when} (X0D31={amplifier}, active-low)"

    reading: dict[str, Any] = {
        "takenUtc": datetime.now(UTC).isoformat(),
        "firmware": version,
        "usb": devices[0],
        "gpoPins": list(GPO_PINS),
        "gpoReadings": readings,
        "stable": stable,
        "amplifierPin": amplifier,
        "muteButton": values[MUTE_INDEX] if values else None,
        "ledRing": values[LED_INDEX] if values else None,
        "ordering": order,
        "orderingWhy": order_why,
        "verdict": verdict,
        "agentActiveState": active,
        "toolDirectory": directory,
    }

    directory_out = _run_dir()
    (directory_out / "reading.json").write_text(json.dumps(reading, indent=2) + "\n", encoding="utf-8")
    (directory_out / "raw.txt").write_text(
        "\n".join(f"--- {label} ---\n{text}\n" for label, text in raw), encoding="utf-8"
    )

    for label, text in raw:
        ui.block(label, text)

    ui.kv(
        {
            "firmware (VERSION)": version or "not reported",
            "firmware (bcdDevice)": devices[0].get("bcdDevice", "?"),
            "serial": devices[0].get("serial", "(none)"),
            "X0D31 amplifier": "?" if amplifier is None else str(amplifier),
            "X0D30 mute button": "?" if not values else str(values[MUTE_INDEX]),
            "X0D33 LED ring": "?" if not values else str(values[LED_INDEX]),
            "readings stable": "yes" if stable else "NO",
            "connect-order proof": order,
            "artifact": str(directory_out),
        }
    )
    ui.info(order_why)

    if order == "proven" and stable and amplifier is not None:
        ui.ok(f"{verdict} on firmware {version or 'unknown'}")
    elif amplifier is not None:
        ui.warn(f"{verdict} on firmware {version or 'unknown'} - NOT a certified factory reading")
    else:
        ui.fail(verdict)

    ui.info("")
    ui.info("Next: unplug this array, plug the frame's own array back in, then")
    ui.info("  python tools/harness/fl.py array read      (same reading, order now proved)")
    ui.info("  python tools/harness/fl.py array release   (agent back, photographs back)")

    reading["certified"] = order == "proven" and stable and amplifier is not None
    return reading


def release(*, timeout_s: float = CONVERGE_TIMEOUT_S) -> dict[str, Any]:
    """Start the agent again and wait until the frame says it is converged.

    The evidence is the agent's own self-report line, which it logs when it tells its Fleet
    Manager what it is: ``This frame now reports itself as InSync(...)``. ``InSync`` is
    ``AgentHealth.ReportFor`` of loop state ``Converged``, and a pass is only ``Converged``
    when every resource in the graph observed in sync - so the line is the census, not a
    summary of it. It is logged on change, and a fresh process has nothing recorded yet, so a
    start always produces one.
    """
    with ssh.connect() as mule:
        ui.step(f"Starting {UNIT} on {mule.host}")
        mule.run_privileged(f"systemctl start {UNIT}", timeout=120).check(f"systemctl start {UNIT}")

        invocation = _show(mule, "InvocationID").get("InvocationID", "")
        if not invocation:
            raise HarnessError(
                f"{UNIT} started but systemd reports no InvocationID for it.", exit_code=5
            )

        deadline = time.monotonic() + timeout_s
        seen = ""
        report = ""
        while time.monotonic() < deadline:
            result = mule.run_privileged(
                f"journalctl _SYSTEMD_INVOCATION_ID={invocation} -o cat --no-pager "
                "| grep -a 'reports itself as' | tail -1",
                timeout=90,
            )
            report = result.stdout.strip()
            if report and report != seen:
                seen = report
                ui.info(report)
            if "InSync(" in report:
                break
            time.sleep(CONVERGE_POLL_S)

        state = _show(mule, "ActiveState", "SubState", "ActiveEnterTimestamp")
        panel = mule.run_privileged(
            f"journalctl _SYSTEMD_INVOCATION_ID={invocation} -o cat --no-pager "
            "| grep -a -E \"browser is now the frame's screen|The screen is now the product\" | tail -2",
            timeout=90,
        ).stdout.strip()
        processes = mule.run(
            "pgrep -a -f 'chromium|kiosk' | head -4 || true", timeout=30
        ).stdout.strip()

        converged = "InSync(" in report

    ui.kv(
        {
            "ActiveState": state.get("ActiveState", "?"),
            "started": state.get("ActiveEnterTimestamp", "?"),
            "self-report": report or "(none logged yet)",
        }
    )
    if panel:
        ui.block("panel", panel)
    if processes:
        ui.block("kiosk processes", processes)

    if converged:
        ui.ok("The frame reports itself InSync - every resource in the graph is in sync.")
    else:
        ui.warn(
            f"The frame has not reported InSync within {timeout_s:.0f} s. It may still be "
            "converging; re-run this command or read the journal."
        )

    return {
        "activeState": state.get("ActiveState", ""),
        "selfReport": report,
        "converged": converged,
        "panel": panel,
        "processes": processes,
    }
