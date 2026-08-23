"""Excessive, bounded, off-frame logging of everything the harness did.

Why this exists as a feature rather than as a flag on one subcommand
-------------------------------------------------------------------
The harness's console transcript is the only record of most runs, and it is deliberately
terse: :mod:`flh.ui` prints what an operator needs to *decide* something, not what an
investigator needs to *reconstruct* something. Those are different jobs, and every time
they have been made the same job the answer has been worse at both.

The operation that made this urgent is the array firmware flash, because it is the one
operation on this project whose forensics cannot be recreated: the array is written once,
the bytes that went to it are gone, and the frame's own journal keeps roughly **five days**
of history. An investigation that starts a week later has nothing. But nothing about that
argument is specific to the flash, which is why this is a harness-wide facility that the
flash merely turns on by default.

Three properties, and each one is a reaction to a measured failure
------------------------------------------------------------------
1. **It lives on the workstation, never on the frame.** Not one byte of this is written to
   the frame, to the frame's journal, or to the frame's card. That is not tidiness. The
   agent already learned this the expensive way: a Kiosk flood erased the frame's own
   forensic history, and ``ChildOutputBudget`` now caps a supervised child at 60 lines per
   10 minutes precisely so that a chatty child can never again push the interesting part of
   the journal out of the ring buffer. A debug mode that wrote its detail *into* the journal
   would reintroduce that failure with a new author, so this one cannot: it has no code path
   that writes to the frame at all.

2. **It is bounded three ways, and the bound never truncates the run you are reading.**
   A single captured stream is clipped at :data:`MAX_STREAM_BYTES` with the middle removed
   and the removal stated in place, so the head and the tail — which is where the useful
   text always is — both survive. A whole run's log is capped at :data:`MAX_RUN_BYTES`.
   Across runs, :func:`sweep` evicts whole *older* run directories until the total is under
   :data:`MAX_TOTAL_BYTES`, oldest first. Evicting a whole old run rather than trimming the
   live one is the important half: a cap that eats the tail of the current log deletes the
   failure you turned it on to find.

3. **Secrets are scrubbed on the way in, not on the way out.** ``FL_PW`` and
   ``FL_HA_TOKEN`` are read from the environment once and every record is scanned for them
   before it is written. The harness already keeps the password off command lines entirely
   (it goes on ``sudo``'s stdin — see :mod:`flh.ssh`), so this is a second net under a
   design that should never drop anything into it, and it is cheap enough to be
   unconditional. CLAUDE.md section 1.2 is absolute and a debug facility is exactly the
   kind of thing that quietly violates it.

What it captures
----------------
Every remote command with its verbatim text, exit status, both streams and wall-clock
duration; every gate a command evaluated, with the two values that were compared and which
way it went; every artifact written; and, for the flash, the frame's own journal window
pulled back to the workstation so it survives the journal's retention.

How a person gets at it
-----------------------
It is a plain text file next to the run's other artifacts, and its path is printed at the
end of the run. ``fl.py status`` names the newest one. There is no viewer, no format and no
tooling, because the thing that has to work in a year is ``grep``.

Turning it on
-------------
Any of, in precedence order:

* ``--debug`` on any subcommand.
* ``FL_DEBUG=1`` in the environment (also ``true``, ``yes``, ``on``; case-insensitive).
* Automatically, for operations that declare themselves unrepeatable — currently
  ``fl.py array flash``, which turns it on and cannot turn it off.

``--no-debug`` suppresses it everywhere except where it is forced, and a forced log says so
in its own header so nobody wonders why it appeared.
"""

from __future__ import annotations

import os
import shutil
import sys
import time
import traceback
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .config import RUNS_DIR

#: Longest a single captured stream may be before its middle is removed. Generous, because
#: the whole point is excess: dfu-util's progress bar, a full journal window and an apt
#: transcript all fit comfortably, and the clip only fires on genuine runaways.
MAX_STREAM_BYTES = 256 * 1024

#: Longest one run's debug log may grow. A flash run measures in tens of kilobytes, so this
#: is roughly two hundred times a normal run and exists to bound a loop that has gone wrong.
MAX_RUN_BYTES = 16 * 1024 * 1024

#: Total budget across every retained debug log. Whole older runs are evicted to stay under
#: it — see the module docstring for why eviction is by run and not by truncation.
MAX_TOTAL_BYTES = 256 * 1024 * 1024

#: Never evict below this many runs however large they are, so a single enormous run cannot
#: leave the directory empty and a person always has the last few sessions to compare.
MIN_RETAINED_RUNS = 5

#: What is scrubbed from every record, by environment variable name. Values only — the
#: names themselves are safe and are what a reader needs to see.
SECRET_VARIABLES = ("FL_PW", "FL_HA_TOKEN")

#: What replaces a secret that reached a record anyway.
REDACTION = "<redacted:{name}>"

_TRUE = ("1", "true", "yes", "on")

_current: "DebugLog | None" = None


def wanted(flag: bool | None = None) -> bool:
    """Whether debug logging is on, from the flag then the environment.

    ``flag`` is the tri-state ``--debug`` / ``--no-debug`` / unset, and it wins outright
    when it is not None so an explicit ``--no-debug`` is not overridden by a variable
    somebody exported last week and forgot.
    """
    if flag is not None:
        return flag
    return os.environ.get("FL_DEBUG", "").strip().lower() in _TRUE


def current() -> "DebugLog | None":
    """The log this run is writing to, or None.

    A module-level accessor rather than an argument threaded through every call, because
    the thing that most needs recording is :meth:`flh.ssh.Mule.run`, and giving every one of
    its dozens of callers a parameter they do not care about is how a facility like this
    ends up half-wired and silently missing the calls that mattered.
    """
    return _current


class DebugLog:
    """One run's debug log. Append-only, bounded, and never on the frame."""

    def __init__(self, directory: Path, *, command: str, forced: bool = False):
        self.directory = directory
        self.path = directory / "debug.log"
        self.command = command
        self.forced = forced
        self.started = time.monotonic()
        self._written = 0
        self._clipped = 0
        self._sequence = 0
        self._secrets = [
            (name, value)
            for name in SECRET_VARIABLES
            if (value := os.environ.get(name)) and len(value) >= 8
        ]

        directory.mkdir(parents=True, exist_ok=True)
        self._handle = self.path.open("a", encoding="utf-8", errors="replace")
        self._header()

    # --- lifecycle ---------------------------------------------------------
    def _header(self) -> None:
        self._raw(
            "\n".join(
                (
                    "=" * 78,
                    f"FrameLink harness debug log - {self.command}",
                    f"opened            {datetime.now(UTC).isoformat()}",
                    f"argv              {' '.join(sys.argv)}",
                    f"cwd               {Path.cwd()}",
                    f"python            {sys.version.split()[0]} on {sys.platform}",
                    f"turned on by      {'the operation itself; it cannot be turned off' if self.forced else 'the --debug flag or FL_DEBUG'}",
                    f"secrets scrubbed  {', '.join(name for name, _ in self._secrets) or 'none set in this environment'}",
                    "",
                    "This file is on the workstation. Nothing here is written to the frame,",
                    "to the frame's journal or to the frame's card, deliberately - see the",
                    "module docstring in flh/debuglog.py.",
                    "=" * 78,
                    "",
                )
            )
        )

    def close(self, *, outcome: str = "") -> None:
        """Write the footer and release the file. Safe to call twice."""
        if self._handle.closed:
            return
        elapsed = time.monotonic() - self.started
        self._raw(
            "\n".join(
                (
                    "",
                    "=" * 78,
                    f"closed            {datetime.now(UTC).isoformat()}",
                    f"elapsed           {elapsed:.1f} s",
                    f"records           {self._sequence}",
                    f"bytes written     {self._written}",
                    f"streams clipped   {self._clipped}",
                    f"outcome           {outcome or 'not stated'}",
                    "=" * 78,
                    "",
                )
            )
        )
        self._handle.close()

    def __enter__(self) -> "DebugLog":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if exc is not None:
            self.note("the run ended in an exception", detail="".join(traceback.format_exception(exc_type, exc, tb)))
        self.close(outcome="raised " + exc_type.__name__ if exc_type else "completed")

    # --- redaction and bounding -------------------------------------------
    def _scrub(self, text: str) -> str:
        for name, value in self._secrets:
            if value in text:
                text = text.replace(value, REDACTION.format(name=name))
        return text

    def _clip(self, text: str) -> str:
        """Keep the head and the tail; state the removal where it happened."""
        raw = text.encode("utf-8", errors="replace")
        if len(raw) <= MAX_STREAM_BYTES:
            return text
        self._clipped += 1
        keep = MAX_STREAM_BYTES // 2 - 64
        head = raw[:keep].decode("utf-8", errors="replace")
        tail = raw[-keep:].decode("utf-8", errors="replace")
        removed = len(raw) - 2 * keep
        return f"{head}\n...[{removed} bytes removed from the middle by flh.debuglog]...\n{tail}"

    def _raw(self, text: str) -> None:
        if self._handle.closed:
            return
        if self._written >= MAX_RUN_BYTES:
            return
        payload = text if text.endswith("\n") else text + "\n"
        encoded = payload.encode("utf-8", errors="replace")
        if self._written + len(encoded) > MAX_RUN_BYTES:
            payload = (
                f"...[this run's debug log reached its {MAX_RUN_BYTES} byte cap and stops here; "
                "raise MAX_RUN_BYTES in flh/debuglog.py if a run legitimately needs more]...\n"
            )
            encoded = payload.encode("utf-8")
        self._written += len(encoded)
        self._handle.write(payload)
        self._handle.flush()

    # --- the things worth recording ---------------------------------------
    def _stamp(self) -> str:
        self._sequence += 1
        return f"[{self._sequence:04d}] [{datetime.now(UTC).strftime('%H:%M:%S.%f')[:-3]}] [+{time.monotonic() - self.started:7.2f}s]"

    def note(self, message: str, *, detail: str = "") -> None:
        """A sentence about what the harness decided, with optional verbatim detail."""
        body = f"{self._stamp()} {self._scrub(message)}"
        if detail:
            body += "\n" + self._indent(self._clip(self._scrub(detail)))
        self._raw(body)

    def gate(self, name: str, passed: bool, *, expected: Any = None, observed: Any = None, why: str = "") -> None:
        """One interlock, the two values it compared, and which way it went.

        This is the record that answers "why did it refuse" a month later, and it is written
        for every gate whether it passed or not — a log that only records failures cannot
        show that a gate was *evaluated*, which is the question that matters when one turns
        out to have been skipped.
        """
        verdict = "PASS" if passed else "REFUSE"
        body = f"{self._stamp()} gate {name:<28} {verdict}"
        if expected is not None or observed is not None:
            body += f"\n    expected  {self._scrub(str(expected))}\n    observed  {self._scrub(str(observed))}"
        if why:
            body += f"\n    because   {self._scrub(why)}"
        self._raw(body)

    def command_result(
        self,
        command: str,
        *,
        exit_code: int | None,
        stdout: str = "",
        stderr: str = "",
        seconds: float | None = None,
        host: str = "",
        elevated: bool = False,
    ) -> None:
        """One remote or local command, verbatim, with both streams and its duration."""
        head = f"{self._stamp()} command{' (sudo)' if elevated else ''}{f' on {host}' if host else ''}"
        parts = [
            head,
            self._indent("$ " + self._scrub(command)),
            self._indent(f"exit {exit_code if exit_code is not None else 'unknown'}"
                         + (f"  in {seconds:.2f}s" if seconds is not None else "")),
        ]
        for label, stream in (("stdout", stdout), ("stderr", stderr)):
            text = stream.rstrip("\n")
            if text:
                parts.append(self._indent(f"--- {label} ---"))
                parts.append(self._indent(self._clip(self._scrub(text)), prefix="    | "))
            else:
                parts.append(self._indent(f"--- {label}: empty ---"))
        self._raw("\n".join(parts))

    def artifact(self, path: Path | str, *, what: str = "") -> None:
        """A file this run wrote, so the log names everything it produced."""
        self.note(f"artifact {path}" + (f" - {what}" if what else ""))

    def capture(self, name: str, text: str) -> Path:
        """Write a verbatim capture beside the log as its own file, and record it.

        Used for anything whose value is in being a whole file — a journal window, a
        ``dfu-util`` transcript — where clipping it into the log would be the wrong trade.
        The file itself is unclipped; the log records its path and its length.
        """
        path = self.directory / name
        path.write_text(text, encoding="utf-8", errors="replace")
        self.note(f"captured {len(text)} characters into {path.name}")
        return path

    @staticmethod
    def _indent(text: str, prefix: str = "    ") -> str:
        return "\n".join(prefix + line for line in text.splitlines()) or prefix

    # --- the frame's own history, brought somewhere it survives ------------
    def pull_journal(self, mule, *, since: str, unit: str = "fl-agent.service", name: str = "journal.txt") -> Path | None:
        """Copy a window of the frame's journal to the workstation.

        Journal retention on a frame is measured at roughly **five days**, so a record that
        exists only there has an expiry date that nobody is told about. This is a read, it
        runs after the operation it documents rather than during it, and its output lands
        beside the rest of the run's artifacts. It never writes anything to the frame.
        """
        try:
            result = mule.run_privileged(
                f"journalctl -u {unit} --since '{since}' -o short-iso --no-pager | tail -n 4000",
                timeout=120,
            )
        except Exception as error:  # noqa: BLE001 - a failed pull must not fail the run
            self.note(f"the journal could not be pulled back: {error}")
            return None
        text = result.stdout + (("\n--- stderr ---\n" + result.stderr) if result.stderr.strip() else "")
        return self.capture(name, text)


def open_log(command: str, *, flag: bool | None = None, forced: bool = False, directory: Path | None = None) -> DebugLog | None:
    """Start this run's debug log if anything asked for one, and install it as current."""
    global _current
    if not (forced or wanted(flag)):
        return None
    if directory is None:
        stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
        directory = RUNS_DIR / f"{stamp}-{command}"
    sweep()
    _current = DebugLog(directory, command=command, forced=forced)
    return _current


def close_log(outcome: str = "") -> None:
    """Close and uninstall this run's log, if there is one."""
    global _current
    if _current is not None:
        _current.close(outcome=outcome)
        _current = None


def sweep(*, budget: int = MAX_TOTAL_BYTES, keep: int = MIN_RETAINED_RUNS) -> list[Path]:
    """Evict whole oldest run directories until the retained total is under ``budget``.

    Returns what it removed. Only directories that actually contain a ``debug.log`` are
    candidates, so a run's screenshots and readings are never deleted by the debug
    facility's budget — this sweeps what it created and nothing else.
    """
    if not RUNS_DIR.exists():
        return []

    runs: list[tuple[float, Path, int]] = []
    for child in RUNS_DIR.iterdir():
        if not child.is_dir() or not (child / "debug.log").exists():
            continue
        size = sum(f.stat().st_size for f in child.rglob("*") if f.is_file())
        runs.append((child.stat().st_mtime, child, size))

    runs.sort(key=lambda item: item[0])
    total = sum(size for _, _, size in runs)
    removed: list[Path] = []

    while total > budget and len(runs) > keep:
        _, path, size = runs.pop(0)
        shutil.rmtree(path, ignore_errors=True)
        total -= size
        removed.append(path)

    return removed
