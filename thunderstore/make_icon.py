# Draws thunderstore/icon.png (256x256): a deer head in front of a server-style badge with a heart.
# python3 thunderstore/make_icon.py   (needs Pillow)
import os
from PIL import Image, ImageDraw

S = 4  # draw at 4x, then shrink for smooth edges
W = 256 * S
img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
def p(*pts):
    return [(x * S, y * S) for x, y in pts]
d.rounded_rectangle((8 * S, 8 * S, 248 * S, 248 * S), radius=40 * S, fill=(30, 46, 34, 255), outline=(92, 128, 84, 255), width=4 * S)
antler = (226, 210, 178, 255)
for side in (-1, 1):
    def x(v):
        return 128 + side * v
    w = 9 * S
    d.line(p((x(22), 92), (x(44), 58), (x(58), 30)), fill=antler, width=w, joint="curve")
    d.line(p((x(36), 70), (x(64), 62)), fill=antler, width=w)
    d.line(p((x(48), 50), (x(76), 40)), fill=antler, width=w)
    d.line(p((x(44), 58), (x(30), 34)), fill=antler, width=w)
    d.polygon(p((x(26), 100), (x(66), 84), (x(40), 116)), fill=(150, 98, 58, 255))  # ear
fur = (176, 116, 66, 255)
d.polygon(p((92, 96), (164, 96), (152, 168), (140, 206), (116, 206), (104, 168)), fill=fur)
d.ellipse(p((88, 82), (168, 140)), fill=fur)
d.ellipse(p((110, 182), (146, 214)), fill=(232, 214, 190, 255))  # muzzle
d.ellipse(p((120, 190), (136, 202)), fill=(40, 30, 26, 255))  # nose
for ex in (106, 138):
    d.ellipse(p((ex, 118), (ex + 12, 132)), fill=(30, 22, 18, 255))
# Heart badge, bottom right (tamed).
cx, cy = 200, 200
d.ellipse(p((cx - 34, cy - 34), (cx + 34, cy + 34)), fill=(30, 46, 34, 255), outline=(92, 128, 84, 255), width=4 * S)
d.ellipse(p((cx - 20, cy - 16), (cx + 1, cy + 5)), fill=(230, 70, 90, 255))
d.ellipse(p((cx - 1, cy - 16), (cx + 20, cy + 5)), fill=(230, 70, 90, 255))
d.polygon(p((cx - 19, cy - 2), (cx + 19, cy - 2), (cx, cy + 20)), fill=(230, 70, 90, 255))
img.resize((256, 256), Image.LANCZOS).save(os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon.png"))
