"""Generate EdgeDock's multi-size Windows icon using only Python's standard library."""

from __future__ import annotations

import binascii
import struct
import zlib
from pathlib import Path


SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SCALE = 4
PURPLE = (96, 70, 184, 255)
PURPLE_LIGHT = (155, 124, 255, 255)
PANEL = (248, 246, 255, 255)


def rounded_rectangle(pixels, canvas, box, radius, color):
    left, top, right, bottom = box
    for y in range(top, bottom):
        for x in range(left, right):
            dx = max(left + radius - x, 0, x - (right - radius - 1))
            dy = max(top + radius - y, 0, y - (bottom - radius - 1))
            if dx * dx + dy * dy <= radius * radius:
                pixels[y * canvas + x] = color


def downsample(pixels, source_size, target_size):
    output = []
    area = SCALE * SCALE
    for y in range(target_size):
        for x in range(target_size):
            samples = [pixels[(y * SCALE + sy) * source_size + x * SCALE + sx]
                       for sy in range(SCALE) for sx in range(SCALE)]
            output.append(tuple(sum(sample[channel] for sample in samples) // area
                                for channel in range(4)))
    return output


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data) & 0xFFFFFFFF)


def render_png(size):
    canvas = size * SCALE
    pixels = [(0, 0, 0, 0)] * (canvas * canvas)
    margin = max(SCALE, round(size * 0.07) * SCALE)
    radius = max(2 * SCALE, round(size * 0.22) * SCALE)
    rounded_rectangle(pixels, canvas, (margin, margin, canvas - margin, canvas - margin), radius, PURPLE)

    # A simple four-pane dashboard, matching the app's ViewAll title glyph.
    inset = max(3, round(size * 0.23)) * SCALE
    gap = max(1, round(size * 0.07)) * SCALE
    inner_right = canvas - inset
    inner_bottom = canvas - inset
    pane_width = (inner_right - inset - gap) // 2
    pane_height = (inner_bottom - inset - gap) // 2
    pane_radius = max(SCALE, round(size * 0.045) * SCALE)
    panes = (
        (inset, inset, inset + pane_width, inset + pane_height),
        (inset + pane_width + gap, inset, inner_right, inset + pane_height),
        (inset, inset + pane_height + gap, inset + pane_width, inner_bottom),
        (inset + pane_width + gap, inset + pane_height + gap, inner_right, inner_bottom),
    )
    for index, pane in enumerate(panes):
        rounded_rectangle(pixels, canvas, pane, pane_radius, PANEL if index != 3 else PURPLE_LIGHT)

    pixels = downsample(pixels, canvas, size)
    raw = b"".join(b"\x00" + bytes(channel for pixel in pixels[y * size:(y + 1) * size] for channel in pixel)
                   for y in range(size))
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", header) + png_chunk(b"IDAT", zlib.compress(raw, 9)) + png_chunk(b"IEND", b"")


def main():
    images = [render_png(size) for size in SIZES]
    header_size = 6 + 16 * len(images)
    entries = []
    offset = header_size
    for size, image in zip(SIZES, images):
        encoded_size = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", encoded_size, encoded_size, 0, 0, 1, 32, len(image), offset))
        offset += len(image)

    destination = Path(__file__).resolve().parents[1] / "src" / "EdgeDock" / "Assets" / "EdgeDock.ico"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(images))
    print(f"Generated {destination} with {len(images)} sizes: {', '.join(map(str, SIZES))}")


if __name__ == "__main__":
    main()
