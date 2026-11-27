# Build the Windows application icons from the source artwork.
#
#   python tools/make_icon.py [source.png] [out.ico]     the application icon
#   python tools/make_icon.py --games                     one tile per supported game
#
# A .ico is a container of pre-scaled bitmaps; Windows picks the one closest to the size it needs
# (title bar 16, taskbar/alt-tab 24-32, Explorer small 48, large 256). Shipping only the 256 makes
# the small sizes a blurry downscale of the whole picture, so every size is rendered from the
# original instead. Pillow writes the 32-bit entries, PNG-compressed, which is what the apphost
# embeds and what Explorer reads.
#
# The per-game tiles (--games) are what let Explorer, the window's taskbar button and the Start-Menu
# shortcuts the installer writes say which family a level belongs to. Each one is the application
# tile with its purple swapped for the family accent and its "TE" mark swapped for the family's
# short code, so the set reads as one product in five colours rather than five unrelated pictures.
# The accents are the same RGB the editor's own status-bar chip uses (MainWindow.GameChip) and the
# codes are the family keys the editor reports; change one side, change the other.
#
# Nothing here is used at run time: the generated .ico files are committed, and the only inputs are
# assets/app-icon.png and a system font.

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, 'assets', 'app-icon.png')
OUT = os.path.join(ROOT, 'src', 'TastyEditor.App', 'app.ico')
GAMEDIR = os.path.join(ROOT, 'src', 'TastyEditor.App', 'icons')
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
FONT = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts', 'arialbd.ttf')

# (profile key, accent RGB, mark). The mark is short because it has to stay legible at 48 px.
GAMES = (
    ('tp2', (94, 129, 214), 'TP2'),
    ('tb', (56, 169, 196), 'TB'),
    ('f4', (122, 180, 110), 'F4'),
    ('f5', (201, 138, 110), 'F5'),
    ('tp1', (170, 120, 178), 'TP1'),
)

# Three bands are separated by these thresholds, measured off the artwork: the tile's darkest
# shade sits at ~48 on the max-channel scale while the black matte behind it tops out at ~40, and
# the "TE" is the only bright, unsaturated thing inside the tile.
MATTE_LO, MATTE_HI = 8, 40
MARK_MIN_V, MARK_MAX_S = 200.0 / 255.0, 0.25
MARK_PAD = 14            # px at source scale: covers the mark's antialiased edge and its shadow
FIELD_SIDE = 384         # the tile's lighting is smooth, so it is fitted, not copied per pixel
FIELD_DEGREE = 4


def load_source():
    """The artwork on a square canvas (pad the short side rather than squash it)."""
    im = Image.open(SRC).convert('RGB')
    w, h = im.size
    if w != h:
        side = max(w, h)
        pad = Image.new('RGB', (side, side), (0, 0, 0))
        pad.paste(im, ((side - w) // 2, (side - h) // 2))
        im = pad
    return im


def tile_alpha(rgb):
    """Turn the black matte into real transparency.

    Windows composites an icon with its alpha channel, so an opaque matte would paint a black
    square behind the rounded tile on any desktop. The ramp over the antialiased edge keeps the
    corners from fringing."""
    mx = rgb.max(axis=2).astype(np.int32)
    a = np.clip((mx - MATTE_LO) * 255 // (MATTE_HI - MATTE_LO), 0, 255)
    return a.astype(np.uint8)


def mark_mask(rgb):
    """Where the app's own "TE" (and its shadow) sits: the artwork a family tile must not inherit."""
    mx = rgb.max(axis=2).astype(np.float32) / 255.0
    mn = rgb.min(axis=2).astype(np.float32) / 255.0
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    m = (mx > MARK_MIN_V) & (sat < MARK_MAX_S)
    img = Image.fromarray((m * 255).astype(np.uint8), 'L').filter(
        ImageFilter.MaxFilter(MARK_PAD * 2 + 1))
    return np.asarray(img) > 0


def _poly_terms(x, y, degree):
    """The 2-D polynomial basis up to `degree`, as a (n, k) design matrix."""
    cols = [x ** i * y ** j for i in range(degree + 1) for j in range(degree + 1 - i)]
    return np.stack(cols, axis=1).astype(np.float64)


def light_field(rgb, mask):
    """The source tile's lighting as one smooth surface: a low-order polynomial through its
    brightness, sampled away from the app mark and the matte.

    Driving the recolour from the fit rather than from the pixels means nothing of the original
    mark can survive into a family tile, however it is re-scaled: the tile is repainted from the
    gradient, so the accent is lit exactly like the artwork it came from."""
    mx = rgb.max(axis=2).astype(np.float64)
    h, w = mx.shape
    step = 4
    ok = (mx > MATTE_HI) & ~mask
    gy, gx = np.mgrid[0:h:step, 0:w:step]
    sel = ok[::step, ::step]
    x = gx[sel].astype(np.float64) / (w - 1) * 2 - 1
    y = gy[sel].astype(np.float64) / (h - 1) * 2 - 1
    coef = np.linalg.lstsq(_poly_terms(x, y, FIELD_DEGREE), mx[::step, ::step][sel], rcond=None)[0]

    fy, fx = np.mgrid[0:FIELD_SIDE, 0:FIELD_SIDE]
    nx = fx.astype(np.float64).ravel() / (FIELD_SIDE - 1) * 2 - 1
    ny = fy.astype(np.float64).ravel() / (FIELD_SIDE - 1) * 2 - 1
    field = _poly_terms(nx, ny, FIELD_DEGREE) @ coef
    field = field.reshape(FIELD_SIDE, FIELD_SIDE)
    lo, hi = np.percentile(field, [1.0, 99.0])
    return np.clip((field - lo) / max(hi - lo, 1e-6), 0.0, 1.0).astype(np.float32)


def recolour(light, accent):
    """Stretch the accent across the fitted lighting: dark at the tile's shadowed corner, bright
    where the source is lit, with the family colour readable over the whole tile rather than only
    at its brightest point."""
    a = np.array(accent, dtype=np.float32)[None, None, :]
    return np.clip(a * (0.52 + 0.68 * light[:, :, None]), 0, 255).astype(np.uint8)


def draw_mark(im, text):
    """Set the family code where "TE" was: same optical centre, same share of the tile, white with
    the soft shadow the source art carries."""
    side = im.size[0]
    overlay = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    # Arial Bold caps at ~0.716 em; fit the string into the box the original mark occupied.
    box_w, box_h = 0.78 * side, 0.40 * side
    size = max(6, int(box_h / 0.716))
    while True:
        f = ImageFont.truetype(FONT, size)
        x0, y0, x1, y1 = d.textbbox((0, 0), text, font=f)
        if size <= 6 or ((x1 - x0) <= box_w and (y1 - y0) <= box_h):
            break
        size = max(6, size - 2)
    x0, y0, x1, y1 = d.textbbox((0, 0), text, font=f)
    ox = (side - (x1 - x0)) / 2 - x0
    oy = side * 0.482 - (y1 - y0) / 2 - y0
    shadow = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).text((ox + side * 0.012, oy + side * 0.016), text, font=f, fill=(0, 0, 0, 130))
    shadow = shadow.filter(ImageFilter.GaussianBlur(max(1.0, side * 0.012)))
    overlay = Image.alpha_composite(overlay, shadow)
    ImageDraw.Draw(overlay).text((ox, oy), text, font=f, fill=(255, 255, 255, 255))
    return overlay


def render(light, alpha, accent, text, side=256):
    """One game tile at `side`: the accent lit by the source's gradient, the family code set over
    it, and the source's rounded silhouette as alpha."""
    t = np.asarray(Image.fromarray(light, mode='F').resize((side, side), Image.LANCZOS),
                   dtype=np.float32)
    im = Image.fromarray(recolour(t, accent)).convert('RGBA')
    im = Image.alpha_composite(im, draw_mark(im, text))
    im.putalpha(Image.fromarray(alpha).resize((side, side), Image.LANCZOS))
    return im


def render_set(light, alpha, accent, text):
    """Every icon size, each rendered straight from the source art, so the 16 px title-bar glyph is
    a 16 px composition rather than a 256 px picture crushed down to it."""
    return [render(light, alpha, accent, text, side=s) for s in SIZES]


def save_ico(images, out):
    """Pack pre-rendered sizes into one .ico (largest first, the rest as append_images).

    Pillow walks the `sizes` list and only falls back to thumbnailing when no supplied image has
    that size, so naming every entry keeps the container exactly as it was rendered."""
    os.makedirs(os.path.dirname(out), exist_ok=True)
    images = [i for i in images if i.size[0] <= 256]
    big = max(images, key=lambda i: i.size[0])
    rest = [i for i in images if i is not big]
    sizes = [(i.size[0], i.size[1]) for i in images]
    big.save(out, format='ICO', sizes=sizes, append_images=rest)
    print('wrote %s (%d bytes, %d entries)' % (out, os.path.getsize(out), len(images)))


def main(argv):
    if argv and argv[0] == '--games':
        rgb = np.asarray(load_source())
        light = light_field(rgb, mark_mask(rgb))
        alpha = tile_alpha(rgb)
        sheet = []
        for key, accent, text in GAMES:
            images = render_set(light, alpha, accent, text)
            save_ico(images, os.path.join(GAMEDIR, key + '.ico'))
            sheet.append((key, images[SIZES.index(256)]))
        # A contact sheet so the set can be eyeballed in one look (not shipped, not committed).
        side = 256
        out = Image.new('RGBA', (side * len(sheet), side), (32, 32, 32, 255))
        for i, (_, im) in enumerate(sheet):
            out.paste(im, (i * side, 0), im)
        preview = os.path.join(ROOT, '_game_icons.png')
        out.save(preview)
        print('preview: %s' % preview)
        return

    src = argv[0] if len(argv) > 0 else SRC
    out = argv[1] if len(argv) > 1 else OUT
    im = Image.open(src).convert('RGB')
    w, h = im.size
    if w != h:
        side = max(w, h)
        pad = Image.new('RGB', (side, side), (0, 0, 0))
        pad.paste(im, ((side - w) // 2, (side - h) // 2))
        im = pad
    images = []
    for s in SIZES:
        tile = im.resize((s, s), Image.LANCZOS).convert('RGBA')
        tile.putalpha(Image.fromarray(tile_alpha(np.asarray(tile.convert('RGB')))))
        images.append(tile)
    save_ico(images, out)


if __name__ == '__main__':
    main(sys.argv[1:])
