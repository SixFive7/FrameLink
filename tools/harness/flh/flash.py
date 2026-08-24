"""The attended bench flash of an XVF3800 microphone array, and every refusal in front of it.

What this is, and why it is not in :mod:`flh.xvf`
--------------------------------------------------
:mod:`flh.xvf` owns ``fl.py array hold | read | release`` and is **structurally read-only**:
its module docstring asserts that ``GPO_WRITE_VALUE`` and ``dfu-util`` are named nowhere in
that file except in prose, and its :func:`~flh.xvf._invocation` enforces three independent
gates so that no single edit can turn a getter into a setter. That property is load-bearing
and it is worth more than the convenience of one file, so the write lives here instead and
``xvf.py`` keeps its guarantee intact. What this module reuses from it is the part that
matters: :func:`flh.xvf.hold` stops the agent and *proves* it stopped, and a stopped agent
cannot self-update, cannot reboot the frame, and cannot race this process for the array's
HID interface.

Why an attended bench path exists at all when the agent can flash
-----------------------------------------------------------------
Decision 91 gives the agent a single-use, digest-named, interlocked flash beside the
reconcile loop, and lists the interlocks it added. One of them it explicitly does **not**
build:

    *A power-cycle guard — impossible at the device; the agent writes a durable marker on
    the card for the duration of the write, and the harness-side refusal that reads it is
    the one interlock that lives on the workstation and is not yet built.*

That refusal is the ``flash.marker`` gate in :func:`evaluate`, and it is the reason this
module exists at all.
Everything else here is the second reason: the first flash of any array, on a bench, with a
person present, should not be performed by a loop on a one-minute timer. It should be
performed deliberately, by somebody who has rehearsed the way back.

**One marker, two writers, one meaning.** This module writes the *agent's* marker file —
``/var/lib/fl-agent/array-flash.inprogress`` — in the agent's own format, for the duration
of its own write. That is deliberate and it is the single most important design decision in
this file. The marker means *a DFU write to this frame's array began and nothing knows how
far it got*, and that sentence is exactly as true when the workstation started the write as
when the agent did. A harness-private marker would leave the agent free to begin a second
write onto an Upgrade partition nothing knows the state of. So an interrupted bench flash
fuses the agent too, permanently, until a person removes the file — and
``fl.py array flash --clear-marker`` is that person's tool, not this module's own escape
hatch.

**A correction, kept because it is the reason the marker survives a policy change.** This
paragraph used to justify the marker by saying that retrying a partial write "is the route
from a recoverable board to an unrecoverable one", citing upstream issues #8 and #32. That
documentation was searched for on 2026-08-24 and does not exist; XMOS documents the opposite -
*"Another download operation may be reattempted."* The agent now writes up to three times per
authorisation for exactly that reason. What the marker rests on is the narrower and true
statement: an interrupted write leaves a partition whose state nothing on the frame can
measure, and the answer to a state you cannot measure is a person rather than another
attempt. Retrying a *completed* write that did not stick is a different thing entirely, and
that is the one the agent does.

The five gates in front of a write, and why they are five
----------------------------------------------------------
The brief for this module was an allowlist *that cannot be widened into a write by editing
one list*. A single list is one edit away from a mistake, so the write is behind five
independent conditions, no two of which live in the same place:

1. **Intent** — ``--write`` must be present. Without it no writing program is resolved at
   all; the command is a pre-flight that reports what a write *would* do.
2. **Authorisation naming a digest** — ``--authorise <sha256>[:ticket]``, compared against
   the pinned target's SHA-256. A version number authorises nothing in particular, because
   upstream has published one version string twice with different bytes; only a digest is a
   name. This is the same shape and the same string the agent's ``audio.arrayFirmwareFlash``
   setting carries, so an operator learns one format.
3. **The pin, held twice** — :data:`TARGET` here and ``XvfFirmwarePin.Current`` in
   ``src/FrameLink.Agent/Firmware/XvfFirmwareRelease.cs``, cross-checked at run time by
   :func:`cross_check_pin`. Editing either one alone makes this command refuse rather than
   flash something new. Two records of one fact is how a bump stops being silent.
4. **The bytes, re-hashed on the frame in the instant before the write** — not the name,
   not "the resource installed it", not a digest this process measured a minute ago. The
   same discipline ``XvfFirmwareInstaller.VerifyAsync`` is called twice for.
5. **The partition** — :func:`arguments` refuses any alt setting but ``1``, by number, and
   names ``0`` and ``2`` in its refusal. Alt 0 is the Factory partition Safe Mode boots
   from; it is the way back, and nothing in this project may write it. That gate does not
   consult the image allowlist, so adding an image to :data:`IMAGES` cannot reach it.

And a sixth that is not about the write but about the room it happens in: the agent must be
**held**, proven by systemd's own timestamps, exactly as :func:`flh.xvf.read` proves it.

What this module will not do, ever
-----------------------------------
* **It will not write the Factory partition** (alt 0) or the DataPartition (alt 2).
* **It will not write any image but the pinned Target.** There is only one pinned image now;
  see the note on the recovery kit below.
* **It will not enter Safe Mode**, because nothing in software can: the gesture is sampled
  by the bootloader at power-on from a physical button. Nor does it print the procedure any
  more - see the note on Safe Mode support below.

The recovery kit is gone, and this is the account of it
--------------------------------------------------------
Until 2026-08-24 this module pinned three images: the target, a v2.0.6 ``FALLBACK`` and
Seeed's 4 MiB all-``0xFF`` ``RECOVERY`` erase image, and a ``recovery_runbook`` function
printed a six-step procedure built around them. All of that went with the operator's decision
to embed one target firmware and nothing else.

What was measured, against XMOS's and Seeed's own sources and recorded in
``reference/xvf3800-recovery-model.md``:

* A DFU download **already erases the whole upgrade section before it writes** - XMOS's
  ``lib_dfu``: *"on receiving the first DFU_DNLOAD command, the device starts to erase
  FLASH_MAX_UPGRADE_SIZE bytes of the upgrade section"* - so a separate erase step has nothing
  to do.
* **Seeed's own documented recovery has no erase step**: enter Safe Mode, flash the firmware.
  The string ``all_ff`` appears nowhere in the wiki, the DFU guide or the changelog; its
  entire documentation is one GitHub issue comment.
* The failure the erase image was published for is a configuration corrupted by
  ``SAVE_CONFIGURATION``, which upstream says was **fixed in firmware from v2.0.9**, and which
  this repository cannot cause because it sends that command nowhere.
* ``v2.0.6`` as "the fallback" was one commit's unexplained choice. Neither upstream, nor
  Seeed, nor decision 91 ever recommended it, and the maintainer's own recovery advice tracks
  the newest image rather than an old one.

What that costs, stated rather than glossed: a second known-good image on the card was
insurance against the pinned target being bad on a board nobody has met - real, unquantified,
and never observed here or upstream - and putting one back is now a pin bump and a release.
The erase image was the only answer to a corrupted DataPartition, a failure this product
cannot reach.

Two procedural details went with the erase and are recorded here so nobody re-derives them
while reading an old transcript: the ``all_ff`` write terminated at about 96% (4,030,464 of
4,194,304 bytes) with ``dfuERROR status(8) = ... address that is out of range``, which was the
expected outcome because the image is larger than the partition; and a power cycle was
required between that erase and the next write, or the download failed at 0% with the same
status. Neither applies to writing a firmware image, which is the only thing left.

Safe Mode support went too, and that is a larger removal than the kit
----------------------------------------------------------------------
On the same day the operator dropped this project's *support* for Safe Mode entirely: no
runbook here, no ``fl.py array runbook``, no on-screen recovery instructions on a frame, and
no wedged-board detection. A board that has stopped presenting itself over USB goes back to
the maintainer.

Safe Mode itself cannot be dropped and nothing here pretends otherwise: it is firmware in the
board's Factory partition, put there at manufacture, which no DFU write can touch, and it is
entered by a physical gesture the bootloader samples at power-on. **It is also the only route
back from a board that has stopped enumerating** - precisely the state in which this module's
normal path is useless, because ``dfu-util -e`` needs a working device to detach. After this
change the software has no recovery path for that state at all. That was said before the
decision was taken; it is recorded here for whoever reads it afterwards.

**The knowledge was not deleted.** ``reference/xvf3800-recovery-model.md`` is the measured
record of what Safe Mode is, what the Factory partition guarantees, what the erase image
actually did and what every recovery route costs. It stays exactly as it is. What went is the
product's support for the procedure, not the finding behind it.

Simulation
----------
``--simulate <outcome>`` exercises the whole system around a flash without a byte reaching
the array. It does that by substituting the *program* — a stub this module writes itself,
whose contents it verifies by digest immediately before running — and by substituting
nothing else: the marker is the real marker at the real path, the gates are the real gates,
the artifact is the real artifact, the re-enumeration poll really reads sysfs on the real
frame, and the refusals are produced by the same code that produces them for a real run.
``--simulate`` and ``--write`` are mutually exclusive at the parser and again in
:func:`flash`, and the real ``dfu-util`` path is resolved only on the second.

Honesty about that: a simulated run proves the *system* is right. It cannot prove the
*device* behaves, and this module never claims otherwise — a simulated run's artifact
records ``"simulated": true`` and every console line says so.
"""

from __future__ import annotations

import base64
import hashlib
import json
import re
import shlex
import time
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import debuglog, ssh, ui, xvf
from .config import RUNS_DIR, HarnessError

# --- the pin, which this file holds as its own second record ----------------
#: Where the agent keeps the same three digests. Parsed by :func:`cross_check_pin` so a bump
#: on one side and not the other is a refusal rather than a surprise.
AGENT_PIN_SOURCE = "src/FrameLink.Agent/Firmware/XvfFirmwarePin.cs"


@dataclass(frozen=True)
class Image:
    """One pinned DFU image. Mirrors the agent's ``XvfFirmwareImage`` field for field."""

    name: str
    directory: str
    commit: str
    sha256: str
    size_bytes: int
    role: str
    version: str
    purpose: str

    @property
    def path_in_repository(self) -> str:
        return f"xmos_firmwares/{self.directory}/{self.name}"

    @property
    def local_path(self) -> str:
        return f"{self.directory}/{self.name}"

    def url(self, owner: str = "respeaker", repository: str = "reSpeaker_XVF3800_USB_4MIC_ARRAY") -> str:
        return (
            f"https://raw.githubusercontent.com/{owner}/{repository}/"
            f"{self.commit}/{self.path_in_repository}"
        )


#: The version the fleet converges on. **The only image this module may ever write.**
TARGET = Image(
    name="respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin",
    directory="usb",
    commit="183ef1ca6befd592da6c4c504259335f8bb3d097",
    sha256="60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6",
    size_bytes=933_888,
    role="Target",
    version="2 1 0",
    purpose="the firmware version this fleet converges on",
)

#: Every image this project pins. A one-tuple since the recovery kit went; it stays a tuple
#: because :func:`cross_check_pin` walks it against the agent's own list and a second image
#: joining the pin should be a data edit on both sides rather than a reshape.
IMAGES = (TARGET,)

#: The agent's own marker, which this module both reads and writes. See the module
#: docstring: one marker, two writers, one meaning.
MARKER_PATH = "/var/lib/fl-agent/array-flash.inprogress"

#: Where the agent records a spent authorisation.
CONSUMED_PATH = "/var/lib/fl-agent/array-flash.consumed"

#: The fleet setting the agent takes an authorisation from. Named here so an operator can be
#: told the one string that means the same thing on both sides.
AUTHORISATION_KEY = "audio.arrayFirmwareFlash"

#: Where the agent's ``firmware.xvf3800.image`` resource puts the pinned images, and where
#: this module looks first - so a bench flash writes the same bytes the fleet converges on.
AGENT_IMAGE_DIRECTORY = "/var/lib/fl-agent/xvf3800/xmos_firmwares"

#: Where ``--stage`` puts them when the agent has not. Deliberately in the login user's home
#: and never under ``/var/lib/fl-agent``: an image a person staged by hand must never be
#: mistakable for one the agent fetched and verified.
BENCH_IMAGE_DIRECTORY = "$HOME/fl-flash/xmos_firmwares"

#: Where the simulation stub is written. Same reasoning, and swept by ``--clean``.
BENCH_DIRECTORY = "$HOME/fl-flash"

#: The one program that performs a real write, at the one path ``pkg.dfu-util`` installs it.
DFU_UTIL = "/usr/bin/dfu-util"

#: The only alt setting anything here may write. 0 is the Factory partition Safe Mode boots
#: from - the way back - and 2 is the DataPartition. Neither is writable from this file.
UPGRADE_ALT = 1

#: Alt settings named in the refusal, so the error says what it is protecting rather than
#: only what it rejected.
FORBIDDEN_ALT = {
    0: "the Factory partition, which is what Safe Mode boots from and is the only way back "
       "from a bad write",
    2: "the DataPartition, which holds the saved configuration and which upstream issues #8 "
       "and #32 both report rejecting all access",
}

#: How long the array is given to come back reporting the target. Matches the agent's
#: ``ArrayFirmwareFlash.ReEnumerationTimeout`` exactly, because a bench run that waited a
#: different length would not be a rehearsal of the automated path.
REENUMERATION_TIMEOUT_S = 90.0

#: How often the bus is re-read while waiting. Matches ``ReEnumerationPoll``.
REENUMERATION_POLL_S = 2.0

#: A frame that rebooted this recently is still settling, and decision 91's own list calls a
#: pre-flight refusal on unstable conditions the honest version of a power-cycle guard.
SETTLED_MINIMUM_UPTIME_S = 300.0

#: How long a simulated write runs before ``--simulate interrupted`` kills it. Long enough
#: that the marker is provably on the card while it runs.
SIMULATED_WRITE_S = 8.0


# --- outcomes ---------------------------------------------------------------
#: Every simulated outcome, and honestly what each one substitutes. The value is the
#: sentence printed beside the result, so nobody reads a simulation as a measurement.
SIMULATIONS: dict[str, str] = {
    "success": (
        "the stub exits 0 with upstream's own success transcript, and the re-enumeration "
        "check is told to expect the version the array already reports - so this is the one "
        "outcome where the verify itself is substituted, not just the writer"
    ),
    "dfu-error": (
        "the stub exits non-zero with a dfuERROR status(8) transcript; nothing else is "
        "substituted"
    ),
    "erase-out-of-range": (
        "the stub reproduces a write terminating at 96% with status(8) out of range. Kept "
        "after the recovery kit went, because it is still the most confusing transcript this "
        "device produces and the one a person is most likely to misread"
    ),
    "interrupted": (
        "the stub is started and then killed mid-write, exactly as a cgroup teardown or a "
        "power cut would kill dfu-util; the marker is real and is deliberately left behind"
    ),
    "no-reenumerate": (
        "the stub exits 0 and nothing else is substituted, so the re-enumeration poll runs "
        "against the real array and really does not find the target version"
    ),
    "tool-missing": "the tool gate is pointed at a path that genuinely does not exist",
    "no-array": "the array enumeration is filtered to nothing, as an unplugged unit would be",
    "two-arrays": "the array enumeration is duplicated, as a second attached unit would be",
    "image-corrupt": "the image gate is pointed at a real file whose bytes are genuinely wrong",
}


@dataclass
class Gate:
    """One interlock: what it wanted, what it saw, and which way it went."""

    name: str
    passed: bool
    expected: str = ""
    observed: str = ""
    why: str = ""
    #: The agent's own refusal name for the same condition, where there is one. Recorded so
    #: a bench refusal and a fleet refusal can be recognised as the same event.
    refusal: str = ""

    def as_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "passed": self.passed,
            "expected": self.expected,
            "observed": self.observed,
            "why": self.why,
            "refusal": self.refusal,
        }


@dataclass
class Preflight:
    """Every gate, in the order they were evaluated."""

    gates: list[Gate] = field(default_factory=list)

    def add(self, gate: Gate) -> Gate:
        self.gates.append(gate)
        log = debuglog.current()
        if log is not None:
            log.gate(gate.name, gate.passed, expected=gate.expected, observed=gate.observed, why=gate.why)
        return gate

    @property
    def refusals(self) -> list[Gate]:
        return [gate for gate in self.gates if not gate.passed]

    @property
    def permitted(self) -> bool:
        return not self.refusals

    def as_dict(self) -> list[dict[str, Any]]:
        return [gate.as_dict() for gate in self.gates]


# --- gate 5: the argument vector --------------------------------------------
def arguments(operation: str, *, image_path: str | None = None, alt: int = UPGRADE_ALT) -> list[str]:
    """The exact ``dfu-util`` argument vector for one operation, or a refusal.

    Three gates, in the order a wrong call trips them, all unconditional and none of them
    consulting the others - the same shape and the same reasoning as
    :func:`flh.xvf._invocation`, because the failure being prevented is the same one: a
    single edit turning a read into a write.

    ``list`` is ``dfu-util -l``, which enumerates alt settings and **writes nothing**. It is
    the operation that proves Safe Mode was entered, because Safe Mode lists a third alt
    setting that run-time mode does not.

    ``download`` is the write. It is upstream's own documented flow unchanged, and is the
    same vector ``ArrayFirmwareFlash.Arguments`` builds, deliberately: ``-e`` detaches the
    device out of run-time mode into DFU mode, ``-a 1`` targets the **Upgrade** partition,
    ``-D`` downloads, and ``-R`` resets afterwards so the array comes back as an audio
    device. A bench run that used a different vector would not be a rehearsal of the path
    the fleet will take.
    """
    if operation not in ("list", "download"):
        raise HarnessError(
            f"'{operation}' is not a DFU operation this harness performs.",
            remedy=(
                "Only 'list' (dfu-util -l, which writes nothing) and 'download' (the flash) "
                "exist here. Upload, detach and abort are deliberately absent: an operation "
                "this file cannot name is an operation it cannot be talked into."
            ),
        )

    if operation == "list":
        return ["-l"]

    # Gate: the partition. Checked by number and independently of everything else, so no
    # edit to the image list can reach it and no image can carry an alt setting with it.
    if alt != UPGRADE_ALT:
        protecting = FORBIDDEN_ALT.get(alt, "a partition this project does not write")
        raise HarnessError(
            f"alt {alt} is {protecting}.",
            remedy=(
                f"Only alt {UPGRADE_ALT}, the Upgrade partition, is ever written from this "
                "harness. An interrupted write to the Upgrade partition leaves Safe Mode "
                "reachable because Safe Mode's code lives in the Factory partition; that is "
                "the whole reason this operation is recoverable at all, and writing alt 0 "
                "would spend it."
            ),
        )

    if not image_path:
        raise HarnessError("A download needs an image path.")

    return ["-R", "-e", "-a", str(UPGRADE_ALT), "-D", image_path]


# --- gate 3: the pin, held twice --------------------------------------------
def cross_check_pin(repo_root: Path) -> dict[str, Any]:
    """Compare this file's pin against the agent's, and refuse on any disagreement.

    Two independent records of one fact. The agent's is the one the fleet converges on and
    the one the ledger's ``github-path-commit`` probe watches; this file's is what a bench
    flash writes. Editing either alone must not be enough to make this command write
    different bytes, which is why a mismatch is a refusal and not a warning.

    Parsed with a regular expression rather than by building the agent, because the harness
    must work on a workstation with no .NET SDK and because a parse that fails is reported
    as a parse that failed rather than silently passing.
    """
    source = repo_root / AGENT_PIN_SOURCE
    if not source.exists():
        raise HarnessError(
            f"{AGENT_PIN_SOURCE} is not in this checkout, so the pin cannot be cross-checked.",
            exit_code=3,
            remedy=(
                "This command deliberately refuses to write firmware it can only find one "
                "record of. Run it from a full checkout."
            ),
        )

    text = source.read_text(encoding="utf-8")
    found: dict[str, dict[str, str]] = {}
    pattern = re.compile(
        r'new XvfFirmwareImage\(\s*'
        r'"(?P<name>[^"]+)",\s*'
        r'"(?P<directory>[^"]+)",\s*'
        r'"(?P<commit>[0-9a-fA-F]+)",\s*'
        r'"(?P<sha>[0-9a-fA-F]{64})",\s*'
        r'(?P<size>[0-9_]+),\s*'
        r'XvfFirmwareRole\.(?P<role>\w+)',
        re.MULTILINE,
    )
    for match in pattern.finditer(text):
        found[match.group("role")] = {
            "name": match.group("name"),
            "directory": match.group("directory"),
            "commit": match.group("commit"),
            "sha256": match.group("sha"),
            "size": match.group("size").replace("_", ""),
        }

    disagreements: list[str] = []
    for image in IMAGES:
        theirs = found.get(image.role)
        if theirs is None:
            disagreements.append(f"{image.role}: the agent's pin has no image with that role")
            continue
        for label, mine, other in (
            ("name", image.name, theirs["name"]),
            ("directory", image.directory, theirs["directory"]),
            ("commit", image.commit, theirs["commit"]),
            ("sha256", image.sha256.lower(), theirs["sha256"].lower()),
            ("size", str(image.size_bytes), theirs["size"]),
        ):
            if mine != other:
                disagreements.append(
                    f"{image.role} {label}: this harness has {mine}, {AGENT_PIN_SOURCE} has {other}"
                )

    if disagreements:
        raise HarnessError(
            "The harness's pin and the agent's pin disagree, so nothing will be written.",
            exit_code=3,
            remedy=(
                "Two independent records of one fact is the point; a bump has to move both.\n  "
                + "\n  ".join(disagreements)
            ),
        )

    return {"source": AGENT_PIN_SOURCE, "images": len(found), "agreed": True}


# --- frame-side reads --------------------------------------------------------
def _home(mule: ssh.Mule) -> str:
    return mule.run('printf %s "$HOME"', timeout=30).stdout.strip() or "/home/framelink"


def _expand(path: str, home: str) -> str:
    return path.replace("$HOME", home)


def _b64(text: str) -> str:
    """Base64 of a UTF-8 string, for putting exact bytes on the frame through one command.

    ``base64`` rather than ``xxd``: measured on Frame #1 2026-08-24, ``xxd`` is **not
    installed** on Raspberry Pi OS Lite (it ships with vim, which a Lite image does not
    carry), while ``base64`` is coreutils and is always there. The point of encoding at all
    is that the marker and the spent-authorisation record must be written byte for byte
    through a shell that would otherwise interpret, split or newline-mangle them.
    """
    return base64.b64encode(text.encode("utf-8")).decode("ascii")


def read_marker(mule: ssh.Mule) -> str | None:
    """The agent's flash-in-progress marker, or None.

    ``/var/lib/fl-agent`` is 0700 root, so even the existence test needs elevation.
    """
    result = mule.run_privileged(f"cat {MARKER_PATH} 2>/dev/null || true", timeout=30)
    text = result.stdout.strip()
    return text or None


def read_consumed(mule: ssh.Mule) -> str | None:
    """The authorisation the agent has already spent, or None."""
    result = mule.run_privileged(f"cat {CONSUMED_PATH} 2>/dev/null || true", timeout=30)
    text = result.stdout.strip()
    return text or None


def digest_on_frame(mule: ssh.Mule, path: str) -> tuple[str | None, int | None]:
    """SHA-256 and byte length of a file on the frame, measured by the frame.

    Measured there rather than fetched here: the bytes that matter are the ones
    ``dfu-util`` will open, and a digest taken over a copy is a digest of the copy.
    """
    result = mule.run_privileged(
        f"sha256sum {shlex.quote(path)} 2>/dev/null; stat -c %s {shlex.quote(path)} 2>/dev/null; true",
        timeout=120,
    )
    digest: str | None = None
    size: int | None = None
    for line in result.stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        if len(line.split()) == 2 and len(line.split()[0]) == 64:
            digest = line.split()[0]
        elif line.isdigit():
            size = int(line)
    return digest, size


def locate_image(mule: ssh.Mule, image: Image, home: str) -> tuple[str | None, str | None, int | None]:
    """Where this image is on the frame, and what it actually hashes to there.

    Two candidate directories in a fixed order, the same shape as
    :data:`flh.xvf.TOOL_DIRECTORIES`: the agent's own, which is what the fleet converges on
    and therefore the authoritative one, and then the bench staging directory ``--stage``
    fills when the agent has not. Returns the first path that **exists**, with its measured
    digest - existing and being wrong is a different finding from being absent, and this
    must not silently fall through from a corrupt agent image to a good bench one.
    """
    for directory in (AGENT_IMAGE_DIRECTORY, _expand(BENCH_IMAGE_DIRECTORY, home)):
        path = f"{directory}/{image.local_path}"
        digest, size = digest_on_frame(mule, path)
        if digest is not None:
            return path, digest, size
    return None, None, None


def attached_arrays(mule: ssh.Mule) -> list[dict[str, str]]:
    """Every XVF3800 on the bus, from sysfs. Same reading the agent's own poll takes."""
    devices, _ = xvf._usb_identity(mule)  # noqa: SLF001 - one reading, deliberately shared
    return devices


def descriptor_version(devices: list[dict[str, str]]) -> str | None:
    """The firmware version from ``bcdDevice``, in ``xvf_host``'s spelling.

    The free reading: no tool, no root, no USB control transfer. ``0206`` is ``2 0 6`` and
    ``020a`` is ``2 0 10``, both measured on this project's arrays; the field is hex per
    nibble, which is also why a minor or patch of 16 or more could not be represented.
    ``0210`` would be ``2 1 0`` and has never been observed anywhere.
    """
    if len(devices) != 1:
        return None
    text = (devices[0].get("bcdDevice") or "").strip()
    if len(text) != 4:
        return None
    try:
        return f"{int(text[:2], 16)} {int(text[2], 16)} {int(text[3], 16)}"
    except ValueError:
        return None


# --- the pre-flight ----------------------------------------------------------
def evaluate(
    mule: ssh.Mule,
    *,
    authorisation: str | None,
    repo_root: Path,
    simulate: str | None = None,
) -> tuple[Preflight, dict[str, Any]]:
    """Every gate in front of a write, evaluated in order and none of them short-circuited.

    Every gate is evaluated even after one has refused, because a pre-flight whose job is to
    tell an operator whether a bench session can go ahead is worth much more when it reports
    all four things that are wrong than when it reports the first. The console prints them
    in order and the artifact keeps all of them.
    """
    flight = Preflight()
    facts: dict[str, Any] = {}
    home = _home(mule)
    facts["home"] = home

    # Gate 3 - the pin, held twice. First, because every later gate compares against it.
    try:
        facts["pin"] = cross_check_pin(repo_root)
        flight.add(Gate(
            "pin.agreement", True,
            expected=f"{AGENT_PIN_SOURCE} agrees with flh/flash.py",
            observed=f"{len(IMAGES)} images, all fields equal",
            why="a bump has to move both records or this command refuses",
        ))
    except HarnessError as error:
        flight.add(Gate(
            "pin.agreement", False,
            expected=f"{AGENT_PIN_SOURCE} agrees with flh/flash.py",
            observed=str(error),
            why=error.remedy or "",
        ))

    # The interlock decision 91 names as living on the workstation.
    marker = read_marker(mule)
    facts["marker"] = marker
    flight.add(Gate(
        "flash.marker", marker is None,
        expected=f"{MARKER_PATH} absent",
        observed=marker or "absent",
        why=(
            "" if marker is None else
            "A previous firmware write on this frame never finished. A cgroup kill, a power "
            "cut and a crash all leave the same array behind, so nothing knows how far that "
            "write got - and a state nothing can measure is one a person should look at. "
            "(This is NOT the claim that a write may not be repeated: XMOS documents that "
            "another download may be reattempted, and the agent writes up to three times per "
            "authorisation. What must not be repeated blindly is an INTERRUPTED write.) "
            "A person looks at the unit and removes the file; "
            "`fl.py array flash --clear-marker` is how."
        ),
        refusal="PreviousFlashUnfinished",
    ))

    # The agent must be held. This is the harness's whole substitute for the agent's
    # in-process update stand-down and reboot hold, both of which are null in every process
    # except the one holding the window - so on a bench flash they protect nothing and a
    # stopped unit protects everything.
    state = xvf._show(  # noqa: SLF001
        mule, "ActiveState", "SubState", "InactiveEnterTimestamp", "InactiveEnterTimestampMonotonic",
    )
    active = state.get("ActiveState", "?")
    facts["agentActiveState"] = active
    flight.add(Gate(
        "agent.held", active in ("inactive", "failed"),
        expected="fl-agent.service inactive or failed",
        observed=active,
        why=(
            "" if active in ("inactive", "failed") else
            "A running agent self-updates hourly, and fl-agent.service leaves KillMode at "
            "control-group, so that restart SIGKILLs dfu-util through the cgroup. It also "
            "reboots after every Act and holds the array's HID interface. Run "
            "`fl.py array hold` first. Nothing restarts a unit an operator stopped."
        ),
    ))

    stray = mule.run("pgrep -x xvf_host || true", timeout=30).stdout.strip()
    facts["xvfHostProcesses"] = stray
    flight.add(Gate(
        "agent.no-control-tool", not stray,
        expected="no xvf_host process",
        observed=stray or "none",
        why="" if not stray else "xvf_host has no device selector and claims the array's HID interface.",
    ))

    uptime = mule.run("cut -d' ' -f1 /proc/uptime", timeout=30).stdout.strip()
    try:
        uptime_s = float(uptime)
    except ValueError:
        uptime_s = 0.0
    facts["uptimeSeconds"] = uptime_s
    flight.add(Gate(
        "frame.settled", uptime_s >= SETTLED_MINIMUM_UPTIME_S,
        expected=f"up for at least {SETTLED_MINIMUM_UPTIME_S:.0f} s",
        observed=f"{uptime_s:.0f} s",
        why=(
            "" if uptime_s >= SETTLED_MINIMUM_UPTIME_S else
            "Nothing on the frame can prevent mains loss, so the honest version of a "
            "power-cycle guard is refusing to start in conditions that correlate with "
            "instability. A frame that rebooted a minute ago is one of them."
        ),
    ))

    # The authorisation. Same string, same shape, same parse as the agent's setting.
    digest = (authorisation or "").split(":", 1)[0].strip()
    facts["authorisationDigest"] = digest
    flight.add(Gate(
        "auth.present", bool(authorisation),
        expected=f"--authorise <sha256>[:ticket], the same string {AUTHORISATION_KEY} carries",
        observed=authorisation or "absent",
        why="" if authorisation else "No firmware write is authorised on this frame.",
        refusal="NotAuthorised",
    ))
    flight.add(Gate(
        "auth.digest", digest.lower() == TARGET.sha256.lower(),
        expected=f"{TARGET.sha256} ({TARGET.name})",
        observed=digest or "absent",
        why=(
            "" if digest.lower() == TARGET.sha256.lower() else
            "A version number authorises nothing in particular: upstream has published one "
            "version string twice with different bytes. Only a digest is a name."
        ),
        refusal="NotThePinnedImage",
    ))

    consumed = read_consumed(mule)
    facts["consumed"] = consumed
    spent = bool(authorisation) and consumed == authorisation
    flight.add(Gate(
        "auth.unspent", not spent,
        expected=f"{CONSUMED_PATH} does not already hold this exact string",
        observed=consumed or "nothing spent",
        why=(
            "" if not spent else
            "This authorisation has already been used. Single-use means single-use; "
            "authorising another write means writing a different value, which is what the "
            "ticket after the colon is for."
        ),
        refusal="AlreadyConsumed",
    ))

    # The tool.
    tool_path = "/usr/bin/dfu-util-this-path-does-not-exist" if simulate == "tool-missing" else DFU_UTIL
    tool_ok = mule.run_privileged(f"test -x {shlex.quote(tool_path)}", timeout=30).ok
    version_text = ""
    if tool_ok:
        version_text = mule.run(f"{tool_path} --version 2>&1 | head -1", timeout=30).stdout.strip()
    facts["dfuUtil"] = {"path": tool_path, "present": tool_ok, "version": version_text}
    flight.add(Gate(
        "tool.dfu-util", tool_ok,
        expected=f"{tool_path} executable",
        observed=version_text or "not installed",
        why="" if tool_ok else f"{tool_path} is not installed on this frame, so nothing could write the image.",
        refusal="DfuUtilMissing",
    ))

    # The images, re-hashed on the frame. Target first, then the way back.
    facts["images"] = {}
    for image in IMAGES:
        path, actual, size = locate_image(mule, image, home)
        if simulate == "image-corrupt" and image.role == "Target":
            path, actual, size = f"{_expand(BENCH_DIRECTORY, home)}/corrupt.bin", *digest_on_frame(
                mule, f"{_expand(BENCH_DIRECTORY, home)}/corrupt.bin"
            )
        matches = actual is not None and actual.lower() == image.sha256.lower()
        facts["images"][image.role] = {
            "name": image.name, "path": path, "expected": image.sha256,
            "observed": actual, "sizeBytes": size, "matches": matches,
        }
        flight.add(Gate(
            f"image.{image.role.lower()}", matches,
            expected=f"{image.name} sha256 {image.sha256[:12]}",
            observed=(f"{path} sha256 {actual[:12]}" if actual else "absent from both directories"),
            why=(
                "" if matches else
                f"{image.name} - {image.purpose} - is missing or does not match the pinned "
                "digest. An unverified image must never be written to an array."
            ),
            refusal="ImageNotVerified",
        ))

    # The array.
    devices = attached_arrays(mule)
    if simulate == "no-array":
        devices = []
    elif simulate == "two-arrays":
        devices = devices + devices
    facts["arrays"] = devices
    flight.add(Gate(
        "array.exactly-one", len(devices) == 1,
        expected="exactly one 2886:001a on the bus",
        observed=f"{len(devices)} attached",
        why=(
            "" if len(devices) == 1 else
            ("No 2886:001a device is on this frame's USB bus."
             if not devices else
             f"{len(devices)} microphone units are attached, and nothing here can say which "
             "one would be written. xvf_host and dfu-util both take whichever enumerated first.")
        ),
        refusal="NoArrayAttached" if not devices else "MoreThanOneArray",
    ))

    running = descriptor_version(devices)
    facts["runningVersion"] = running
    at_target = running == TARGET.version
    flight.add(Gate(
        "array.not-at-target", not at_target,
        expected=f"anything but the target {TARGET.version}",
        observed=running or "unreadable",
        why=(
            "" if not at_target else
            f"The microphone unit already reports firmware {running}, which is the pinned "
            "target, so a write would change nothing."
        ),
        refusal="AlreadyAtTarget",
    ))

    return flight, facts


# --- the simulation stub -----------------------------------------------------
#: Upstream's own transcript shapes, reproduced so a simulated run's output is recognisable
#: to somebody who has read the issues. These are **not** captured from this project's
#: hardware and nothing here presents them as such - they are the shapes the parser and the
#: operator have to cope with, written down so both can be exercised.
STUB_TRANSCRIPTS: dict[str, tuple[int, str]] = {
    "success": (0, """dfu-util 0.11

Copyright 2005-2009 Weston Schmidt, Harald Welte and OpenMoko Inc.
Copyright 2010-2021 Tormod Volden and Stefan Schmidt
This program is Free Software and has ABSOLUTELY NO WARRANTY
Please report bugs to http://sourceforge.net/p/dfu-util/tickets/

Opening DFU capable USB device...
Device ID 2886:001a
Device DFU version 0101
Claiming USB DFU Interface...
Setting Alternate Interface #1 ...
Determining device status...
DFU state(2) = dfuIDLE, status(0) = No error condition is present
DFU mode device DFU version 0101
Device returned transfer size 256
Copying data from PC to DFU device
Download        [=========================] 100%       933888 bytes
Download done.
DFU state(7) = dfuMANIFEST, status(0) = No error condition is present
DFU state(2) = dfuIDLE, status(0) = No error condition is present
Done!
Resetting USB to switch back to Run-Time mode
"""),
    "dfu-error": (74, """dfu-util 0.11

Opening DFU capable USB device...
Device ID 2886:001a
Claiming USB DFU Interface...
Setting Alternate Interface #1 ...
Determining device status...
DFU state(10) = dfuERROR, status(10) = Device's firmware is corrupt
dfu-util: Error during download get_status
"""),
    "erase-out-of-range": (74, """dfu-util 0.11

Opening DFU capable USB device...
Device ID 2886:001a
Claiming USB DFU Interface...
Setting Alternate Interface #1 ...
Determining device status...
Device returned transfer size 256
Copying data from PC to DFU device
Download        [=======================  ]  96%      4030464 bytes
Download done.
dfu-util: Error during download get_status
DFU state(10) = dfuERROR, status(8) = Cannot program memory due to received address that is out of range
"""),
    "no-reenumerate": (0, """dfu-util 0.11

Opening DFU capable USB device...
Device ID 2886:001a
Claiming USB DFU Interface...
Setting Alternate Interface #1 ...
Copying data from PC to DFU device
Download        [=========================] 100%       933888 bytes
Download done.
Done!
Resetting USB to switch back to Run-Time mode
"""),
    "interrupted": (0, """dfu-util 0.11

Opening DFU capable USB device...
Device ID 2886:001a
Claiming USB DFU Interface...
Setting Alternate Interface #1 ...
Copying data from PC to DFU device
Download        [========                 ]  33%       308224 bytes
"""),
}


def _stub_source(outcome: str) -> str:
    """The shell program that stands in for ``dfu-util`` in a simulated run.

    It is generated here and its digest is checked on the frame immediately before it runs,
    so ``--simulate`` cannot be pointed at an arbitrary program: there is no option that
    takes a path, and the only thing this command will execute in simulation is a file whose
    bytes this process just produced. It reads no device, opens no USB handle and writes
    nothing anywhere.
    """
    code, transcript = STUB_TRANSCRIPTS[outcome]
    body = transcript.replace("\\", "\\\\").replace("'", "'\\''")
    slow = f"sleep {SIMULATED_WRITE_S:.0f}" if outcome == "interrupted" else "sleep 2"
    return (
        "#!/bin/sh\n"
        "# Written by tools/harness/flh/flash.py for a simulated flash. It touches no device.\n"
        f"echo 'SIMULATED dfu-util - outcome {outcome} - no device is touched' >&2\n"
        f"echo \"argv: $*\" >&2\n"
        f"{slow}\n"
        f"printf '%s' '{body}'\n"
        f"exit {code}\n"
    )


def _install_stub(mule: ssh.Mule, outcome: str, home: str) -> str:
    """Put the stub on the frame and prove the bytes there are the bytes generated here."""
    source = _stub_source(outcome)
    expected = hashlib.sha256(source.encode("utf-8")).hexdigest()
    directory = _expand(BENCH_DIRECTORY, home)
    path = f"{directory}/dfu-util-simulated"

    mule.run(f"mkdir -p {shlex.quote(directory)}", timeout=30).check("mkdir bench directory")
    encoded = _b64(source)
    mule.run(
        f"printf %s {encoded} | base64 -d > {shlex.quote(path)} && chmod 0755 {shlex.quote(path)}",
        timeout=60,
    ).check("write the simulation stub")

    actual = mule.run(f"sha256sum {shlex.quote(path)}", timeout=30).stdout.split()[0]
    if actual != expected:
        raise HarnessError(
            "The simulation stub on the frame is not the file this harness generated.",
            exit_code=5,
            remedy=(
                "Simulation refuses to run a program it cannot show it wrote. Nothing was "
                f"run. Expected sha256 {expected}, the frame has {actual}."
            ),
        )
    return path


# `recovery_runbook()` stood here and printed the Safe Mode procedure for a person to follow.
# It was removed on 2026-08-24 with the rest of this project's Safe Mode support, on the
# operator's decision: a board that has stopped presenting itself over USB goes back to the
# maintainer rather than being talked through a gesture down a telephone. Nothing in this
# module gates on its absence, so removing it cannot refuse a write.
#
# The knowledge is not lost and is deliberately not restated here: reference/xvf3800-recovery-
# model.md is the measured record of what Safe Mode is, why the Factory partition makes a bad
# write survivable, and why it is nonetheless the only route back from a board that will not
# enumerate - which is a route this software no longer offers.


# --- artifacts ---------------------------------------------------------------
def _run_dir() -> Path:
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    path = RUNS_DIR / f"{stamp}-array-flash"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _marker_text(detail: str) -> str:
    """The marker in the agent's own format, so both writers produce one readable thing.

    ``ArrayFlashWindow.Open`` writes ``{UtcNow:O} {detail}`` and a newline. The round-trip
    format ``O`` on a .NET ``DateTimeOffset`` is ISO-8601 with seven fractional digits and an
    explicit offset, which Python's ``isoformat`` does not produce, so it is built by hand
    here - the agent has to be able to read what this wrote and paste it into a sentence.
    """
    now = datetime.now(UTC)
    stamp = now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond:06d}0" + "+00:00"
    return f"{stamp} {detail}\n"


def write_marker(mule: ssh.Mule, detail: str) -> str:
    """Open the flash window on the card, atomically, before the device is touched.

    Written the way the agent writes it - staged as ``.new``, fsynced, renamed, 0600 root -
    because the reader that matters is the next agent process on a machine that lost power
    in the middle of this.
    """
    text = _marker_text(detail)
    encoded = _b64(text)
    staging = MARKER_PATH + ".new"
    script = (
        f"printf %s {encoded} | base64 -d > {staging}; "
        f"chmod 0600 {staging}; sync {staging}; mv -f {staging} {MARKER_PATH}"
    )
    mule.run_privileged(f"sh -c {shlex.quote(script)}", timeout=60).check(
        "write the flash-in-progress marker"
    )
    return text.strip()


def consume(mule: ssh.Mule, authorisation: str) -> None:
    """Spend the authorisation on the card, durably, **before** anything is started.

    The same file and the same bytes the agent's ``Consume`` writes -
    ``/var/lib/fl-agent/array-flash.consumed``, the authorisation string verbatim plus one
    newline, 0600 root - and for the same reason, plus one that is specific to a bench run.

    The agent's reason: everything after this line may die at any instant, and nothing after
    this line may authorise a second write.

    The bench reason: an operator who has armed the fleet setting *and* flashed from the
    bench has authorised **one operation**, not two. Without this, a bench flash would leave
    the frame's own ``audio.arrayFirmwareFlash`` still unspent, and the agent would begin an
    operation of its own a minute after being released - on an array a person has just been
    writing to by hand, with no idea what state that left it in. Sharing the record is what
    makes "one authorisation, one operation" true across both writers rather than true of
    each writer separately.

    Note what the agent's operation now is: up to ``ArrayFirmwareFlash.MaxAttempts`` writes,
    the operator's decision of 2026-08-24, spending the authorisation once before the first of
    them. That does not change this function's contract by a word - the record is still the
    exact authorisation string, still written before anything starts, and still the thing that
    stops a second *operation*.
    """
    encoded = _b64(authorisation + chr(10))
    staging = CONSUMED_PATH + ".new"
    script = (
        f"printf %s {encoded} | base64 -d > {staging}; "
        f"chmod 0600 {staging}; sync {staging}; mv -f {staging} {CONSUMED_PATH}"
    )
    mule.run_privileged(f"sh -c {shlex.quote(script)}", timeout=60).check(
        "record the spent authorisation"
    )


def clear_marker(mule: ssh.Mule) -> str | None:
    """Remove the marker. **A person's tool, never this module's own escape hatch.**

    Nothing in the flash path calls this except the one place a write returned normally.
    It exists so that after somebody has looked at the microphone unit - which is the whole
    condition the latch encodes - they can say so.
    """
    existing = read_marker(mule)
    if existing is None:
        return None
    mule.run_privileged(f"rm -f {MARKER_PATH} {MARKER_PATH}.new", timeout=30).check(
        "remove the marker"
    )
    return existing


# --- the operations ----------------------------------------------------------
def list_dfu(mule: ssh.Mule) -> str:
    """``dfu-util -l``. Enumerates alt settings and **writes nothing**.

    The operation that proves Safe Mode was entered: run-time mode lists alt 0 and alt 1,
    and Safe Mode lists a third, ``alt=2 "reSpeaker DFU DataPartition"``. That difference is
    the only confirmation available that the recovery route is open on a given unit, and
    decision 91 makes demonstrating it binding before any first flash.
    """
    result = mule.run_privileged(f"{DFU_UTIL} {' '.join(arguments('list'))} 2>&1", timeout=60)
    return (result.stdout + result.stderr).strip()


def _await_reenumeration(
    mule: ssh.Mule,
    expected_version: str,
    *,
    timeout_s: float = REENUMERATION_TIMEOUT_S,
    poll_s: float = REENUMERATION_POLL_S,
) -> tuple[str | None, float, list[str]]:
    """Poll sysfs until the array comes back reporting ``expected_version``, or time out.

    Evidence, not a timer - the same shape and the same two constants as the agent's
    ``AwaitReEnumerationAsync``, including its stickiness: a reading that goes away does not
    regress the answer to nothing, because an array that is mid-reset is not an array that
    is absent.
    """
    deadline = time.monotonic() + timeout_s
    started = time.monotonic()
    seen: str | None = None
    trail: list[str] = []
    while True:
        devices = attached_arrays(mule)
        reading = descriptor_version(devices)
        trail.append(
            f"+{time.monotonic() - started:5.1f}s  {len(devices)} attached, "
            f"version {reading or 'none'}"
        )
        seen = reading or seen
        if seen == expected_version or time.monotonic() >= deadline:
            return seen, time.monotonic() - started, trail
        time.sleep(poll_s)


def flash(
    *,
    authorise: str | None = None,
    write: bool = False,
    simulate: str | None = None,
    repo_root: Path | None = None,
    reenumeration_timeout_s: float = REENUMERATION_TIMEOUT_S,
) -> dict[str, Any]:
    """Pre-flight, and - only behind five independent gates - the write.

    With neither ``write`` nor ``simulate`` this evaluates every gate, reports each one, and
    resolves no writing program at all. That is the default and it is the mode an operator
    should run first, every time.
    """
    if write and simulate:
        raise HarnessError(
            "--write and --simulate are mutually exclusive.",
            remedy=(
                "A simulated run substitutes the program that performs the write. There is "
                "no mode in which both happen, and refusing here rather than picking one is "
                "the difference between a harness that is safe and a harness that is lucky."
            ),
        )
    if simulate is not None and simulate not in SIMULATIONS:
        raise HarnessError(
            f"'{simulate}' is not a simulated outcome.",
            remedy="Known outcomes: " + ", ".join(sorted(SIMULATIONS)),
        )

    repo_root = repo_root or Path(__file__).resolve().parent.parent.parent.parent
    directory = _run_dir()
    log = debuglog.open_log("array-flash", forced=True, directory=directory)
    mode = "write" if write else ("simulate:" + simulate if simulate else "preflight")

    record: dict[str, Any] = {
        "takenUtc": datetime.now(UTC).isoformat(),
        "mode": mode,
        "simulated": simulate is not None,
        "target": {
            "name": TARGET.name,
            "sha256": TARGET.sha256,
            "commit": TARGET.commit,
            "sizeBytes": TARGET.size_bytes,
            "version": TARGET.version,
            "url": TARGET.url(),
        },
        "authorisation": authorise,
        "reenumerationTimeoutSeconds": reenumeration_timeout_s,
    }

    try:
        with ssh.connect() as mule:
            ui.step(f"Array firmware - {mode} on {mule.host}")
            if simulate:
                ui.warn(f"SIMULATION '{simulate}': {SIMULATIONS[simulate]}")
                ui.warn(
                    "No byte reaches the array in this mode, and nothing here is a "
                    "measurement of one."
                )

            flight, facts = evaluate(
                mule, authorisation=authorise, repo_root=repo_root, simulate=simulate
            )
            record["gates"] = flight.as_dict()
            record["facts"] = facts
            record["permitted"] = flight.permitted

            _print_gates(flight)

            if not (write or simulate):
                record["outcome"] = "preflight only; nothing was run"
                _finish(record, directory, log)
                if flight.permitted:
                    ui.ok("Every gate passes. A write would proceed. Nothing was run.")
                else:
                    ui.warn(f"{len(flight.refusals)} gate(s) refuse. A write would not start.")
                return record

            if not flight.permitted:
                record["outcome"] = "refused before anything started: " + "; ".join(
                    gate.name for gate in flight.refusals
                )
                _finish(record, directory, log)
                ui.fail(record["outcome"])
                ui.info("Nothing was written and no process was started.")
                return record

            # --- past every gate ---------------------------------------------
            image_path = facts["images"]["Target"]["path"]
            before = facts["runningVersion"]

            if simulate:
                program = _install_stub(mule, simulate, facts["home"])
                ui.info(f"simulated writer: {program}")
            else:
                program = DFU_UTIL
                # Gate 4, again and last: the bytes, in the instant before the write. A
                # record that this file was verified a minute ago describes bytes that may
                # have changed since, and the reader that matters is the one about to hand
                # the file to dfu-util.
                final, _ = digest_on_frame(mule, image_path)
                if (final or "").lower() != TARGET.sha256.lower():
                    raise HarnessError(
                        "The target image changed between the pre-flight and the write.",
                        exit_code=3,
                        remedy=(
                            f"Expected {TARGET.sha256}, the frame now has "
                            f"{final or 'nothing'}. Nothing was written."
                        ),
                    )
                ui.ok(f"re-hashed in the instant before the write: {final[:16]}...")

            argv = arguments("download", image_path=image_path)
            detail = (
                f"writing {TARGET.name} (sha256 {TARGET.sha256[:12]}) to the microphone unit"
            )
            if simulate:
                detail = f"SIMULATED ({simulate}) - {detail}"

            if not simulate:
                # Spent first, durably, and only then is anything started. A simulated run
                # spends nothing, because nothing was written and the operator's one
                # authorisation is still theirs to use.
                consume(mule, authorise or "")
                record["consumed"] = True
                ui.ok(f"authorisation spent at {CONSUMED_PATH}; it will not authorise a second write")

            marker_text = write_marker(mule, detail)
            record["marker"] = marker_text
            ui.ok(f"marker on the card: {MARKER_PATH}")
            if log:
                log.note("the flash window is open on the card", detail=marker_text)

            command = f"{program} {' '.join(shlex.quote(part) for part in argv)}"
            ui.step(f"running: {command}")
            started = time.monotonic()
            returned = True
            try:
                if simulate == "interrupted":
                    transcript, exit_code = _run_and_interrupt(mule, command)
                    returned = False
                else:
                    result = mule.run_privileged(f"{command} 2>&1", timeout=600)
                    transcript = (result.stdout + result.stderr).strip()
                    exit_code = result.exit_status
            except Exception as error:  # noqa: BLE001
                # The one path the marker exists for. It is deliberately NOT removed.
                record["outcome"] = f"the write did not return: {error}"
                record["markerLeftBehind"] = True
                _finish(record, directory, log)
                ui.fail(record["outcome"])
                ui.fail(f"The marker is deliberately still on the card at {MARKER_PATH}.")
                return record

            elapsed = time.monotonic() - started
            record["dfuUtil"] = {
                "argv": [program, *argv],
                "exitCode": exit_code,
                "seconds": round(elapsed, 2),
                "transcript": transcript,
            }
            if log:
                log.capture("dfu-util.txt", transcript + "\n")
            ui.block("dfu-util", transcript)

            if not returned:
                # Interrupted: the marker stays, exactly as the agent's `finally` leaves it.
                record["markerLeftBehind"] = True
                record["outcome"] = (
                    "the write was interrupted and did not return, so the marker is still on "
                    "the card and every later flash - agent or bench - is now refused until "
                    "a person has looked at the unit"
                )
                _finish(record, directory, log)
                ui.fail(record["outcome"])
                ui.info("Clear it with: python tools/harness/fl.py array flash --clear-marker")
                return record

            # The write returned, so the window closes - the same asymmetry the agent keeps.
            expected = before if simulate == "success" else TARGET.version
            if simulate == "success":
                ui.warn(
                    f"SEAM: the verify is told to expect {expected}, the version the array "
                    "already reports, because no write happened. A real success expects "
                    f"{TARGET.version}."
                )
            after, waited, trail = _await_reenumeration(
                mule, expected, timeout_s=reenumeration_timeout_s
            )
            ui.block("re-enumeration poll", "\n".join(trail))

            cleared = clear_marker(mule)
            record["markerLeftBehind"] = False
            record["markerCleared"] = cleared is not None
            ui.ok(f"marker removed from {MARKER_PATH}")

            succeeded = after == expected
            record["before"] = before
            record["after"] = after
            record["expectedAfter"] = expected
            record["reenumerationSeconds"] = round(waited, 1)
            record["succeeded"] = succeeded
            record["outcome"] = (
                f"{'simulated writing' if simulate else 'wrote'} {TARGET.name} in "
                f"{elapsed:.0f} s; the array reported {before} before and "
                f"{after or 'nothing'} after, within {waited:.0f} s"
            )
            if succeeded:
                ui.ok(record["outcome"])
            else:
                ui.fail(record["outcome"])
                ui.fail(
                    "The array did not come back reporting the expected firmware. Nothing "
                    "further will be attempted without a new authorisation, and somebody "
                    "has to look at the unit."
                )

            if log:
                log.pull_journal(mule, since="-30min")
            _finish(record, directory, log)
            return record
    finally:
        debuglog.close_log(str(record.get("outcome", "")))


def _run_and_interrupt(mule: ssh.Mule, command: str) -> tuple[str, int]:
    """Start the write, then kill it mid-flight, as a cgroup teardown or a power cut would.

    The interruption is real: a real process is started on the frame and a real ``SIGKILL``
    ends it. What is simulated is only which program was running. This is the one outcome
    whose whole point is that the harness does **not** get a return value, so it must not
    wait for one - the output is collected afterwards from the file the killed process was
    writing to.
    """
    sink = "/tmp/fl-flash-interrupted.log"
    mule.run(f"rm -f {sink}", timeout=30)
    launch = f"{command} > {sink} 2>&1"
    mule.run(
        f"nohup sh -c {shlex.quote(launch)} >/dev/null 2>&1 & echo started", timeout=30
    )
    time.sleep(SIMULATED_WRITE_S / 2)
    # -x, matching the process NAME exactly, and never -f: `pkill -f dfu-util-simulated`
    # matches its own `sh -c` wrapper, so it kills the shell that was about to report the
    # result and the status is lost. Measured on Frame #1 2026-08-24, on the first run of
    # this path.
    killed = mule.run(
        "pkill -KILL -x dfu-util-simulated; echo killed=$?", timeout=30
    ).stdout.strip()
    ui.warn(f"SIGKILLed the running write ({killed}) - this is a real interruption")
    time.sleep(1.0)
    transcript = mule.run(f"cat {sink} 2>/dev/null || true", timeout=30).stdout.strip()
    return transcript or "(the process was killed before it wrote anything)", -9


def _print_gates(flight: Preflight) -> None:
    for gate in flight.gates:
        mark = "  ok" if gate.passed else "REFUSE"
        line = f"{mark}  {gate.name:<26} {gate.observed}"
        (ui.info if gate.passed else ui.fail)(line)
        if not gate.passed and gate.why:
            for part in gate.why.split(". "):
                if part.strip():
                    ui.info(f"        {part.strip().rstrip('.')}.")


def _finish(record: dict[str, Any], directory: Path, log: "debuglog.DebugLog | None") -> None:
    path = directory / "flash.json"
    path.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    if log:
        log.artifact(path, what="the certified record of this run")
    ui.kv({"artifact": str(directory), "debug log": str(log.path) if log else "none"})


# --- staging the pinned images ----------------------------------------------
def stage(*, repo_root: Path | None = None) -> dict[str, Any]:
    """Fetch the three pinned images, verify them here, and put them on the frame.

    Needed because ``firmware.xvf3800.image`` - the resource that does this for the whole
    fleet - only exists in agents built after decision 91, and a bench session may well be
    working with a frame that predates it. Every byte is verified twice: once on the
    workstation against the pin, and again by the frame's own ``sha256sum`` after the
    upload, because a digest measured on the sending side says nothing about what arrived.

    They go into the login user's home and **never** under ``/var/lib/fl-agent``: an image a
    person staged by hand must never be mistakable for one the agent fetched and verified.
    """
    import urllib.request

    repo_root = repo_root or Path(__file__).resolve().parent.parent.parent.parent
    cross_check_pin(repo_root)

    cache = RUNS_DIR / "firmware-images"
    cache.mkdir(parents=True, exist_ok=True)
    staged: dict[str, Any] = {}

    with ssh.connect() as mule:
        home = _home(mule)
        for image in IMAGES:
            local = cache / image.name
            cached = (
                local.exists()
                and hashlib.sha256(local.read_bytes()).hexdigest() == image.sha256
            )
            if not cached:
                ui.step(f"fetching {image.name} at {image.commit[:12]}")
                with urllib.request.urlopen(image.url(), timeout=180) as response:  # noqa: S310
                    payload = response.read()
                if len(payload) != image.size_bytes:
                    raise HarnessError(
                        f"{image.name} is {len(payload)} bytes; the pin says {image.size_bytes}.",
                        exit_code=5,
                        remedy="Nothing was written to the frame.",
                    )
                actual = hashlib.sha256(payload).hexdigest()
                if actual != image.sha256:
                    raise HarnessError(
                        f"{image.name} hashes to {actual}; the pin says {image.sha256}.",
                        exit_code=5,
                        remedy="Nothing was written to the frame.",
                    )
                local.write_bytes(payload)
            ui.ok(
                f"{image.name} verified on the workstation "
                f"({image.size_bytes} bytes, {'cached' if cached else 'fetched'})"
            )

            directory = f"{_expand(BENCH_IMAGE_DIRECTORY, home)}/{image.directory}"
            remote = f"{directory}/{image.name}"
            mule.run(f"mkdir -p {shlex.quote(directory)}", timeout=30).check(
                "make the bench image directory"
            )
            mule.put(local, remote, mode=0o640)

            confirmed, size = digest_on_frame(mule, remote)
            if confirmed != image.sha256:
                raise HarnessError(
                    f"{remote} hashes to {confirmed} on the frame, not {image.sha256}.",
                    exit_code=5,
                    remedy=(
                        "A digest measured on the sending side says nothing about what "
                        "arrived. Nothing will be flashed from this file."
                    ),
                )
            ui.ok(f"{image.role:<9} {remote} sha256 {confirmed[:16]}... ({size} bytes)")
            staged[image.role] = {"path": remote, "sha256": confirmed, "sizeBytes": size}

    return {"staged": staged, "directory": BENCH_IMAGE_DIRECTORY}


def clean() -> dict[str, Any]:
    """Remove the bench directory this module created on the frame.

    Deliberately does **not** touch the marker: a marker is a statement about the array, not
    about this harness's scratch files, and sweeping it away with the stub would be exactly
    the self-clearing latch the design forbids.
    """
    with ssh.connect() as mule:
        home = _home(mule)
        directory = _expand(BENCH_DIRECTORY, home)
        listing = mule.run(
            f"ls -laR {shlex.quote(directory)} 2>/dev/null || true", timeout=30
        ).stdout
        mule.run(f"rm -rf {shlex.quote(directory)}", timeout=60).check(
            "remove the bench directory"
        )
        mule.run("rm -f /tmp/fl-flash-interrupted.log", timeout=30)
        marker = read_marker(mule)
    ui.ok(f"removed {directory}")
    if marker:
        ui.warn(f"The flash marker is still on the card and was deliberately left: {marker}")
    return {"removed": directory, "listing": listing, "marker": marker}
