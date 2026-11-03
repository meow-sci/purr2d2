#!/usr/bin/env python3
"""Author deterministic opaque end-grain maps for centered per-section wood-cap UVs.

Run with Python's standard library; no game or external image library is needed.
The usable trunk radius is .47 around UV (.5,.5). Geometry owns that mapping.
"""
from pathlib import Path
import argparse
import math
import struct
import zlib


SIZE = 512
PREFIX = 'Purr2D2_WoodCutSurface'
USABLE_RADIUS = .47


def png(size, pixels):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    stride = size * 4
    scanlines = b''.join(b'\0' + pixels[row * stride:(row + 1) * stride] for row in range(size))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(scanlines, 9)) + chunk(b'IEND', b''))


def clamp(value):
    return max(0, min(255, round(value)))


def noise(x, y):
    value = (x * 0x1f123bb5 + y * 0x5f356495 + 0x9e3779b9) & 0xffffffff
    value = ((value ^ (value >> 16)) * 0x7feb352d) & 0xffffffff
    value = ((value ^ (value >> 15)) * 0x846ca68b) & 0xffffffff
    return ((value ^ (value >> 16)) & 0xffff) / 32767.5 - 1


def textures():
    diffuse, orm = bytearray(), bytearray()
    heights = []
    for y in range(SIZE):
        for x in range(SIZE):
            px = ((x + .5) / SIZE - .5) / USABLE_RADIUS
            py = ((y + .5) / SIZE - .5) / USABLE_RADIUS
            radius, angle = math.hypot(px, py), math.atan2(py, px)
            warp = (.011 * math.sin(angle * 3 + radius * 4) + .006 * math.sin(angle * 7 - radius * 9)
                    + .003 * math.sin(angle * 13 + radius * 3)) * min(radius * 3, 1)
            growth = radius + warp
            phase = growth * 27 + .16 * math.sin(growth * 11)
            latewood = (.5 + .5 * math.cos(phase * math.tau)) ** 12
            rays = max(0, math.cos(angle * 53 + .6 * math.sin(radius * 18))) ** 45
            heart = 1 - max(0, min(1, (radius - .18) / .66))
            heart = heart * heart * (3 - 2 * heart)
            pith = math.exp(-((radius / .027) ** 2))
            fine = noise(x, y)
            grain = fine * 2.2 + rays * 3.5 - latewood * 25 - pith * 13
            diffuse.extend((clamp(201 - heart * 28 + grain), clamp(157 - heart * 31 + grain * .85),
                            clamp(104 - heart * 28 + grain * .64), 255))
            # Fresh end grain remains rough dielectric wood, with no fake metal.
            orm.extend((255, clamp(220 + latewood * 13 + fine * 2), 0, 255))
            heights.append(latewood * .10 + rays * .025 + fine * .006)

    normal = bytearray()
    for y in range(SIZE):
        for x in range(SIZE):
            left, right = heights[y * SIZE + max(0, x - 1)], heights[y * SIZE + min(SIZE - 1, x + 1)]
            down, up = heights[max(0, y - 1) * SIZE + x], heights[min(SIZE - 1, y + 1) * SIZE + x]
            nx, ny = (left - right) * .7, (down - up) * .7
            length = math.sqrt(nx * nx + ny * ny + 1)
            normal.extend((clamp(127.5 + 127.5 * nx / length), clamp(127.5 + 127.5 * ny / length),
                           clamp(127.5 + 127.5 / length), 255))
    return {f'{PREFIX}_{kind}.png': png(SIZE, pixels)
            for kind, pixels in (('Diffuse', diffuse), ('Normal', normal), ('ORM', orm))}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, default=Path(__file__).resolve().parents[1] / 'Assets/Textures')
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for name, data in textures().items():
        path = args.output_dir / name
        path.write_bytes(data)
        print(f'Wrote {path} ({len(data):,} bytes)')
