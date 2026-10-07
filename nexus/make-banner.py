"""Renders the Nexus page banner (nexus/images/0-banner.png) from the app's own assets: logo, fonts, theme colours
and a screenshot. Run from the repository root: python nexus/make-banner.py"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import math, random

W, H = 1920, 1080
APP = "src/DungeonsModLoader.App/Assets"
BG, BG_DEEP, PANEL, BORDER = (10, 23, 34), (6, 12, 18), (15, 34, 48), (31, 58, 73)
ACCENT, ACCENT_LIGHT, ACCENT_DARK, ACCENT_DEEP = (245, 166, 35), (255, 210, 74), (201, 100, 28), (140, 41, 18)
CYAN, TEXT, TEXT2, ON_ACCENT = (62, 195, 230), (244, 241, 232), (157, 176, 187), (36, 18, 4)

def font(name, size):
    return ImageFont.truetype(f"{APP}/Fonts/{name}.ttf", size)

img = Image.new("RGB", (W, H), BG)
# Vertical gradient + two soft glows (gold behind the title, cyan behind the screenshot).
grad = Image.new("RGB", (1, H))
for y in range(H):
    t = y / H
    grad.putpixel((0, y), tuple(int(a * (1 - t) + b * t) for a, b in zip(BG, BG_DEEP)))
img.paste(grad.resize((W, H)))
glow = Image.new("RGB", (W, H), (0, 0, 0))
g = ImageDraw.Draw(glow)
g.ellipse((80, 120, 980, 760), fill=(70, 42, 8))
g.ellipse((1150, 250, 2100, 1150), fill=(8, 48, 62))
glow = glow.filter(ImageFilter.GaussianBlur(160))
img = Image.blend(img, Image.eval(glow, lambda v: v), 0.0)  # keep base
img = Image.fromarray(__import__("numpy").clip(__import__("numpy").asarray(img, dtype=int) + __import__("numpy").asarray(glow, dtype=int), 0, 255).astype("uint8")) if False else img
# additive glow without numpy:
from PIL import ImageChops
img = ImageChops.add(img, glow)

d = ImageDraw.Draw(img, "RGBA")
# Faint pixel grid (the app's "pixel" feel).
for x in range(0, W, 48):
    d.line((x, 0, x, H), fill=(255, 255, 255, 6))
for y in range(0, H, 48):
    d.line((0, y, W, y), fill=(255, 255, 255, 6))
# Floating pixel blocks, deterministic.
rnd = random.Random(7)
for _ in range(38):
    s = rnd.choice((12, 16, 24, 32))
    x, y = rnd.randint(0, W - s), rnd.randint(0, H - s)
    col = rnd.choice((ACCENT, CYAN, ACCENT_DARK, (157, 176, 187)))
    d.rectangle((x, y, x + s, y + s), fill=col + (rnd.randint(18, 60),))

# Logo (the one asset the user allowed) + title.
logo = Image.open(f"{APP}/Images/Logo.png").convert("RGBA")
lw = 780
logo = logo.resize((lw, int(logo.height * lw / logo.width)), Image.LANCZOS)
shadow = Image.new("RGBA", logo.size, (0, 0, 0, 0))
shadow.paste((0, 0, 0, 160), (0, 0), logo)
shadow = shadow.filter(ImageFilter.GaussianBlur(14))
lx, ly = 110, 150
img.paste(shadow, (lx + 6, ly + 14), shadow)
img.paste(logo, (lx, ly), logo)

def pixel_text(x, y, text, size, fill, depth=6):
    f = font("PixelifySans-Bold", size)
    for i in range(depth, 0, -1):
        d.text((x + i, y + i), text, font=f, fill=ACCENT_DEEP if i > 2 else ACCENT_DARK)
    d.text((x, y), text, font=f, fill=fill)

pixel_text(lx + 14, ly + logo.height + 10, "MOD LOADER", 124, ACCENT_LIGHT)

tag = font("Inter-SemiBold", 40)
d.text((lx + 18, ly + logo.height + 180), "Browse, install and manage Nexus mods", font=tag, fill=TEXT)
d.text((lx + 18, ly + logo.height + 232), "for Minecraft Dungeons II.", font=tag, fill=TEXT)

# Feature chips.
chips = ["One-click install", "Enable / disable", "Profiles", "Mod updates", "Free & open source"]
cf = font("Inter-SemiBold", 28)
cx, cy = lx + 18, ly + logo.height + 320
for c in chips:
    tw = d.textlength(c, font=cf)
    if cx + tw + 44 > 960:
        cx, cy = lx + 18, cy + 66
    d.rounded_rectangle((cx, cy, cx + tw + 44, cy + 52), radius=26, fill=PANEL + (255,), outline=BORDER + (255,), width=2)
    d.text((cx + 22, cy + 10), c, font=cf, fill=TEXT2)
    cx += tw + 60

# Screenshot in a window frame, right side, slightly off the bottom-right edge.
shot = Image.open("nexus/images/2-browse.png").convert("RGB")
sw = 920
shot = shot.resize((sw, int(shot.height * sw / shot.width)), Image.LANCZOS)
frame = Image.new("RGBA", (shot.width + 4, shot.height + 4), (0, 0, 0, 0))
fd = ImageDraw.Draw(frame)
fd.rounded_rectangle((0, 0, frame.width - 1, frame.height - 1), radius=18, fill=BORDER + (255,))
mask = Image.new("L", shot.size, 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, shot.width - 1, shot.height - 1), radius=16, fill=255)
frame.paste(shot, (2, 2), mask)
fs = Image.new("RGBA", (frame.width + 120, frame.height + 120), (0, 0, 0, 0))
ImageDraw.Draw(fs).rounded_rectangle((60, 70, 60 + frame.width, 70 + frame.height), radius=22, fill=(0, 0, 0, 190))
fs = fs.filter(ImageFilter.GaussianBlur(28))
sx, sy = W - shot.width + 50, 330
img.paste(fs, (sx - 60, sy - 60), fs)
img.paste(frame, (sx, sy), frame)

# Bottom strip.
d = ImageDraw.Draw(img, "RGBA")
d.rectangle((0, H - 8, W, H), fill=ACCENT)
small = font("Inter-Medium", 26)
d.text((lx + 18, H - 60), "Windows 10 / 11  ·  Steam and Xbox app  ·  github.com/Boosterfrank/DungeonsModLoader", font=small, fill=TEXT2)

img.save("nexus/images/0-banner.png", optimize=True)
img.resize((1280, 720), Image.LANCZOS).save("nexus/images/0-banner-1280.png", optimize=True)
print("banner written", img.size)
