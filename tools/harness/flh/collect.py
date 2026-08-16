"""Screenshot and journal tail from the mule.

version2.md section 3.6 fixes the diagnostics allowlist at exactly these two: *screenshot* and
*journal tail*. Everything else already streams as telemetry, so this module is the whole
remote-diagnostic surface - which is why both capture paths are implemented properly
rather than one being left as a stub.

The screenshot method, corrected
--------------------------------
The method is **not** in ``docs/12-systemd-and-reliability.md`` - that guide has no
screenshot content at all. v1 captured screenshots in
``docs/8-webrtc-validation.md`` step 7, in the soak-test loop:

    WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 grim <path>.png

and ``reference/v1-state-inventory.txt`` line 226 confirms ``grim 1.4.0+ds-2+b2`` was
installed on the v1 frame. Guide 8 also calls grim optional ("Optionally, install grim"),
so it was never a base-image package.

That matters for v2, because two things changed:

1. **The mule is now bare Raspberry Pi OS Lite Trixie.** grim is almost certainly not
   installed, and installing it is a system mutation this harness must not perform
   unasked (CLAUDE.md section 1.8).
2. **The agent's console stage writes directly to /dev/tty8** before any graphical stack
   exists (section 2.7). grim is a Wayland client; it cannot see a console at all. Screenshotting
   the console stage - the thing M0 most needs to see - is inherently a framebuffer read.

So this module tries both, in the order that matches what is likely to be on screen, and
when neither works it reports *which* precondition failed rather than just "no screenshot":

* **grim** - only if a Wayland session is actually present.
* **framebuffer** - ``/dev/fb0`` read straight off the device, converted to PNG on the
  workstation by :mod:`flh.png`. Needs nothing installed on the mule, works for the
  console stage, and is therefore the primary path for a bare frame.

One caveat that cannot be resolved without a live panel: v1's kernel command line carries
``fbcon=rotate:1`` and labwc applies a 270-degree output transform, so a framebuffer grab
may come back rotated relative to what a person sees. ``--rotate`` is provided for that;
the correct default is unknown until someone compares a capture with the panel.
"""

from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import png, progress, ssh, ui
from .config import RUNS_DIR, UNIT_NAME, ElevationError, HarnessError

#: Cap on a single framebuffer read. 1280x800x4 is 4 MB; this leaves generous headroom for
#: a larger panel while still refusing to stream something pathological over the session.
MAX_FB_BYTES = 64 * 1024 * 1024


def run_dir(label: str = "collect") -> Path:
    """A fresh timestamped directory under the gitignored runs/ tree."""
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    path = RUNS_DIR / f"{stamp}-{label}"
    path.mkdir(parents=True, exist_ok=True)
    return path


# --------------------------------------------------------------------------
# journal
# --------------------------------------------------------------------------
def journal(mule: ssh.Mule, *, unit: str | None = UNIT_NAME, lines: int = 200,
            boot: bool = False, since: str | None = None) -> str:
    """Tail the journal. ``unit=None`` takes the whole system journal.

    Elevation is used unconditionally because whether the login user can read the system
    journal depends on ``adm``/``systemd-journal`` group membership that a reflash may not
    reproduce, and a tail that silently returns nothing is worse than one that costs a sudo.
    It goes through :meth:`ssh.Mule.run_privileged`, which answers the password on stdin: a
    stock Raspberry Pi OS Lite image has **no** NOPASSWD drop-in, so the plain ``sudo`` this
    used to run would have failed here with ``sudo: a password is required``. Guide 12 step 5
    made the journal persistent and capped it at 64 MB, so a tail after a crash still has
    history - the whole reason that decision was made.
    """
    parts = ["journalctl", "--no-pager", "-o", "short-iso"]
    if unit:
        parts += ["-u", unit]
    if boot:
        parts += ["-b"]
    if since:
        parts += ["--since", f"'{since}'"]
    parts += ["-n", str(int(lines))]
    result = mule.run_privileged(" ".join(parts), timeout=90)
    if not result.ok:
        raise HarnessError(
            f"journalctl failed (exit {result.exit_status}): {(result.stderr or result.stdout).strip()}",
            exit_code=5,
        )
    return result.stdout


# --------------------------------------------------------------------------
# screenshot
# --------------------------------------------------------------------------
def _wayland_probe(mule: ssh.Mule) -> dict[str, str]:
    """What the Wayland screenshot path needs, and whether each piece is there."""
    result = mule.run(
        'echo "uid=$(id -u)"; '
        'echo "grim=$(command -v grim || echo MISSING)"; '
        'echo "socket=$(ls /run/user/$(id -u)/wayland-* 2>/dev/null | head -1 || echo MISSING)"; '
        'echo "fb0=$(test -c /dev/fb0 && echo present || echo MISSING)"'
    )
    facts: dict[str, str] = {}
    for line in result.stdout.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            facts[key.strip()] = value.strip()
    return facts


def screenshot_grim(mule: ssh.Mule, destination: Path, facts: dict[str, str]) -> dict[str, Any]:
    """Capture through grim. Writes PNG bytes straight from the remote stdout."""
    uid = facts.get("uid", "1000")
    socket = Path(facts.get("socket", "")).name or "wayland-0"
    command = (
        f"XDG_RUNTIME_DIR=/run/user/{uid} WAYLAND_DISPLAY={socket} grim -"
    )
    status, data, err = mule.run_binary(command, timeout=60)
    if status != 0 or not data.startswith(b"\x89PNG"):
        raise HarnessError(
            f"grim failed (exit {status}): {err.strip() or 'no PNG on stdout'}",
            exit_code=5,
        )
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    return {"method": "grim", "bytes": len(data), "waylandDisplay": socket}


def screenshot_framebuffer(mule: ssh.Mule, destination: Path, *, pixel_format: str = "bgrx",
                           rotate: int = 0) -> dict[str, Any]:
    """Capture by reading /dev/fb0 and encoding the PNG locally.

    Geometry comes from sysfs rather than being assumed: ``virtual_size`` for the
    dimensions, ``bits_per_pixel`` for the depth, and ``stride`` for the real bytes per
    line - which is usually larger than ``width * bpp/8`` because scanlines are padded, and
    ignoring it produces a sheared image.
    """
    geometry = mule.run(
        "cat /sys/class/graphics/fb0/virtual_size 2>/dev/null; "
        "cat /sys/class/graphics/fb0/bits_per_pixel 2>/dev/null; "
        "cat /sys/class/graphics/fb0/stride 2>/dev/null || echo 0"
    )
    fields = [line.strip() for line in geometry.stdout.splitlines() if line.strip()]
    if len(fields) < 2 or "," not in fields[0]:
        raise HarnessError(
            "Cannot read framebuffer geometry from /sys/class/graphics/fb0 - no framebuffer device.",
            exit_code=5,
            remedy=(
                "The frame has no /dev/fb0. On a Pi 5 that means the vc4-kms-v3d overlay is "
                "not loaded, which is itself the finding."
            ),
        )

    width, height = (int(v) for v in fields[0].split(",", 1))
    bpp = int(fields[1])
    stride = int(fields[2]) if len(fields) > 2 and fields[2].isdigit() and int(fields[2]) > 0 else width * bpp // 8
    expected = stride * height
    if expected > MAX_FB_BYTES:
        raise HarnessError(f"framebuffer is {expected} bytes, over the {MAX_FB_BYTES} cap", exit_code=5)

    ui.info(f"framebuffer {width}x{height} {bpp}bpp stride={stride} ({expected:,} bytes)")
    # `head -c` rather than `dd`: reading a character device can return short blocks, and
    # dd without iflag=fullblock silently stops at the first one, producing a truncated
    # image that looks like a half-drawn screen rather than a truncated read. head -c keeps
    # reading until it has the byte count asked for.
    status, raw, err = mule.run_privileged_binary(f"head -c {expected} /dev/fb0", timeout=120)
    if status != 0 or len(raw) < expected:
        raise HarnessError(
            f"reading /dev/fb0 returned {len(raw)} of {expected} bytes (exit {status}) {err.strip()}",
            exit_code=5,
        )

    rgb = png.framebuffer_to_rgb(
        raw, width, height, bits_per_pixel=bpp, stride=stride, pixel_format=pixel_format
    )
    if rotate:
        rgb, width, height = _rotate(rgb, width, height, rotate)
    size = png.write_rgb(destination, width, height, rgb)
    return {
        "method": "framebuffer",
        "bytes": size,
        "geometry": f"{width}x{height}",
        "bitsPerPixel": bpp,
        "stride": stride,
        "pixelFormat": pixel_format,
        "rotate": rotate,
    }


def _rotate(rgb: bytes, width: int, height: int, degrees: int) -> tuple[bytes, int, int]:
    """Rotate packed RGB by 90, 180 or 270 degrees clockwise."""
    if degrees % 90 or degrees % 360 == 0:
        return rgb, width, height
    degrees %= 360
    out = bytearray(len(rgb))
    if degrees == 180:
        for y in range(height):
            for x in range(width):
                src = (y * width + x) * 3
                dst = ((height - 1 - y) * width + (width - 1 - x)) * 3
                out[dst : dst + 3] = rgb[src : src + 3]
        return bytes(out), width, height
    new_w, new_h = height, width
    for y in range(height):
        for x in range(width):
            src = (y * width + x) * 3
            if degrees == 90:
                nx, ny = height - 1 - y, x
            else:  # 270
                nx, ny = y, width - 1 - x
            dst = (ny * new_w + nx) * 3
            out[dst : dst + 3] = rgb[src : src + 3]
    return bytes(out), new_w, new_h


def screenshot(mule: ssh.Mule, destination: Path, *, method: str = "auto",
               pixel_format: str = "bgrx", rotate: int = 0) -> dict[str, Any]:
    """Capture the screen, choosing the path that can actually work right now."""
    facts = _wayland_probe(mule)
    ui.info(
        f"probe: grim={facts.get('grim', '?')} wayland-socket={facts.get('socket', '?')} "
        f"fb0={facts.get('fb0', '?')}"
    )

    attempts: list[str] = []

    wayland_ready = facts.get("grim", "MISSING") != "MISSING" and facts.get("socket", "MISSING") != "MISSING"
    if method in ("auto", "grim"):
        if wayland_ready or method == "grim":
            try:
                return screenshot_grim(mule, destination, facts)
            except HarnessError as exc:
                attempts.append(f"grim: {exc}")
        else:
            attempts.append(
                "grim: skipped - "
                + ("grim is not installed" if facts.get("grim") == "MISSING" else "no Wayland socket")
            )

    if method in ("auto", "framebuffer"):
        if facts.get("fb0") == "present" or method == "framebuffer":
            try:
                return screenshot_framebuffer(mule, destination, pixel_format=pixel_format, rotate=rotate)
            except ElevationError:
                # Not a screenshot problem, and no second path can route around it. Letting
                # it fall into `attempts` would end in "No screenshot path succeeded" with a
                # remedy about the vc4-kms-v3d overlay, which is the wrong subject entirely.
                raise
            except HarnessError as exc:
                attempts.append(f"framebuffer: {exc}")
        else:
            attempts.append("framebuffer: skipped - /dev/fb0 does not exist")

    raise HarnessError(
        "No screenshot path succeeded.\n  " + "\n  ".join(attempts),
        exit_code=5,
        remedy=(
            "On a bare frame with no compositor the framebuffer path is the one that should "
            "work; a missing /dev/fb0 on a Pi 5 points at the vc4-kms-v3d overlay not being "
            "loaded. grim needs both `apt install grim` and a live Wayland session - neither "
            "is installed by the harness, because that is a system mutation (CLAUDE.md section 1.8)."
        ),
    )


# --------------------------------------------------------------------------
# entry point
# --------------------------------------------------------------------------
def collect(*, unit: str | None = UNIT_NAME, lines: int = 200, boot: bool = False,
            since: str | None = None, want_screenshot: bool = True, want_journal: bool = True,
            method: str = "auto", pixel_format: str = "bgrx", rotate: int = 0) -> dict[str, Any]:
    """Gather both allowlisted diagnostics into one timestamped run directory."""
    destination = run_dir("collect")
    result: dict[str, Any] = {"directory": str(destination), "screenshot": None, "journal": None}
    problems: list[str] = []

    with ssh.connect() as mule:
        ui.step(f"Collecting diagnostics from {mule.user}@{mule.host} into {destination.name}")

        if want_journal:
            try:
                text = journal(mule, unit=unit, lines=lines, boot=boot, since=since)
                name = f"journal-{unit or 'system'}.txt".replace("/", "_")
                path = destination / name
                path.write_text(text, encoding="utf-8")
                captured = len(text.splitlines())
                result["journal"] = {"path": str(path), "unit": unit or "system", "lines": captured}
                ui.ok(f"journal: {captured} lines -> {path.name}")
                if captured == 0:
                    ui.warn(f"journal is empty - {unit or 'the system journal'} has logged nothing")
            except ElevationError:
                # One diagnostic failing must not cost the other, which is why the rest of
                # this is absorbed. An elevation failure is different in kind: it fails both
                # for one reason, and reporting it twice as two problems would read as two.
                raise
            except HarnessError as exc:
                problems.append(str(exc))
                ui.fail(f"journal: {exc}")

        if want_screenshot:
            try:
                path = destination / "screenshot.png"
                meta = screenshot(mule, path, method=method, pixel_format=pixel_format, rotate=rotate)
                meta["path"] = str(path)
                result["screenshot"] = meta
                ui.ok(f"screenshot: {meta['method']} {meta['bytes']:,} bytes -> {path.name}")
            except ElevationError:
                raise
            except HarnessError as exc:
                problems.append(str(exc))
                ui.fail(f"screenshot: {exc}")

    progress.bump("collections")
    progress.set_artifact(
        "lastCollection",
        {
            "directory": str(destination).replace("\\", "/"),
            "capturedUtc": progress.utcnow(),
            "screenshot": result["screenshot"],
            "journal": result["journal"],
        },
    )

    got_screenshot = result["screenshot"] is not None or not want_screenshot
    got_journal = result["journal"] is not None or not want_journal
    if got_screenshot and got_journal:
        progress.prove(
            "collect",
            by="fl.py collect",
            detail=(
                f"screenshot via {result['screenshot']['method'] if result['screenshot'] else 'n/a'}, "
                f"journal {result['journal']['lines'] if result['journal'] else 0} lines"
            ),
        )
    else:
        progress.mark("collect", "failed", detail="; ".join(problems)[:500])

    result["problems"] = problems
    return result
