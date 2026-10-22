"""Generate the small repeatable dark metal texture set for real sliced faces."""
from pathlib import Path
import math
import struct
import zlib


def png(path: Path, pixel):
    size = 64
    raw = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(size)) for y in range(size))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


if __name__ == "__main__":
    target = Path(__file__).resolve().parents[1] / "Assets" / "Textures"
    def metal(x, y):
        value = 30 + int(4 * math.sin(x * .9 + y * .2) + 2 * math.cos(y * 1.3))
        return value, int(value * .82), int(value * .70), 255
    png(target / "Purr2D2_CutSurface_Diffuse.png", metal)
    png(target / "Purr2D2_CutSurface_ORM.png", lambda x, y: (255, 178, 200, 255))
