"""Console output for the harness.

Deliberately tiny and dependency-free. The harness runs unattended for long stretches and
its transcript is often the only record of what happened, so output is prefixed, ordered
and greppable rather than pretty. No progress spinners, no cursor tricks: a log that
survives being redirected to a file is worth more than one that looks good live.
"""

from __future__ import annotations

import os
import sys
from typing import Any


def _make_output_lossless() -> None:
    """Never let an encoding mismatch kill a run.

    The harness prints text it did not write: journal lines from the mule, compiler output
    from the container. Any of it may contain characters the console encoding cannot
    represent, and on a legacy Windows code page that raises UnicodeEncodeError from
    ``print`` - which would abort a deploy or a collection over a decorative glyph in
    somebody else's log line. ``errors="replace"`` turns that class of crash into a
    substituted character, which is the correct trade for a diagnostic tool.
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(errors="replace")
            except (ValueError, OSError):  # pragma: no cover - non-reconfigurable stream
                pass


_make_output_lossless()

_COLOR = (
    sys.stdout.isatty()
    and os.environ.get("NO_COLOR") is None
    and os.environ.get("TERM") != "dumb"
)


def _paint(code: str, text: str) -> str:
    return f"\033[{code}m{text}\033[0m" if _COLOR else text


def step(message: str) -> None:
    """A phase of work beginning."""
    print(_paint("1;34", "==>") + f" {message}", flush=True)


def info(message: str) -> None:
    print(f"    {message}", flush=True)


def ok(message: str) -> None:
    print(_paint("32", "  OK") + f"  {message}", flush=True)


def warn(message: str) -> None:
    print(_paint("33", "WARN") + f"  {message}", flush=True)


def fail(message: str) -> None:
    print(_paint("1;31", "FAIL") + f"  {message}", file=sys.stderr, flush=True)


def abort(message: str) -> None:
    """A loud stop. Used for the wrong-entity relay abort, which must not be missable."""
    bar = "!" * 72
    print(_paint("1;31", bar), file=sys.stderr, flush=True)
    for line in message.splitlines():
        print(_paint("1;31", f"!! {line}"), file=sys.stderr, flush=True)
    print(_paint("1;31", bar), file=sys.stderr, flush=True)


def kv(pairs: dict[str, Any], indent: str = "    ") -> None:
    """Aligned key/value block."""
    if not pairs:
        return
    width = max(len(k) for k in pairs)
    for key, value in pairs.items():
        print(f"{indent}{key.ljust(width)}  {value}", flush=True)


def block(title: str, body: str, indent: str = "    ") -> None:
    """Verbatim captured output, indented so it cannot be mistaken for harness output."""
    print(f"{indent}--- {title} ---", flush=True)
    for line in body.rstrip("\n").splitlines() or [""]:
        print(f"{indent}| {line}", flush=True)
    print(f"{indent}--- end {title} ---", flush=True)
