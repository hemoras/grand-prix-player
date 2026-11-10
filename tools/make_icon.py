import sys
from PIL import Image, ImageDraw, ImageFilter
out_ico, out_png = sys.argv[1], sys.argv[2]
RED = (225, 6, 0, 255); DARK = (21, 21, 24, 255); WHITE = (255, 255, 255, 255)

def render(size, cells):
    S = 1024  # rendu sur-échantillonné puis réduit
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # fond : carré arrondi rouge F1
    m = int(S * 0.03)
    d.rounded_rectangle([m, m, S - m, S - m], radius=int(S * 0.2), fill=RED)
    b = int(S * (0.075 if size <= 32 else 0.055))
    d.rounded_rectangle([m + b, m + b, S - m - b, S - m - b], radius=int(S * 0.2) - b, fill=(30, 30, 34, 255))
    # triangle "play", centré optiquement (légèrement à droite)
    cx, cy, h = S * 0.535, S * 0.5, S * 0.64
    w = h * 0.9
    tri = [(cx - w / 2, cy - h / 2), (cx - w / 2, cy + h / 2), (cx + w / 2, cy)]
    # damier découpé par le triangle
    check = Image.new("RGBA", (S, S), WHITE)
    cd = ImageDraw.Draw(check)
    x0, y0 = tri[0]
    cell = h / cells
    for i in range(cells + 1):
        for j in range(cells + 1):
            if (i + j) % 2 == 0:
                cd.rectangle([x0 + i * cell, y0 + j * cell, x0 + (i + 1) * cell, y0 + (j + 1) * cell], fill=DARK)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).polygon(tri, fill=255)
    # liseré blanc autour du triangle (lisibilité sur le rouge)
    border = mask.filter(ImageFilter.MaxFilter(int(S * 0.045) | 1))
    img.paste(WHITE, (0, 0), border)
    img.paste(check, (0, 0), mask)
    return img.resize((size, size), Image.LANCZOS)

sizes = [(16, 2), (20, 2), (24, 3), (32, 3), (40, 4), (48, 4), (64, 5), (128, 6), (256, 6)]
imgs = [render(s, c) for s, c in sizes]
imgs[-1].save(out_ico, format="ICO", sizes=[(s, s) for s, _ in sizes], append_images=imgs[:-1])
# aperçu : toutes les tailles sur fond clair et sombre
W = sum(s for s, _ in sizes) + 20 * len(sizes) + 20
prev = Image.new("RGBA", (W, 2 * 300), (240, 240, 240, 255))
ImageDraw.Draw(prev).rectangle([0, 300, W, 600], fill=(32, 32, 36, 255))
x = 20
for (s, _), im in zip(sizes, imgs):
    prev.paste(im, (x, 150 - s // 2), im); prev.paste(im, (x, 450 - s // 2), im); x += s + 20
prev.save(out_png)
print("ok", out_ico)
