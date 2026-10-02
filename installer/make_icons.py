#!/usr/bin/env python3
"""Generate the DDT4All icon set (PNG sizes, .ico, .icns, SVG) with numpy + stdlib only.

Motif: an ECU chip (rounded body with pins) carrying a diagnostic pulse trace.
Usage: python3 make_icons.py   (writes into installer/assets/)
"""
import struct, zlib, io, os
import numpy as np

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets")
os.makedirs(OUT, exist_ok=True)
SS = 4  # supersampling factor


def rrect_sdf(x, y, cx, cy, hw, hh, r):
    qx = np.abs(x - cx) - (hw - r)
    qy = np.abs(y - cy) - (hh - r)
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - r


def seg_dist(x, y, a, b):
    ax, ay = a; bx, by = b
    dx, dy = bx - ax, by - ay
    t = np.clip(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1)
    return np.hypot(x - (ax + t * dx), y - (ay + t * dy))


def over(dst, rgb, alpha):
    for i in range(3):
        dst[..., i] = dst[..., i] * (1 - alpha) + rgb[i] * alpha
    return dst


def render(size):
    n = size * SS
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float64)
    x = xs / n; y = ys / n  # unit square
    px = 1.0 / n
    cov = lambda d: np.clip(0.5 - d / px, 0, 1)  # sdf -> coverage

    rgb = np.zeros((n, n, 3))
    # background squircle with vertical gradient
    bg = cov(rrect_sdf(x, y, .5, .5, .5, .5, .22))
    top = np.array([0x1c, 0x3a, 0x5e]); bot = np.array([0x0a, 0x14, 0x26])
    for i in range(3):
        rgb[..., i] = top[i] * (1 - y) + bot[i] * y
    alpha = bg.copy()

    # pins
    pin = np.zeros((n, n))
    pc = np.array([0xb8, 0xc7, 0xd9])
    for k in range(4):
        c = .335 + k * .11
        for (cx, cy, hw, hh) in [(c, .205, .018, .045), (c, .795, .018, .045),
                                 (.205, c, .045, .018), (.795, c, .045, .018)]:
            pin = np.maximum(pin, cov(rrect_sdf(x, y, cx, cy, hw, hh, .012)))
    over(rgb, pc, pin * bg)

    # chip body: light border + dark inside
    body_o = cov(rrect_sdf(x, y, .5, .5, .30, .30, .06))
    body_i = cov(rrect_sdf(x, y, .5, .5, .30 - .022, .30 - .022, .045))
    over(rgb, np.array([0xe6, 0xee, 0xf6]), body_o * bg)
    over(rgb, np.array([0x0d, 0x1b, 0x2f]), body_i * bg)
    # pin-1 notch
    over(rgb, np.array([0x27, 0xd7, 0xb0]), cov(np.hypot(x - .27, y - .27) - .018) * bg)

    # pulse trace
    pts = [(.24, .54), (.38, .54), (.43, .44), (.49, .68), (.55, .33), (.60, .54), (.76, .54)]
    d = np.full((n, n), 9.0)
    for a, b in zip(pts[:-1], pts[1:]):
        d = np.minimum(d, seg_dist(x, y, a, b))
    glow = np.clip(1 - d / .05, 0, 1) ** 2 * .25
    over(rgb, np.array([0x27, 0xd7, 0xb0]), glow * body_i * bg)
    over(rgb, np.array([0x3a, 0xf0, 0xc4]), cov(d - .018 / 1.0) * body_i * bg)

    # downsample
    def ds(a):
        return a.reshape(size, SS, size, SS, *a.shape[2:]).mean(axis=(1, 3))
    prem = ds(rgb * alpha[..., None])
    a = ds(alpha)
    out = np.zeros((size, size, 4), np.uint8)
    with np.errstate(invalid="ignore", divide="ignore"):
        col = np.where(a[..., None] > 0, prem / a[..., None], 0)
    out[..., :3] = np.clip(col, 0, 255).astype(np.uint8)
    out[..., 3] = np.clip(a * 255 + .5, 0, 255).astype(np.uint8)
    return out


def png_bytes(arr):
    h, w = arr.shape[:2]
    raw = b"".join(b"\x00" + arr[r].tobytes() for r in range(h))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def write_ico(path, pngs):  # PNG-compressed ICO entries (Vista+)
    sizes = sorted(pngs)
    hdr = struct.pack("<HHH", 0, 1, len(sizes))
    off = 6 + 16 * len(sizes)
    ent, data = b"", b""
    for s in sizes:
        b = pngs[s]
        ent += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(b), off + len(data))
        data += b
    open(path, "wb").write(hdr + ent + data)


def write_icns(path, pngs):
    types = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07", 256: b"ic08", 512: b"ic09", 1024: b"ic10"}
    body = b""
    for s, t in types.items():
        body += t + struct.pack(">I", 8 + len(pngs[s])) + pngs[s]
    open(path, "wb").write(b"icns" + struct.pack(">I", 8 + len(body)) + body)


SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1c3a5e"/><stop offset="1" stop-color="#0a1426"/></linearGradient></defs>
  <rect width="1024" height="1024" rx="225" fill="url(#g)"/>
  <g fill="#b8c7d9">__PINS__</g>
  <rect x="215" y="215" width="594" height="594" rx="61" fill="#e6eef6"/>
  <rect x="237" y="237" width="550" height="550" rx="46" fill="#0d1b2f"/>
  <circle cx="276" cy="276" r="18" fill="#27d7b0"/>
  <polyline points="246,553 389,553 440,451 502,696 563,338 614,553 778,553" fill="none" stroke="#3af0c4" stroke-width="37" stroke-linejoin="round" stroke-linecap="round"/>
</svg>
"""


def svg():
    pins = ""
    for k in range(4):
        c = .335 + k * .11
        for cx, cy, hw, hh in [(c, .205, .018, .045), (c, .795, .018, .045), (.205, c, .045, .018), (.795, c, .045, .018)]:
            pins += '<rect x="%d" y="%d" width="%d" height="%d" rx="12"/>' % (
                (cx - hw) * 1024, (cy - hh) * 1024, 2 * hw * 1024, 2 * hh * 1024)
    open(os.path.join(OUT, "ddt4all.svg"), "w").write(SVG.replace("__PINS__", pins))


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256, 512, 1024]
    pngs = {}
    for s in sizes:
        pngs[s] = png_bytes(render(s))
        open(os.path.join(OUT, f"ddt4all-{s}.png"), "wb").write(pngs[s])
    write_ico(os.path.join(OUT, "ddt4all.ico"), {s: pngs[s] for s in (16, 24, 32, 48, 64, 128, 256)})
    write_icns(os.path.join(OUT, "ddt4all.icns"), pngs)
    svg()
    # small PNG-based wizard images are not needed; Inno uses the .ico
    print("icons written to", OUT)


if __name__ == "__main__":
    main()
