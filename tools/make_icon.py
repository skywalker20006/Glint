"""
Draws Glint's icon: a glossy cherry magnifying glass over a rosy page, on a sheet of dark cherry-red acrylic.
Everything is softly shaded as if lit from the top left, so the shapes look rounded rather than flat.

    python tools/make_icon.py                 -> Assets/Glint.ico (16-256 px) and Assets/Glint-256.png
    python tools/make_icon.py --preview x.png -> also a sheet of every size on light and dark backgrounds

Each size is drawn at 4x and scaled down. Sizes of 32 px and under drop the page's text and fine highlights
so the magnifier stays crisp in the taskbar and tray.
"""
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "Assets")
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

SHEET = ((168, 28, 58), (78, 6, 26))          # dark cherry acrylic, top-left to bottom-right
SHADOW = (40, 0, 12)                            # shadows cast on the sheet
PAPER = ((250, 214, 222), (228, 160, 176))      # rosy page, top to bottom
INK = (206, 124, 144)                           # the page's lines of text
GLASS = ((242, 96, 124), (150, 16, 46))         # glossy cherry frame and handle, top to bottom


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def gradient(n, c1, c2, diagonal=False):
    """n x n RGBA fill from c1 to c2, top to bottom (or top-left to bottom-right)."""
    small = Image.new("RGBA", (64, 64))
    px = small.load()
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126 if diagonal else y / 63
            px[x, y] = lerp(c1, c2, t) + (255,)
    return small.resize((n, n), Image.BICUBIC)


def shift(mask, dx, dy):
    return mask.transform(mask.size, Image.AFFINE, (1, 0, -dx, 0, 1, -dy))


def scale(mask, k):
    return mask.point(lambda v: round(v * k))


def solid(n, rgb, mask):
    layer = Image.new("RGBA", (n, n), rgb + (0,))
    layer.putalpha(mask)
    return layer


def drop_shadow(img, mask, n, offset, blur, strength):
    img.alpha_composite(solid(n, SHADOW, scale(shift(mask, offset * 0.4, offset), strength)).filter(ImageFilter.GaussianBlur(blur)))


def shaded(img, mask, n, colors, depth, light=0.55, dark=0.45, diagonal=False):
    """
    Paints a shape as a soft, rounded solid: its colour, a lit rim along the top-left edges and a shaded
    rim along the bottom-right, both blurred so the surface seems to curve away.
    """
    img.alpha_composite(Image.composite(gradient(n, *colors, diagonal=diagonal), Image.new("RGBA", (n, n)), mask))
    blur = ImageFilter.GaussianBlur(depth * 0.8)
    lit = ImageChops.multiply(ImageChops.subtract(mask, shift(mask, depth, depth).filter(blur)), mask)
    img.alpha_composite(solid(n, (255, 255, 255), scale(lit.filter(ImageFilter.GaussianBlur(depth * 0.35)), light)))
    shade = ImageChops.multiply(ImageChops.subtract(mask, shift(mask, -depth, -depth).filter(blur)), mask)
    img.alpha_composite(solid(n, SHADOW, scale(shade.filter(ImageFilter.GaussianBlur(depth * 0.35)), dark)))


def rounded(n, box, radius):
    m = Image.new("L", (n, n), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m


def disc(n, cx, cy, r):
    m = Image.new("L", (n, n), 0)
    ImageDraw.Draw(m).ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)
    return m


def draw(size):
    ss = 4
    n = size * ss
    simple = size <= 32
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # ---- the acrylic sheet ----
    margin = n * (0.04 if simple else 0.08)
    box = (margin, margin, n - margin, n - margin - (0 if simple else n * 0.015))
    sheet = rounded(n, box, (box[2] - box[0]) * 0.26)
    if not simple:
        drop_shadow(img, sheet, n, n * 0.02, n * 0.03, 0.5)
    shaded(img, sheet, n, SHEET, n * 0.03, light=0.22, dark=0.35, diagonal=True)
    if not simple:
        # frosted grain
        rnd = random.Random(7)
        g = Image.new("L", (n // 4, n // 4))
        g.putdata([rnd.randint(0, 255) for _ in range(g.width * g.height)])
        g = g.resize((n, n), Image.NEAREST).point(lambda v: 12 if v > 200 else 0)
        img.alpha_composite(solid(n, (255, 255, 255), ImageChops.multiply(g, sheet)))

    # ---- the page: a soft rounded card ----
    px0, py0, px1, py1 = (n * 0.22, n * 0.18, n * 0.64, n * 0.74) if not simple else (n * 0.12, n * 0.1, n * 0.6, n * 0.72)
    page = rounded(n, (px0, py0, px1, py1), n * (0.07 if not simple else 0.08))
    drop_shadow(img, page, n, n * 0.025, n * 0.03, 0.55 if not simple else 0.4)
    shaded(img, page, n, PAPER, n * 0.022, light=0.6, dark=0.3)
    if not simple:
        d = ImageDraw.Draw(img)
        lw = n * 0.026
        x0 = px0 + n * 0.065
        for i, frac in enumerate((0.55, 0.85, 0.7, 0.8, 0.5)):
            y = py0 + n * 0.09 + i * n * 0.075
            d.rounded_rectangle((x0, y - lw / 2, x0 + (px1 - px0 - n * 0.13) * frac, y + lw / 2),
                                radius=lw / 2, fill=INK + (255,))

    # ---- the magnifier ----
    cx, cy = n * (0.56 if not simple else 0.53), n * (0.57 if not simple else 0.54)
    r = n * (0.175 if not simple else 0.25)            # to the outside of the frame
    ring_w = n * (0.07 if not simple else 0.11)
    hw = n * (0.095 if not simple else 0.14)          # handle thickness
    k = 0.7071
    hx0, hy0 = cx + (r - ring_w * 0.5) * k, cy + (r - ring_w * 0.5) * k
    hx1, hy1 = hx0 + n * (0.15 if not simple else 0.13) * k, hy0 + n * (0.15 if not simple else 0.13) * k

    lens = disc(n, cx, cy, r - ring_w)
    frame = ImageChops.subtract(disc(n, cx, cy, r), lens)
    handle = Image.new("L", (n, n), 0)
    hd = ImageDraw.Draw(handle)
    hd.line((hx0, hy0, hx1, hy1), fill=255, width=round(hw))
    hd.ellipse((hx1 - hw / 2, hy1 - hw / 2, hx1 + hw / 2, hy1 + hw / 2), fill=255)
    body = ImageChops.lighter(frame, ImageChops.subtract(handle, disc(n, cx, cy, r - ring_w * 0.5)))

    drop_shadow(img, body, n, n * 0.03, n * 0.03, 0.6 if not simple else 0.45)

    # the lens: tinted glass that lets the page show through, darker at its top-left rim, with a gleam
    img.alpha_composite(solid(n, (255, 228, 236), scale(lens, 0.35)))
    inner = ImageChops.multiply(ImageChops.subtract(lens, shift(lens, n * 0.02, n * 0.025).filter(ImageFilter.GaussianBlur(n * 0.01))), lens)
    img.alpha_composite(solid(n, GLASS[1], scale(inner.filter(ImageFilter.GaussianBlur(n * 0.008)), 0.5)))
    if not simple:
        gleam = Image.new("L", (n, n), 0)
        gr = r - ring_w
        ImageDraw.Draw(gleam).ellipse((cx - gr * 0.62, cy - gr * 0.72, cx + gr * 0.05, cy - gr * 0.22), fill=255)
        img.alpha_composite(solid(n, (255, 255, 255), scale(gleam.filter(ImageFilter.GaussianBlur(n * 0.018)), 0.55)))

    # the frame and handle: glossy cherry, rounded like a tube
    shaded(img, body, n, GLASS, n * (0.02 if not simple else 0.03), light=0.75, dark=0.5)
    if not simple:
        spec = Image.new("L", (n, n), 0)
        sr = r - ring_w * 0.5
        ImageDraw.Draw(spec).arc((cx - sr, cy - sr, cx + sr, cy + sr), 200, 250, fill=255, width=round(ring_w * 0.28))
        img.alpha_composite(solid(n, (255, 255, 255), scale(spec.filter(ImageFilter.GaussianBlur(n * 0.005)), 0.8)))

    return img.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    images = [draw(s) for s in SIZES]
    big = images[-1]
    big.save(os.path.join(OUT_DIR, "Glint-256.png"))
    big.save(os.path.join(OUT_DIR, "Glint.ico"), sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    if "--preview" in sys.argv:
        w = sum(SIZES) + 12 * (len(SIZES) + 1)
        sheet = Image.new("RGBA", (w, 2 * 280), (243, 243, 243, 255))
        ImageDraw.Draw(sheet).rectangle((0, 280, w, 2 * 280), fill=(32, 32, 32, 255))
        for row in range(2):
            x = 12
            for im in images:
                sheet.alpha_composite(im, (x, row * 280 + 12 + (256 - im.height)))
                x += im.width + 12
        sheet.save(sys.argv[sys.argv.index("--preview") + 1])


if __name__ == "__main__":
    main()
