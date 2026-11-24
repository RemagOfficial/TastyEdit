# Build the Windows application icon from the source artwork.
#
# A .ico is a container of pre-scaled bitmaps; Windows picks the one closest to the size it needs
# (title bar 16, taskbar/alt-tab 24-32, Explorer small 48, large 256). Shipping only the 256 makes
# the small sizes a blurry downscale of the whole picture, so every size is rendered from the
# original instead. Pillow writes the 32-bit (RGBA) entries, which is what the apphost embeds.
#
# usage:  python tools/make_icon.py [source.png] [out.ico]

import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, 'assets', 'app-icon.png')
OUT = os.path.join(ROOT, 'src', 'TastyEditor.App', 'app.ico')
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def main(argv):
    src = argv[0] if len(argv) > 0 else SRC
    out = argv[1] if len(argv) > 1 else OUT
    im = Image.open(src).convert('RGBA')

    # Square canvas: pad the short side rather than squash the artwork.
    w, h = im.size
    if w != h:
        side = max(w, h)
        pad = Image.new('RGBA', (side, side), (0, 0, 0, 0))
        pad.paste(im, ((side - w) // 2, (side - h) // 2))
        im = pad
    # The largest entry the container can carry is 256.
    if im.size[0] > 256:
        im = im.resize((256, 256), Image.LANCZOS)

    os.makedirs(os.path.dirname(out), exist_ok=True)
    im.save(out, format='ICO', sizes=[(s, s) for s in SIZES if s <= im.size[0]])
    print('wrote %s (%d bytes) from %s' % (out, os.path.getsize(out), src))
    with open(out, 'rb') as f:
        data = f.read()
    print('entries:', data[4:6].hex())


if __name__ == '__main__':
    main(sys.argv[1:])
