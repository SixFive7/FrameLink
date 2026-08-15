"""Minimal PNG writer, used to turn raw framebuffer bytes into a viewable image.

Why hand-rolled rather than Pillow: the framebuffer path exists precisely for the state
where the mule is a bare Trixie install with no compositor, and adding a workstation
dependency (CLAUDE.md section 1.3 discourages new installs, and every one becomes an inherited
persistent mutation) to decode four bytes per pixel is a poor trade. PNG's baseline
encoding is a zlib stream of filter-0 scanlines and three chunks; ``zlib`` and ``struct``
are both in the standard library, so this is about forty lines and no install.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path


def _chunk(tag: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + tag
        + payload
        + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
    )


def write_rgb(path: Path, width: int, height: int, rgb: bytes) -> int:
    """Write ``width * height * 3`` bytes of packed RGB as an 8-bit truecolour PNG.

    Returns the file size. Raises ValueError if the buffer length does not match the
    declared geometry, because a silently mis-sized image is worse than no image - it
    would be read as a diagnosis of the frame rather than of the capture.
    """
    expected = width * height * 3
    if len(rgb) != expected:
        raise ValueError(f"expected {expected} bytes for {width}x{height} RGB, got {len(rgb)}")

    raw = bytearray()
    stride = width * 3
    for y in range(height):
        raw.append(0)  # filter type 0 (None) - no prediction, smallest possible encoder
        raw += rgb[y * stride : (y + 1) * stride]

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)  # 8-bit, colour type 2 (RGB)
    data = (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", ihdr)
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 6))
        + _chunk(b"IEND", b"")
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return len(data)


def framebuffer_to_rgb(
    raw: bytes, width: int, height: int, *, bits_per_pixel: int, stride: int, pixel_format: str
) -> bytes:
    """Convert a raw Linux framebuffer dump to packed RGB.

    ``stride`` is the bytes-per-line the kernel reports, which is often larger than
    ``width * bytes_per_pixel`` because scanlines are padded for alignment. Ignoring it is
    the classic way to produce a diagonally sheared screenshot, so the padding is skipped
    per row rather than assumed away.

    ``pixel_format`` covers what a Pi actually produces: ``bgrx`` (the DRM fbdev emulation
    default - XRGB8888 stored little-endian, so the bytes arrive B, G, R, X), ``rgbx``, and
    ``rgb565`` for 16-bit modes.
    """
    out = bytearray(width * height * 3)
    fmt = pixel_format.lower()

    if bits_per_pixel == 32:
        if fmt not in ("bgrx", "rgbx"):
            raise ValueError(f"unsupported 32bpp pixel format {pixel_format!r} (expected bgrx or rgbx)")
        r_off, g_off, b_off = (2, 1, 0) if fmt == "bgrx" else (0, 1, 2)
        for y in range(height):
            row = y * stride
            dst = y * width * 3
            for x in range(width):
                src = row + x * 4
                out[dst] = raw[src + r_off]
                out[dst + 1] = raw[src + g_off]
                out[dst + 2] = raw[src + b_off]
                dst += 3
        return bytes(out)

    if bits_per_pixel == 16:
        for y in range(height):
            row = y * stride
            dst = y * width * 3
            for x in range(width):
                src = row + x * 2
                pixel = raw[src] | (raw[src + 1] << 8)  # little-endian RGB565
                # Replicate the high bits into the low ones so full-scale stays full-scale.
                out[dst] = ((pixel >> 11) & 0x1F) * 255 // 31
                out[dst + 1] = ((pixel >> 5) & 0x3F) * 255 // 63
                out[dst + 2] = (pixel & 0x1F) * 255 // 31
                dst += 3
        return bytes(out)

    raise ValueError(f"unsupported framebuffer depth: {bits_per_pixel} bpp")
