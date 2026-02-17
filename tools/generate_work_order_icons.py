#!/usr/bin/env python3
"""Generate parchment-with-wax-seal PNG icons for Valheim Villages work orders.

Uses only Python stdlib (struct + zlib) -- no PIL/Pillow required.
Each station type gets a distinct wax seal colour. Output is 64x64 RGBA PNGs.

Style: old treasure map viewed from Valheim's isometric perspective.
Parchment is rotated ~20° with opposite curls at top (toward viewer)
and bottom (away from viewer). Coloured wax seal sits bottom-right.
"""

import math, os, struct, zlib

SIZE = 64
SCX, SCY = 32, 32  # screen centre

# ── rotation (20° clockwise in icon space) ─────────────────────────
THETA = math.radians(20)
COS_T, SIN_T = math.cos(THETA), math.sin(THETA)

# ── parchment dimensions (local coords, centred at origin) ────────
HALF_W = 16                      # half-width (spacing between sine centres)
HALF_H = 22                      # half-height
WAVE_AMP = 5                     # sine wave amplitude on each edge
PHASE_OFF = 0.4                  # phase offset between left and right waves
WAVE_FREQ = 0.75                 # sine frequency multiplier (0.5 = half-wave)

# ── wax seal (local coords, centre-right of document) ─────────────
SEAL_LX, SEAL_LY = 5, -5
SEAL_R = 6
SEAL_R2 = SEAL_R * SEAL_R

# ── text lines (local coords): (ly, lx_left, lx_right) ───────────
TEXT_LINES = [(11, -9, 15), (8, -9, 15), (5, -9, 8)]

OUTLINE = (26, 18, 10, 255)

STATIONS = {
    "workbench":    (155, 100, 45),    # rich wood brown
    "forge":        (185, 115, 45),    # bronze / copper
    "cauldron":     (110, 50,  120),   # alchemical purple
    "stonecutter":  (125, 130, 140),   # stone gray
    "artisantable": (85,  140, 185),   # light artisan blue
    "farmer":       (55,  135, 55),    # crop green
    "tavernkeeper": (160, 55,  40),    # wine / mead red
}

# ── station symbols (small bitmaps drawn as ink next to the seal) ─
SYM_CX, SYM_CY = -6, -3           # symbol centre in local coords

STATION_SYMBOLS = {
    "workbench": [     # Hammer
        ".###.",
        ".###.",
        "..#..",
        "..#..",
        "..#..",
        "..#..",
        "..##.",
    ],
    "forge": [         # Anvil
        "..##..",
        ".####.",
        "..##..",
        "..##..",
        ".####.",
        "######",
    ],
    "cauldron": [      # Potion flask
        "..##..",
        "..##..",
        ".####.",
        "######",
        "######",
        ".####.",
    ],
    "stonecutter": [   # Brick / block
        "######",
        "#..#.#",
        "######",
        "#.#..#",
        "######",
    ],
    "artisantable": [  # Gear / cog
        "..##..",
        ".####.",
        "##..##",
        "##..##",
        ".####.",
        "..##..",
    ],
    "farmer": [        # Wheat sprout
        "..#...",
        ".###..",
        "..#...",
        ".###..",
        "..#...",
        "..##..",
    ],
    "tavernkeeper": [  # Goblet / chalice
        "#...#",
        ".#.#.",
        "..#..",
        "..#..",
        ".###.",
        ".###.",
    ],
}


# ── coordinate transform ─────────────────────────────────────────

def _to_local(sx, sy):
    """Screen → parchment-local coords (inverse of clockwise rotation)."""
    dx, dy = sx - SCX, sy - SCY
    return dx * COS_T - dy * SIN_T, dx * SIN_T + dy * COS_T


# ── shape helpers (all in local coords) ──────────────────────────

def _edge_bounds(ly):
    """Return (left_x, right_x) parchment bounds at height ly, or None.

    Two sine waves, slightly phase-offset, form the left and right edges.
    """
    if abs(ly) > HALF_H:
        return None
    n = ly / HALF_H                 # normalise to [-1, +1]
    left_x  = -HALF_W + WAVE_AMP * math.sin(n * math.pi * WAVE_FREQ)
    right_x =  HALF_W + WAVE_AMP * math.sin(n * math.pi * WAVE_FREQ + PHASE_OFF)
    return (left_x, right_x)

def _in_parchment(lx, ly):
    bounds = _edge_bounds(ly)
    if bounds is None:
        return False
    return bounds[0] <= lx <= bounds[1]

def _in_seal(lx, ly):
    return (lx - SEAL_LX) ** 2 + (ly - SEAL_LY) ** 2 <= SEAL_R2

def _in_text(lx, ly):
    for tly, tl, tr in TEXT_LINES:
        if abs(ly - tly) < 1.0 and tl <= lx <= tr:
            return True
    return False

def _in_symbol(lx, ly, symbol):
    """Check if (lx, ly) falls on a filled '#' pixel of the symbol bitmap."""
    rows, cols = len(symbol), len(symbol[0])
    bx = int(lx - (SYM_CX - cols / 2.0))
    by = int((SYM_CY + rows / 2.0) - ly)
    if 0 <= bx < cols and 0 <= by < rows:
        return symbol[by][bx] == '#'
    return False


# ── colour helpers ───────────────────────────────────────────────

def _clamp(v):
    return max(0, min(255, int(v)))

def _lerp(a, b, t):
    return a + (b - a) * t

def _darken(rgba, f):
    return (_clamp(rgba[0] * f), _clamp(rgba[1] * f), _clamp(rgba[2] * f), 255)

def _parchment_base(lx, ly):
    """Base parchment colour with curvature shading and grain."""
    curve = 1.0 - abs(lx) / (HALF_W + WAVE_AMP + 4)
    v = max(0.0, min(1.0, curve * 0.5 + 0.5))
    ix, iy = int(lx + 40), int(ly + 40)
    grain = ((ix * 7 + iy * 13 + ix * iy) % 19) / 300.0 - 0.03
    return (_clamp((_lerp(0.62, 0.87, v) + grain) * 255),
            _clamp((_lerp(0.50, 0.76, v) + grain * 0.8) * 255),
            _clamp((_lerp(0.35, 0.58, v) + grain * 0.6) * 255), 255)

def _parchment_color(lx, ly):
    """Parchment with smooth curl shading — top darker, bottom lighter."""
    base = _parchment_base(lx, ly)

    if ly >= 0:
        t = ly / HALF_H
        shadow = 0.38 * (t ** 2) * (1.0 - t * 0.35)
        return _darken(base, max(0.50, 1.0 - shadow))

    # Bottom half — toward viewer, lit face
    t = -ly / HALF_H
    shadow = 0.16 * (t ** 2) * (1.0 - t)
    f = max(0.82, 1.0 - shadow)
    if t > 0.65:
        f += (t - 0.65) * 0.35          # highlight at curling tip
    return _darken(base, min(1.12, f))

def _seal_color(lx, ly, rgb):
    """Wax seal: dark rim, directional light, glossy highlight, stamp ring."""
    d = math.sqrt((lx - SEAL_LX) ** 2 + (ly - SEAL_LY) ** 2)
    r = d / SEAL_R
    if r > 0.78:
        shade = 0.38 + 0.30 * ((1.0 - r) / 0.22)
    else:
        dx, dy = lx - SEAL_LX, ly - SEAL_LY
        light = (-dx + dy) / (SEAL_R * 1.414)
        shade = 0.72 + light * 0.18
        hx, hy = lx - (SEAL_LX - 1.5), ly - (SEAL_LY + 1.5)
        h_dist = math.sqrt(hx * hx + hy * hy) / (SEAL_R * 0.5)
        if h_dist < 1.0:
            shade += (1.0 - h_dist) * 0.25
        if 0.30 < r < 0.50:
            shade -= 0.06
        if r < 0.15:
            shade -= 0.04
    shade = max(0.28, min(1.15, shade))
    return (_clamp(rgb[0] * shade), _clamp(rgb[1] * shade),
            _clamp(rgb[2] * shade), 255)

def _seal_outline(rgb):
    return (_clamp(rgb[0] * 0.22), _clamp(rgb[1] * 0.22),
            _clamp(rgb[2] * 0.22), 255)

def _symbol_ink(parch, seal_rgb):
    """Blend darkened seal colour into the parchment — like a coloured ink stamp."""
    return (_clamp(parch[0] * 0.35 + seal_rgb[0] * 0.30),
            _clamp(parch[1] * 0.35 + seal_rgb[1] * 0.30),
            _clamp(parch[2] * 0.35 + seal_rgb[2] * 0.30), 255)


# ── rendering ────────────────────────────────────────────────────

def render(seal_rgb, station):
    symbol = STATION_SYMBOLS.get(station)
    px = [(0, 0, 0, 0)] * (SIZE * SIZE)

    for sy in range(SIZE):
        for sx in range(SIZE):
            lx, ly = _to_local(sx, sy)

            if not _in_parchment(lx, ly):
                # Drop shadow (lower-right in image)
                slx, sly = _to_local(sx - 2, sy + 2)
                if _in_parchment(slx, sly):
                    px[sy * SIZE + sx] = (0, 0, 0, 64)
                continue

            # Parchment body + curl shading
            c = _parchment_color(lx, ly)

            # Faint text lines
            if _in_text(lx, ly):
                c = _darken(c, 0.78)

            # Station symbol (coloured ink stamp)
            if symbol and _in_symbol(lx, ly, symbol):
                c = _symbol_ink(c, seal_rgb)

            # Wax seal (drawn on top of symbol if overlapping)
            if _in_seal(lx, ly):
                c = _seal_color(lx, ly, seal_rgb)
                if (not _in_seal(*_to_local(sx - 1, sy))
                        or not _in_seal(*_to_local(sx + 1, sy))
                        or not _in_seal(*_to_local(sx, sy - 1))
                        or not _in_seal(*_to_local(sx, sy + 1))):
                    c = _seal_outline(seal_rgb)

            # Outer outline (screen-space neighbours, local-space test)
            if (not _in_parchment(*_to_local(sx - 1, sy))
                    or not _in_parchment(*_to_local(sx + 1, sy))
                    or not _in_parchment(*_to_local(sx, sy - 1))
                    or not _in_parchment(*_to_local(sx, sy + 1))):
                c = OUTLINE

            px[sy * SIZE + sx] = c

    return px


# ── minimal PNG writer ───────────────────────────────────────────

def _chunk(ctype, data):
    raw = ctype + data
    return struct.pack(">I", len(data)) + raw + struct.pack(">I", zlib.crc32(raw) & 0xFFFFFFFF)

def write_png(path, pixels):
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = _chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    raw = b""
    for y in range(SIZE - 1, -1, -1):
        raw += b"\x00"
        for x in range(SIZE):
            raw += bytes(pixels[y * SIZE + x])
    idat = _chunk(b"IDAT", zlib.compress(raw))
    iend = _chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(sig + ihdr + idat + iend)


# ── main ─────────────────────────────────────────────────────────

def main():
    out = os.path.join(os.path.dirname(__file__), "..",
                       "src", "ValheimVillages", "Items", "Icons", "WorkOrders")
    os.makedirs(out, exist_ok=True)

    for name, rgb in STATIONS.items():
        pixels = render(rgb, name)
        path = os.path.join(out, f"workorder_{name}.png")
        write_png(path, pixels)
        print(f"  wrote {path}")

    print(f"\nGenerated {len(STATIONS)} work order icons.")

if __name__ == "__main__":
    main()
