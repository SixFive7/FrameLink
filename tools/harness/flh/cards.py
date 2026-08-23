"""The SD card register - which physical card is where, what is on it, and how to tell.

The problem this exists for
---------------------------
There are three microSD cards in this project and **none of them is marked**. To the eye they
are interchangeable, so the answer to "which card is in the reader" cannot be recovered by
looking - it can only be recovered from a record, and a record nobody can check is a record
that will eventually be wrong. One of the three is the original v1 frame's system card, the
only surviving v1 system, and ``reference/v1-state-inventory.txt`` is a *capture* taken from
the machine that card ran: captures cannot be re-taken once their source is gone. Overwriting
that card would destroy the parity target's origin permanently.

The operator has committed to not moving a card without being told to, which is what makes a
written register able to stay true. This module is the other half of that commitment: it holds
the record, and it **checks the record against the card that is actually in the reader** so
that a slip is caught rather than inherited.

Where the record lives, and why it is not in progress.json
----------------------------------------------------------
``tools/harness/cards.json``, committed, not gitignored - so a compacted session, or an
entirely different session, reads it and knows. ``progress.json``'s ``orientation`` block
points at it, because orientation is what a session with no memory is told to read first.

It is a *separate* file rather than a section of ``progress.json`` for a mechanical reason.
``progress.save()`` rewrites every derived section on every write, so a fact authored into
that file survives until the next ``fl.py`` invocation and no longer - which is correct for a
file whose content is generated, and fatal for a record of where a physical object is, because
no code path can derive that. This file is authored, nothing regenerates it, and the two
sections that *are* derived from code (:data:`IDENTITY_FIELDS` and the read-me) are rewritten
on save exactly as ``progress.py`` does it.

What can actually tell two unmarked cards apart
-----------------------------------------------
Measured on this workstation 2026-08-23, read-only, against the card in the reader. The
finding that shapes everything below: **nothing readable through this reader is unique to the
physical card.**

* The **SD CID register** *is* unique per card - manufacturer, serial, production date, burned
  in at the factory. The Realtek USB bridge in this reader does not pass it through:
  ``Get-Disk`` and ``Win32_DiskDrive`` both report an **empty** ``SerialNumber``, and the
  ``UniqueId`` they do report (``USBSTOR\\DISK&VEN_REALSIL&...``) is the *reader's* device path,
  identical for every card ever inserted into it. A Pi with a native SD controller can read the
  CID at ``/sys/block/mmcblk0/device/cid``; this workstation cannot read it at all.
* The **MBR disk signature** is on-card, four bytes at offset 0x1B8, and is what Linux calls
  ``PTUUID`` and what ``root=PARTUUID=<sig>-02`` in ``cmdline.txt`` is built from. It is the
  strongest thing available here - but it is a property of the **image**, not of the card: two
  cards written from the same image file carry the same signature. It says what is on a card
  with near-certainty and which card it is only by elimination.
* The **filesystem volume serial** (exFAT/FAT ``VolumeSerialNumber``, ext4 ``UUID``) is on-card
  and is a property of whoever last *formatted* that filesystem - the factory, for a card that
  has never been flashed. Also strong, also not per-card.
* **Capacity, partition layout and filesystem type** narrow the field and nothing more.

So the register can say with confidence *what a card contains*, and can say *which card it is*
only as far as the population allows. That is stated in the output rather than smoothed over:
:func:`check` reports ``ambiguous`` when the evidence fits more than one register entry, and
never picks one.

The one field that is unique by construction is :data:`MARKER_FILENAME` - a small text file
this module can write into a card's FAT boot partition naming the card. It is unique because we
choose it, it is readable on Windows, on Linux and from the running frame, and nothing on
Raspberry Pi OS reads unrecognised files in ``/boot/firmware/``. :func:`label` writes it and is
**gated**: without ``--write`` it prints the exact bytes and the exact destination and stops,
and with ``--write`` it still refuses unless :func:`check` already agrees that the card in the
reader is the card being named. A card you cannot identify is a card you must not label.

Read-only, except for one gated write
-------------------------------------
Everything here is ``Get-Disk`` / ``Get-Partition`` / ``Get-Volume`` / ``Win32_LogicalDisk`` -
queries, no ``Set-``, no ``Format-``, no ``Clear-``, no ``Initialize-``, and nothing that opens
a physical drive handle. The single write this module can perform is :func:`label` with
``--write``, and it writes one text file into a mounted filesystem through the normal file API.
Flashing lives in ``framelink-scratch/m25-image/flash-card.ps1`` and deliberately not here.

Making sense to a Linux operator
--------------------------------
The register does not assume Windows. Every identity field carries the command that reads it on
**both** platforms in :data:`IDENTITY_FIELDS`, and those pairs are written into ``cards.json``
on every save, so the file explains how to reproduce its own contents on a machine this module
cannot run on. The probe itself is Windows-only - it is PowerShell - and says so, naming the
Linux equivalent, rather than pretending to a portability it does not have.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import ui
from .config import HARNESS_DIR, HarnessError

SCHEMA = "framelink.harness.cards/1"

#: The register itself. Committed, like ``progress.json`` and for the same reason: a session
#: that remembers nothing must be able to read it.
CARDS_FILE = HARNESS_DIR / "cards.json"

#: The marker file :func:`label` writes into a card's FAT boot partition. Chosen over changing
#: the volume label because the boot partition of a Raspberry Pi OS card is labelled ``bootfs``
#: and this repository holds no evidence about what, if anything, mounts by that label - so
#: renaming it is a risk that has not been measured, while adding a file is not a risk at all.
MARKER_FILENAME = "FRAMELINK-CARD.txt"

#: Filesystems :func:`label` will write a marker into. Windows cannot write ext4 at all, so the
#: rootfs partition of a flashed card is never a candidate and never needs to be excluded by
#: hand.
WRITABLE_FILESYSTEMS = ("exFAT", "FAT32", "FAT", "FAT16", "NTFS")

#: Seconds before the PowerShell probe is considered hung. A storage query that has not
#: answered in this long is a driver problem, not a slow disk.
PROBE_TIMEOUT_S = 60.0

#: What each identity field is, what it actually distinguishes, and how to read it on either
#: platform. Written into ``cards.json`` on every save - the register carries its own
#: instructions, so a Linux operator holding only the file can reproduce every value in it.
#:
#: ``distinguishes`` is the honest part and the reason this table exists rather than a bare list
#: of field names. ``card`` means the value is unique to this piece of silicon; ``image`` means
#: every card written from the same image shares it; ``format`` means every card formatted by
#: the same tool run shares it; ``class`` means it narrows the population and no more.
IDENTITY_FIELDS: dict[str, dict[str, str]] = {
    "mbrSignature": {
        "what": (
            "The four-byte MBR disk signature at offset 0x1B8, lower-case hex. This is the "
            "prefix of every PARTUUID on the card, so `root=PARTUUID=f870549c-02` in a "
            "cmdline.txt names signature f870549c."
        ),
        "distinguishes": "image",
        "windows": "(Get-Disk -Number N).Signature   # decimal; '{0:x8}' -f it for hex",
        "linux": "lsblk -no PTUUID /dev/sdX   # or blkid -s PTUUID -o value /dev/sdX",
    },
    "capacityBytes": {
        "what": (
            "Total capacity as the storage stack reports it. Use Get-Disk, not "
            "Win32_DiskDrive: the latter reports TotalSectors*512 rounded down to whole "
            "cylinders and came back 3,392,000 bytes smaller on the card measured here, which "
            "is enough to make a naive equality check fail against itself."
        ),
        "distinguishes": "class",
        "windows": "(Get-Disk -Number N).Size",
        "linux": "blockdev --getsize64 /dev/sdX",
    },
    "partitions": {
        "what": (
            "One entry per primary partition: MBR type byte, byte offset and byte length. A "
            "Raspberry Pi OS card is 0x0c (FAT32 LBA) then 0x83 (Linux); a factory card is "
            "one 0x07 exFAT partition."
        ),
        "distinguishes": "image",
        "windows": "Get-Partition -DiskNumber N | Select MbrType, Offset, Size",
        "linux": "sfdisk -d /dev/sdX   # or lsblk -bno NAME,START,SIZE,PARTTYPE /dev/sdX",
    },
    "volumeSerials": {
        "what": (
            "The filesystem volume serial of each mountable partition - exFAT/FAT store a "
            "32-bit serial in the boot record, ext4 a UUID. Set by whoever formatted the "
            "filesystem, which for a never-flashed card is the factory."
        ),
        "distinguishes": "format",
        "windows": "Get-CimInstance Win32_LogicalDisk -Filter \"DeviceID='F:'\" | Select VolumeSerialNumber",
        "linux": "blkid -s UUID -o value /dev/sdXn   # FAT serials print as 2C32-CEEB",
    },
    "volumeLabels": {
        "what": (
            "The filesystem label of each mountable partition. Empty on the factory card; "
            "'bootfs' and 'rootfs' on a Raspberry Pi OS card."
        ),
        "distinguishes": "format",
        "windows": "Get-Volume -DriveLetter F | Select FileSystemLabel",
        "linux": "blkid -s LABEL -o value /dev/sdXn",
    },
    "marker": {
        "what": (
            f"The contents of {MARKER_FILENAME} in the card's FAT boot partition, if this "
            "harness has written one. The only field that is unique to a card by "
            "construction, because the value is chosen rather than observed."
        ),
        "distinguishes": "card",
        "windows": f"Get-Content F:\\{MARKER_FILENAME}",
        "linux": f"cat /boot/firmware/{MARKER_FILENAME}   # or from the mounted boot partition",
    },
    "sdCid": {
        "what": (
            "The SD CID register - manufacturer, OEM, product name, revision, serial and "
            "production date, burned in at manufacture and genuinely unique per card. NOT "
            "AVAILABLE through the USB reader on this workstation: the Realtek bridge reports "
            "an empty SerialNumber and passes no CID through. Readable only from a Pi, whose "
            "native SD controller exposes it. Recorded here so that a session on a frame knows "
            "it is worth capturing."
        ),
        "distinguishes": "card",
        "windows": "not available through a USB card reader",
        "linux": "cat /sys/block/mmcblk0/device/cid   # on the Pi itself, no root needed",
    },
}

_READ_ME_FIRST = [
    "This is the FrameLink SD card register. There are three physical microSD cards and none "
    "of them is marked, so this file is the only way to answer 'which card is where and what "
    "is on it' without a human squinting at a card. It is written by "
    "`python tools/harness/fl.py cards` and is pointed at from progress.json's orientation "
    "block.",
    "Run `python tools/harness/fl.py cards check` first. It reads whatever card is in the "
    "workstation's USB reader and compares it against what this file claims is in there. "
    "Exit 0 means the register and reality agree. Exit 2 means they do not, and the register "
    "is then not to be trusted until someone works out which of the two is wrong.",
    "The operator has committed to not moving a card unless asked to. That commitment is what "
    "keeps this file true; `cards check` is what catches it if the commitment ever slips. "
    "After any card is moved, record it: `fl.py cards record --card <id> --kind <kind> "
    "--where '<text>' --why '<reason>'`. A move that is not recorded is a register that lies.",
    "identityFields explains what each fingerprint field distinguishes and how to read it on "
    "Windows and on Linux. Read it before trusting a match: most of these fields identify the "
    "IMAGE on a card, not the card itself, so two cards flashed from the same file are "
    "indistinguishable by everything except the marker file. `cards check` reports 'ambiguous' "
    "rather than guessing when that happens.",
    "The v1 card is irreplaceable. It is the only surviving v1 system, and "
    "reference/v1-state-inventory.txt is a capture taken from the machine it ran - captures "
    "cannot be re-taken once their source is gone. Never flash it, never format it, never let "
    "a flashing tool resolve to it.",
]

_KEY_ORDER = [
    "schema",
    "generatedBy",
    "updatedUtc",
    "readMeFirst",
    "reader",
    "cards",
    "images",
    "identityFields",
]


def utcnow() -> str:
    """Timestamp in the one format the harness uses: RFC 3339, UTC, second precision."""
    return datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z")


# --- the register file -----------------------------------------------------------------


def _empty() -> dict[str, Any]:
    """A register with no cards in it.

    Deliberately **not** a seeded copy of the three cards. Seeding here would put the same
    facts in two places - this function and the committed file - and the two would eventually
    disagree without either one looking wrong. A missing register is a real problem with a real
    recovery path, and saying so is more useful than silently reconstructing something that may
    be months out of date.
    """
    return {
        "schema": SCHEMA,
        "generatedBy": "tools/harness/fl.py cards",
        "updatedUtc": utcnow(),
        "readMeFirst": list(_READ_ME_FIRST),
        "reader": None,
        "cards": [],
        "images": {},
        "identityFields": {k: dict(v) for k, v in IDENTITY_FIELDS.items()},
        "lost": (
            f"{CARDS_FILE.name} did not exist when this was written, so every card record is "
            f"gone. It is a tracked file: recover it with "
            f"`git log --oneline -- tools/harness/{CARDS_FILE.name}` and "
            f"`git checkout <sha> -- tools/harness/{CARDS_FILE.name}`. Do not re-create the "
            f"records from memory - a register nobody can check is worse than none."
        ),
    }


def load() -> dict[str, Any]:
    """Read the register. A missing or unparseable file yields an empty one that says so."""
    if not CARDS_FILE.exists():
        return _empty()
    try:
        data = json.loads(CARDS_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise HarnessError(
            f"{CARDS_FILE} could not be read: {exc}",
            exit_code=3,
            remedy=(
                "The register is a tracked file. Recover it rather than re-typing it:\n"
                f"    git checkout HEAD -- tools/harness/{CARDS_FILE.name}"
            ),
        ) from exc
    if data.get("schema") != SCHEMA:
        raise HarnessError(
            f"{CARDS_FILE} declares schema {data.get('schema')!r}, not {SCHEMA!r}.",
            exit_code=3,
            remedy="A newer or older harness wrote it. Check out the matching revision.",
        )
    return data


def save(data: dict[str, Any]) -> None:
    """Atomically write the register.

    Temp file in the same directory then ``os.replace``, and ``newline="\\n"`` because
    ``.gitattributes`` pins the working tree to LF on every OS and this file is tracked. Both
    are the same choices ``progress.save`` makes, for the same two reasons: a half-written
    register would be trusted by whoever read it next, and a CRLF translation would show every
    run as a whole-file diff.
    """
    data["schema"] = SCHEMA
    data["generatedBy"] = "tools/harness/fl.py cards"
    data["updatedUtc"] = utcnow()
    # Derived from the code above on every write, never authored. See progress.py's _refresh:
    # a constant that belongs to the module and is copied into a data file will otherwise go
    # on quoting a version of itself that was reworded a long time ago.
    data["readMeFirst"] = list(_READ_ME_FIRST)
    data["identityFields"] = {k: dict(v) for k, v in IDENTITY_FIELDS.items()}

    CARDS_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = CARDS_FILE.with_suffix(".json.tmp")
    tmp.write_text(
        json.dumps(_ordered(data), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    os.replace(tmp, CARDS_FILE)


def _ordered(data: dict[str, Any]) -> dict[str, Any]:
    """Canonical key order, anything unrecognised kept at the end."""
    ordered = {key: data[key] for key in _KEY_ORDER if key in data}
    ordered.update({key: value for key, value in data.items() if key not in ordered})
    return ordered


def card(data: dict[str, Any], card_id: str) -> dict[str, Any]:
    """Find one card by id, or raise naming the ones that exist."""
    for entry in data.get("cards", []):
        if entry.get("id") == card_id:
            return entry
    known = ", ".join(sorted(e.get("id", "?") for e in data.get("cards", []))) or "(none)"
    raise HarnessError(
        f"No card {card_id!r} in the register.",
        exit_code=3,
        remedy=f"Known cards: {known}. See `python tools/harness/fl.py cards list`.",
    )


# --- reading the card that is in the reader --------------------------------------------

#: One PowerShell invocation that answers the whole question, so a probe is one process rather
#: than five and cannot see the reader in two different states halfway through.
#:
#: Only ``Get-`` cmdlets appear in it. ``-ErrorAction SilentlyContinue`` on ``Get-Partition``
#: matters: a reader with no card in it presents a disk object whose media is absent, and
#: asking that disk for its partitions is an error rather than an empty list.
_PROBE_PS = r"""
$ErrorActionPreference = 'Stop'
$result = @{ disks = @() }
foreach ($d in (Get-Disk | Where-Object { $_.BusType -eq 'USB' })) {
    $drive = Get-CimInstance Win32_DiskDrive -Filter "Index=$($d.Number)" -ErrorAction SilentlyContinue
    $parts = @()
    foreach ($p in (Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)) {
        $letter = $null; $fs = $null; $label = $null; $serial = $null; $free = $null
        if ($p.DriveLetter -and [int][char]$p.DriveLetter -ne 0) {
            $letter = [string]$p.DriveLetter
            $vol = Get-Volume -DriveLetter $p.DriveLetter -ErrorAction SilentlyContinue
            if ($vol) { $fs = $vol.FileSystemType; $label = $vol.FileSystemLabel; $free = $vol.SizeRemaining }
            $ld = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($letter):'" -ErrorAction SilentlyContinue
            if ($ld) { $serial = $ld.VolumeSerialNumber; if (-not $fs) { $fs = $ld.FileSystem } }
        }
        $parts += @{
            index       = $p.PartitionNumber
            mbrType     = $p.MbrType
            offsetBytes = $p.Offset
            sizeBytes   = $p.Size
            driveLetter = $letter
            filesystem  = $fs
            label       = $label
            volumeSerial= $serial
            freeBytes   = $free
        }
    }
    $result.disks += @{
        number            = $d.Number
        uniqueId          = $d.UniqueId
        friendlyName      = $d.FriendlyName
        model             = if ($drive) { $drive.Model } else { $null }
        mediaType         = if ($drive) { $drive.MediaType } else { $null }
        diskSerial        = $d.SerialNumber
        capacityBytes     = $d.Size
        partitionStyle    = $d.PartitionStyle
        signature         = $d.Signature
        operationalStatus = [string]$d.OperationalStatus
        isReadOnly        = $d.IsReadOnly
        partitions        = $parts
    }
}
$result | ConvertTo-Json -Depth 8 -Compress
"""


def _powershell() -> str:
    """Locate a PowerShell. ``pwsh`` first, then the in-box ``powershell``."""
    for name in ("pwsh", "powershell"):
        found = shutil.which(name)
        if found:
            return found
    raise HarnessError(
        "No PowerShell found - cannot read the card reader.",
        exit_code=4,
        remedy=(
            "This probe is Windows-only. On Linux the same values come from:\n"
            "    lsblk -o NAME,PTUUID,PARTUUID,UUID,LABEL,FSTYPE,SIZE,MODEL\n"
            "    blkid\n"
            "and the register's identityFields block names the command for each field."
        ),
    )


def probe() -> list[dict[str, Any]]:
    """Return every USB disk the workstation can see, with its partitions and volumes.

    Read-only by construction: the script is a module constant containing only ``Get-``
    cmdlets, it takes no argument from the caller, and it is passed with ``-Command`` as a
    single fixed string, so nothing a card or a register entry contains can reach a shell.
    """
    exe = _powershell()
    try:
        result = subprocess.run(  # noqa: S603 - argv is a module constant, never user text
            [exe, "-NoProfile", "-NonInteractive", "-Command", _PROBE_PS],
            capture_output=True,
            text=True,
            timeout=PROBE_TIMEOUT_S,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise HarnessError(
            f"The storage probe did not answer within {PROBE_TIMEOUT_S:.0f}s.", exit_code=5
        ) from exc
    except OSError as exc:
        raise HarnessError(f"Could not run {exe}: {exc}", exit_code=4) from exc

    if result.returncode != 0:
        raise HarnessError(
            f"The storage probe failed: {(result.stderr or result.stdout).strip()[:400]}",
            exit_code=5,
        )
    text = result.stdout.strip()
    if not text:
        return []
    try:
        payload = json.loads(text)
    except json.JSONDecodeError as exc:
        raise HarnessError(f"The storage probe returned unparseable JSON: {exc}", exit_code=5) from exc

    disks = payload.get("disks") or []
    # ConvertTo-Json collapses a one-element array to the element itself. Nothing downstream
    # should have to know that.
    if isinstance(disks, dict):
        disks = [disks]
    return list(disks)


def reader_disk(data: dict[str, Any], disks: list[dict[str, Any]]) -> tuple[dict[str, Any] | None, str]:
    """Pick out the disk that is *this workstation's card reader*, and say how it was picked.

    The reader is named by its USBSTOR device identity, exactly as
    ``framelink-scratch/m25-image/flash-card.ps1`` names it, and never by drive letter or disk
    number: both of those are assigned at enumeration time and move. That identity contains the
    machine name, so it is workstation-specific - on a different machine the register's
    ``reader`` block has to be re-established, and this function says so rather than reporting
    a bare absence.
    """
    reader = data.get("reader") or {}
    unique = reader.get("uniqueId")
    if not unique:
        return None, (
            "The register names no reader, so no disk can be identified as one. "
            "`fl.py cards identify` lists every USB disk; put the right uniqueId into the "
            "register's reader block."
        )
    matches = [d for d in disks if (d.get("uniqueId") or "") == unique]
    if len(matches) == 1:
        return matches[0], f"matched the register's reader uniqueId {unique}"
    if not matches:
        seen = ", ".join(str(d.get("uniqueId")) for d in disks) or "(no USB disks at all)"
        return None, (
            f"No USB disk matches the register's reader uniqueId {unique}. USB disks seen: {seen}"
        )
    return None, f"{len(matches)} USB disks share uniqueId {unique}, which cannot happen; refusing to guess"


def fingerprint(disk: dict[str, Any]) -> dict[str, Any]:
    """Reduce a probed disk to the fields the register compares on.

    ``None`` is used for "the reader answered and there is no card", which is a different fact
    from "the field was not looked at" and has to survive into :func:`compare` intact.
    """
    signature = disk.get("signature")
    partitions = [
        {
            "index": p.get("index"),
            "mbrType": p.get("mbrType"),
            "offsetBytes": p.get("offsetBytes"),
            "sizeBytes": p.get("sizeBytes"),
        }
        for p in (disk.get("partitions") or [])
    ]
    volumes = [p for p in (disk.get("partitions") or []) if p.get("driveLetter")]
    return {
        "mbrSignature": f"{int(signature):08x}" if isinstance(signature, int) else None,
        "capacityBytes": disk.get("capacityBytes"),
        "partitionStyle": disk.get("partitionStyle"),
        "partitions": partitions,
        "volumeSerials": [v.get("volumeSerial") for v in volumes],
        "volumeLabels": [v.get("label") or "" for v in volumes],
        "filesystems": [v.get("filesystem") for v in volumes],
        "marker": _read_marker(volumes),
    }


def _read_marker(volumes: list[dict[str, Any]]) -> str | None:
    """Read :data:`MARKER_FILENAME` from the first mounted volume that has one."""
    for volume in volumes:
        letter = volume.get("driveLetter")
        if not letter:
            continue
        path = Path(f"{letter}:/{MARKER_FILENAME}")
        try:
            if path.is_file():
                return path.read_text(encoding="utf-8", errors="replace").strip()
        except OSError:
            continue
    return None


# --- comparison ------------------------------------------------------------------------

#: Fields compared field-by-field. ``partitions`` is compared structurally rather than as a
#: string, so a partition table that gained a drive letter still matches.
_COMPARED = ("marker", "mbrSignature", "volumeSerials", "volumeLabels", "partitions", "capacityBytes")


def compare(recorded: dict[str, Any] | None, observed: dict[str, Any]) -> dict[str, Any]:
    """Compare a register fingerprint against an observed one, field by field.

    Only fields present on **both** sides are judged. A register entry that records nothing but
    an MBR signature is judged on its MBR signature, and the fields it does not carry are
    reported as ``notRecorded`` rather than counted as agreement - the difference matters,
    because a card recognised on no evidence at all must never read as a match.
    """
    agreed: list[str] = []
    differed: list[dict[str, Any]] = []
    not_recorded: list[str] = []
    recorded = recorded or {}

    for field in _COMPARED:
        want = recorded.get(field)
        got = observed.get(field)
        if want is None or want == [] or want == "":
            not_recorded.append(field)
            continue
        if want == got:
            agreed.append(field)
        else:
            differed.append({"field": field, "register": want, "observed": got})

    strength = "none"
    if any(IDENTITY_FIELDS.get(f, {}).get("distinguishes") == "card" for f in agreed):
        strength = "conclusive"
    elif agreed:
        strength = "consistent"

    return {
        "agreed": agreed,
        "differed": differed,
        "notRecorded": not_recorded,
        "matches": bool(agreed) and not differed,
        "strength": strength,
    }


def candidates(data: dict[str, Any], observed: dict[str, Any]) -> list[tuple[str, dict[str, Any]]]:
    """Every register card the observed fingerprint is consistent with, best evidence first."""
    found: list[tuple[str, dict[str, Any]]] = []
    for entry in data.get("cards", []):
        verdict = compare(entry.get("fingerprint"), observed)
        if verdict["matches"]:
            found.append((entry.get("id", "?"), verdict))
    order = {"conclusive": 0, "consistent": 1, "none": 2}
    found.sort(key=lambda pair: (order.get(pair[1]["strength"], 3), -len(pair[1]["agreed"])))
    return found


# --- actions ---------------------------------------------------------------------------


def _location_line(entry: dict[str, Any]) -> str:
    location = entry.get("location") or {}
    since = location.get("sinceUtc") or "unknown"
    return f"{location.get('where', '?')}  [{location.get('kind', '?')}, since {since}]"


def show() -> int:
    """Print the whole register. The default action, and the one a stranger reads first."""
    data = load()
    if data.get("lost"):
        ui.fail(str(data["lost"]))
        return 3

    reader = data.get("reader") or {}
    ui.step(f"Card register - {CARDS_FILE}")
    ui.info(f"reader: {reader.get('friendlyName', '?')}  {reader.get('uniqueId', '?')}")
    print()

    for entry in data.get("cards", []):
        contents = entry.get("contents") or {}
        fp = entry.get("fingerprint") or {}
        recognisable = "yes" if any(fp.get(f) for f in _COMPARED) else "NO - cannot be recognised in a reader"
        planned = contents.get("plannedImageId")
        ui.step(f"{entry.get('id')} - {entry.get('title', '')}")
        ui.kv(
            {
                "where": _location_line(entry),
                "contents": contents.get("summary", "?"),
                "image": contents.get("imageId") or (f"none yet; planned {planned}" if planned else "-"),
                "agent": contents.get("agentVersion") or "-",
                "handling": entry.get("handling", "-"),
                "fingerprint": recognisable,
                "mbrSignature": fp.get("mbrSignature") or "not captured",
                "last move": (entry.get("history") or [{}])[-1].get("utc", "-"),
            }
        )
        print()

    images = data.get("images") or {}
    if images:
        ui.step("Images referenced above")
        for image_id, image in images.items():
            ui.kv(
                {
                    image_id: image.get("summary", ""),
                    "  path": image.get("path", "-"),
                    "  sha256": image.get("sha256", "-"),
                    "  signature": image.get("mbrSignature", "-"),
                    "  agent": image.get("agentVersion", "-"),
                }
            )
            print()

    ui.info("`fl.py cards check` compares the reader against what this register claims.")
    return 0


def identify(*, card_id: str | None = None, force: bool = False) -> int:
    """Report what is in the reader; with ``--card``, capture it as that card's fingerprint.

    Without ``--card`` this writes nothing and is the plain answer to "what is available to
    tell these cards apart". With ``--card`` it records the observation, and refuses to
    overwrite a fingerprint that disagrees unless ``--force`` is given: a disagreement is the
    alarm this whole module exists to raise, and quietly recording over it would be the one
    action that turns the register into a thing that cannot detect its own drift.
    """
    data = load()
    disks = probe()
    disk, how = reader_disk(data, disks)

    ui.step("USB disks visible to this workstation")
    for candidate in disks:
        ui.kv(
            {
                f"disk {candidate.get('number')}": f"{candidate.get('model') or candidate.get('friendlyName')}",
                "  uniqueId": candidate.get("uniqueId", "-"),
                "  capacity": f"{candidate.get('capacityBytes') or 0:,} B",
                "  status": str(candidate.get("operationalStatus")),
                "  diskSerial": candidate.get("diskSerial") or "(empty - the reader passes no card serial through)",
            }
        )
    print()
    ui.info(f"reader: {how}")

    if disk is None:
        ui.warn("No reader identified, so nothing was fingerprinted.")
        return 3

    observed = fingerprint(disk)
    ui.step("Fingerprint of the card in the reader")
    ui.kv(
        {
            "mbrSignature": observed.get("mbrSignature") or "(no partition table signature)",
            "capacityBytes": f"{observed.get('capacityBytes') or 0:,}",
            "partitions": json.dumps(observed.get("partitions")),
            "volumeSerials": json.dumps(observed.get("volumeSerials")),
            "volumeLabels": json.dumps(observed.get("volumeLabels")),
            "filesystems": json.dumps(observed.get("filesystems")),
            "marker": observed.get("marker") or f"(no {MARKER_FILENAME} on this card)",
        }
    )
    for field, meta in IDENTITY_FIELDS.items():
        if meta["distinguishes"] == "card" and not observed.get(field):
            ui.info(f"{field}: {meta['what'].splitlines()[0]}")

    if not observed.get("mbrSignature") and not observed.get("volumeSerials"):
        ui.warn("Nothing identifying was readable. Is there a card in the reader?")
        return 3

    matched = candidates(data, observed)
    if matched:
        ui.info("consistent with register cards: " + ", ".join(f"{i} ({v['strength']})" for i, v in matched))
    else:
        ui.info("consistent with no card in the register")

    if card_id is None:
        return 0

    entry = card(data, card_id)
    existing = entry.get("fingerprint") or {}
    if existing and not force:
        verdict = compare(existing, observed)
        if verdict["differed"]:
            ui.abort(
                f"The card in the reader does NOT match the fingerprint recorded for {card_id!r}.\n"
                + "\n".join(
                    f"{d['field']}: register {d['register']!r}, reader {d['observed']!r}"
                    for d in verdict["differed"]
                )
                + "\nNothing was written. Work out which card this is before overwriting the record."
            )
            return 2
    entry["fingerprint"] = {**observed, "capturedUtc": utcnow(), "capturedBy": "fl.py cards identify"}
    save(data)
    ui.ok(f"fingerprint recorded for {card_id}")
    return 0


def check() -> int:
    """Compare the reader against what the register claims is in it. The load-bearing action.

    Every disagreement is loud and exits non-zero, including the two that are easy to miss: a
    card present when the register says the reader is empty, and a reader empty when the
    register says a card is in it. Both mean a card moved without being recorded, which is
    exactly the failure the register exists to catch.
    """
    data = load()
    disks = probe()
    disk, how = reader_disk(data, disks)

    expected = next(
        (e for e in data.get("cards", []) if (e.get("location") or {}).get("kind") == "reader"),
        None,
    )
    expected_id = expected.get("id") if expected else None

    ui.step("Register vs reality")
    ui.kv(
        {
            "register says in the reader": expected_id or "(nothing)",
            "reader": how,
        }
    )

    if disk is None or not (disk.get("partitions") or disk.get("signature")):
        if expected is None:
            ui.ok("No card in the reader and the register does not claim one. Agreed.")
            return 0
        ui.abort(
            f"The register says card {expected_id!r} is in the workstation reader, and no card "
            f"can be read there.\n"
            f"{how}\n"
            f"Either the card was moved without being recorded, or the reader itself is not "
            f"attached - the line above says which. Record the move with `fl.py cards record` "
            f"once you know where it went."
        )
        return 2

    observed = fingerprint(disk)
    matched = candidates(data, observed)

    if expected is None:
        names = ", ".join(f"{i} ({v['strength']})" for i, v in matched) or "no card in the register"
        ui.abort(
            "There is a card in the workstation reader and the register does not claim one is "
            f"there.\nIt is consistent with: {names}\n"
            f"MBR signature {observed.get('mbrSignature')}, volume serials "
            f"{observed.get('volumeSerials')}.\n"
            "Record where this card actually is with `fl.py cards record`."
        )
        return 2

    verdict = compare(expected.get("fingerprint"), observed)

    if not (expected.get("fingerprint") or {}):
        ui.abort(
            f"The register says {expected_id!r} is in the reader but has no fingerprint for it, "
            "so the claim cannot be checked at all.\n"
            f"Capture one with `fl.py cards identify --card {expected_id}` once you are certain "
            "which card this is."
        )
        return 2

    if verdict["differed"]:
        others = ", ".join(f"{i} ({v['strength']})" for i, v in matched if i != expected_id)
        ui.abort(
            f"The card in the reader is NOT the card the register says is in it.\n"
            f"register claims: {expected_id}\n"
            + "\n".join(
                f"  {d['field']}: register {d['register']!r}, reader {d['observed']!r}"
                for d in verdict["differed"]
            )
            + (f"\nThe card in the reader is consistent with: {others}" if others else "")
            + "\nStop. Do not flash anything until this is resolved."
        )
        return 2

    if len(matched) > 1:
        names = ", ".join(i for i, _ in matched if i != expected_id)
        ui.warn(
            f"AMBIGUOUS: the card in the reader matches {expected_id} - and also {names}. The "
            "evidence available cannot tell them apart."
        )
        ui.info(
            "Every field that agreed identifies the image or the format, not the card. "
            f"`fl.py cards label --card {expected_id}` proposes the marker file that would."
        )
        return 2

    ui.ok(f"The card in the reader is {expected_id} ({verdict['strength']}).")
    ui.kv(
        {
            "agreed on": ", ".join(verdict["agreed"]),
            "not recorded": ", ".join(verdict["notRecorded"]) or "-",
            "contents": (expected.get("contents") or {}).get("summary", "?"),
            "handling": expected.get("handling", "-"),
        }
    )
    if verdict["strength"] != "conclusive":
        ui.info(
            "Consistent, not conclusive: nothing that agreed is unique to a physical card. "
            f"`fl.py cards label --card {expected_id}` proposes the one field that would be."
        )
    return 0


def record(*, card_id: str, kind: str, where: str, why: str) -> int:
    """Record that a card has moved. The only way position enters the register.

    ``why`` is required and refused when empty. A move with no reason attached is how a
    register becomes a list of assertions nobody can audit, and this is the cheapest possible
    place to stop that.
    """
    if not why.strip():
        raise HarnessError(
            "--why is required: say what the move was for.",
            exit_code=1,
            remedy="e.g. --why 'flashing the M2.5 acceptance image'",
        )
    data = load()
    entry = card(data, card_id)

    # Two cards cannot both be in the reader, and a register that lets them is a register that
    # will hand the flash script the wrong target.
    if kind == "reader":
        for other in data.get("cards", []):
            if other is not entry and (other.get("location") or {}).get("kind") == "reader":
                raise HarnessError(
                    f"The register already has {other.get('id')!r} in the reader.",
                    exit_code=1,
                    remedy=f"Record where {other.get('id')} went first.",
                )

    now = utcnow()
    previous = entry.get("location") or {}
    entry["location"] = {"kind": kind, "where": where, "sinceUtc": now, "statedBy": "operator"}
    entry.setdefault("history", []).append(
        {"utc": now, "from": previous.get("where"), "kind": kind, "where": where, "why": why}
    )
    save(data)
    ui.ok(f"{card_id}: {previous.get('where', '?')} -> {where}")

    if kind == "reader":
        ui.info(f"Confirm it with `fl.py cards check`, or capture identity with `fl.py cards identify --card {card_id}`.")
    return 0


def marker_text(entry: dict[str, Any]) -> str:
    """The exact bytes :func:`label` writes. Pure, so it can be shown without writing it."""
    contents = entry.get("contents") or {}
    return (
        "FrameLink card register\n"
        f"card:      {entry.get('id')}\n"
        f"title:     {entry.get('title', '')}\n"
        f"contents:  {contents.get('summary', '?')}\n"
        f"handling:  {entry.get('handling', '-')}\n"
        f"register:  tools/harness/cards.json in the FrameLink repository\n"
        f"written:   {utcnow()} by tools/harness/fl.py cards label\n"
        "\n"
        "This file names the card it is stored on. Nothing reads it automatically; it is here\n"
        "so a person, or `fl.py cards check`, can identify this card without a register lookup.\n"
        "If you move this card, record the move in the register.\n"
    )


def label(*, card_id: str, write: bool = False) -> int:
    """Propose - and only with ``write=True``, perform - the one write this module can do.

    The default prints the destination and the exact bytes and stops, because whether a card
    gets marked is the operator's decision and not the harness's. ``write=True`` still refuses
    unless the card in the reader already matches the register entry being named: labelling a
    card you cannot identify writes a confident lie onto it, which is strictly worse than
    leaving it blank.
    """
    data = load()
    entry = card(data, card_id)
    disks = probe()
    disk, how = reader_disk(data, disks)
    if disk is None:
        raise HarnessError(f"No reader to label a card in: {how}", exit_code=3)

    volumes = [
        p
        for p in (disk.get("partitions") or [])
        if p.get("driveLetter") and (p.get("filesystem") or "") in WRITABLE_FILESYSTEMS
    ]
    if not volumes:
        raise HarnessError(
            "No FAT-family volume on the card in the reader to write the marker into.",
            exit_code=3,
            remedy=(
                "A flashed Raspberry Pi OS card presents its bootfs partition to Windows; an "
                "unflashed factory card presents its exFAT partition. Neither is present here, "
                "so there is nowhere to put the file."
            ),
        )
    target = Path(f"{volumes[0]['driveLetter']}:/{MARKER_FILENAME}")
    body = marker_text(entry)

    ui.step(f"Marker file for card {card_id}")
    ui.kv(
        {
            "destination": str(target),
            "volume": f"{volumes[0].get('filesystem')} label={volumes[0].get('label') or '(none)'}",
            "bytes": str(len(body.encode("utf-8"))),
            "existing": "yes, will be overwritten" if target.exists() else "no",
        }
    )
    ui.block(MARKER_FILENAME, body)

    if not write:
        ui.info("Nothing was written. Add --write to write it.")
        ui.info("Cost: one small text file in the boot partition. Nothing on Raspberry Pi OS")
        ui.info("reads unrecognised files there, and the volume label is left alone.")
        ui.info("It does NOT survive a flash: writing an image overwrites the whole card.")
        return 0

    verdict = compare(entry.get("fingerprint"), fingerprint(disk))
    if not verdict["matches"]:
        ui.abort(
            f"Refusing to write: the card in the reader does not match the register's {card_id!r}.\n"
            + (
                "\n".join(
                    f"{d['field']}: register {d['register']!r}, reader {d['observed']!r}"
                    for d in verdict["differed"]
                )
                or "no recorded fingerprint to check against"
            )
        )
        return 2

    target.write_text(body, encoding="utf-8", newline="\r\n")
    ui.ok(f"wrote {target}")
    ui.info(f"Re-capture it into the register with `fl.py cards identify --card {card_id}`.")
    return 0
