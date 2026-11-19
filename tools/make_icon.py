"""Generates assets/DacDeck.ico: three EQ sliders on a dark rounded tile.

Every size is drawn on its own (4x supersampled, then downscaled) so small sizes keep thick, crisp strokes.
Sizes <= 64 are stored as 32-bit DIBs (most compatible), 256 as PNG.

    python tools/make_icon.py            -> assets/DacDeck.ico
    python tools/make_icon.py out.ico    -> custom path
"""
import io
import os
import struct
import sys

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SS = 4

TILE_TOP = (44, 52, 68)
TILE_BOTTOM = (22, 26, 34)
TILE_EDGE = (92, 104, 128, 255)
TRACK = (120, 132, 154, 255)
KNOB_TOP = (140, 220, 255)
KNOB_BOTTOM = (64, 160, 236)
KNOB_RIM = (12, 14, 18, 255)


def render(size):
    S = size * SS
    u = S / 256.0
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Tile: vertical gradient inside a rounded square, with a light edge
    margin = (6 if size >= 32 else 2) * u
    radius = 56 * u
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, S - margin, S - margin), radius=radius, fill=255)
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / float(S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(TILE_TOP[i] + (TILE_BOTTOM[i] - TILE_TOP[i]) * t) for i in range(3)) + (255,))
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    edge = max(1, int((7 if size >= 32 else 12) * u))
    d.rounded_rectangle((margin, margin, S - margin, S - margin), radius=radius, outline=TILE_EDGE, width=edge)

    # Three sliders: tracks and gradient knobs at different heights
    xs = [72, 128, 184]
    knobs = [0.70, 0.22, 0.50]
    top, bottom = 58 * u, 198 * u
    track_w = max(2, int((13 if size >= 32 else 22) * u))
    kr = (20 if size >= 32 else 25) * u
    rim = max(1, int((7 if size >= 32 else 11) * u))
    for x, k in zip(xs, knobs):
        x *= u
        d.line([(x, top), (x, bottom)], fill=TRACK, width=track_w)
        d.ellipse((x - track_w / 2, top - track_w / 2, x + track_w / 2, top + track_w / 2), fill=TRACK)
        d.ellipse((x - track_w / 2, bottom - track_w / 2, x + track_w / 2, bottom + track_w / 2), fill=TRACK)
        cy = top + (bottom - top) * k
        knob = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        kd = ImageDraw.Draw(knob)
        for i in range(int(2 * kr) + 1):
            t = i / max(1.0, 2 * kr)
            c = tuple(int(KNOB_TOP[j] + (KNOB_BOTTOM[j] - KNOB_TOP[j]) * t) for j in range(3)) + (255,)
            kd.line([(x - kr, cy - kr + i), (x + kr, cy - kr + i)], fill=c)
        kmask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(kmask).ellipse((x - kr, cy - kr, x + kr, cy + kr), fill=255)
        d.ellipse((x - kr - rim, cy - kr - rim, x + kr + rim, cy + kr + rim), fill=KNOB_RIM)
        img.paste(knob, (0, 0), kmask)
        if size >= 48:  # specular highlight, blended (ImageDraw on RGBA would replace the pixels instead)
            hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
            ImageDraw.Draw(hl).ellipse((x - kr * 0.5, cy - kr * 0.66, x + kr * 0.05, cy - kr * 0.25), fill=(255, 255, 255, 120))
            img = Image.alpha_composite(img, hl)
            d = ImageDraw.Draw(img)

    return img.resize((size, size), Image.LANCZOS)


def dib(img):
    w, h = img.size
    px = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    row = ((w + 31) // 32) * 4
    andm = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(row)
        for x in range(w):
            if px[x, y][3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        andm += bits
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andm), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(andm)


def write_ico(path, images):
    blobs = []
    for img in images:
        if img.size[0] >= 256:
            buf = io.BytesIO()
            img.save(buf, "PNG")
            blobs.append((img.size[0], buf.getvalue()))
        else:
            blobs.append((img.size[0], dib(img)))
    offset = 6 + 16 * len(blobs)
    out = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))
    for w, data in blobs:
        b = 0 if w >= 256 else w
        out += struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in blobs:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "assets", "DacDeck.ico")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    write_ico(out, [render(s) for s in SIZES])
    print("Wrote " + out)


if __name__ == "__main__":
    main()
