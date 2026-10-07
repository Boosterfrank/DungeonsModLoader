"""Renders the Nexus page header (1300x372, nexus/images/0-header.png) from the app's own assets.
Rendered at 2x and downscaled. Run from the repository root: python nexus/make-header.py"""
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont
import random

S = 2                       # render scale
W, H = 1300 * S, 372 * S
APP = "src/DungeonsModLoader.App/Assets"
BG, BG_DEEP, PANEL, BORDER = (10, 23, 34), (6, 12, 18), (15, 34, 48), (31, 58, 73)
ACCENT, ACCENT_LIGHT, ACCENT_DARK, ACCENT_DEEP = (245, 166, 35), (255, 210, 74), (201, 100, 28), (140, 41, 18)
CYAN, TEXT, TEXT2 = (62, 195, 230), (244, 241, 232), (157, 176, 187)

def font(name, size):
    return ImageFont.truetype(f"{APP}/Fonts/{name}.ttf", size * S)

img = Image.new("RGB", (W, H), BG)
grad = Image.new("RGB", (W, 1))
for x in range(W):
    t = x / W
    grad.putpixel((x, 0), tuple(int(a * (1 - t) + b * t) for a, b in zip(BG, BG_DEEP)))
img.paste(grad.resize((W, H)))

# Right half: the Browse card grid, darkened and faded into the background.
shot = Image.open("nexus/images/2-browse.png").convert("RGB").crop((252, 186, 1232, 706))   # the card grid
sh = H
shot = shot.resize((int(shot.width * sh / shot.height), sh), Image.LANCZOS)
dark = Image.new("RGB", shot.size, BG_DEEP)
shot = Image.blend(shot, dark, 0.62)
fade = Image.new("L", shot.size, 0)
fd = ImageDraw.Draw(fade)
for x in range(shot.width):
    a = max(0, min(255, int((x - shot.width * 0.42) / (shot.width * 0.40) * 255)))
    fd.line((x, 0, x, shot.height), fill=a)
img.paste(shot, (W - shot.width + 230 * S, 0), fade)

glow = Image.new("RGB", (W, H), (0, 0, 0))
g = ImageDraw.Draw(glow)
g.ellipse((-100, -200, 1500 * S // 2, H + 200), fill=(60, 36, 6))
glow = glow.filter(ImageFilter.GaussianBlur(120 * S // 2))
img = ImageChops.add(img, glow)

d = ImageDraw.Draw(img, "RGBA")
for x in range(0, W, 24 * S):
    d.line((x, 0, x, H), fill=(255, 255, 255, 5))
for y in range(0, H, 24 * S):
    d.line((0, y, W, y), fill=(255, 255, 255, 5))
rnd = random.Random(11)
for _ in range(22):
    s = rnd.choice((6, 8, 12, 16)) * S
    x, y = rnd.randint(0, W // 2), rnd.randint(0, H - s)
    d.rectangle((x, y, x + s, y + s), fill=rnd.choice((ACCENT, CYAN, ACCENT_DARK)) + (rnd.randint(20, 60),))

# Logo, left.
logo = Image.open(f"{APP}/Images/Logo.png").convert("RGBA")
lw = 440 * S
logo = logo.resize((lw, int(logo.height * lw / logo.width)), Image.LANCZOS)
shadow = Image.new("RGBA", logo.size, (0, 0, 0, 0))
shadow.paste((0, 0, 0, 170), (0, 0), logo)
shadow = shadow.filter(ImageFilter.GaussianBlur(8 * S))
lx, ly = 44 * S, (H - logo.height) // 2 - 18 * S
img.paste(shadow, (lx + 3 * S, ly + 8 * S), shadow)
img.paste(logo, (lx, ly), logo)
d = ImageDraw.Draw(img, "RGBA")

# Title block right of the logo.
tx, ty = lx + logo.width + 36 * S, 58 * S
def pixel_text(x, y, text, size, fill, depth=4):
    f = font("PixelifySans-Bold", size)
    for i in range(depth * S, 0, -1):
        d.text((x + i, y + i), text, font=f, fill=ACCENT_DEEP if i > 1 * S else ACCENT_DARK)
    d.text((x, y), text, font=f, fill=fill)
pixel_text(tx, ty, "MOD LOADER", 88, ACCENT_LIGHT)

tag = font("Inter-SemiBold", 27)
d.text((tx + 4 * S, ty + 118 * S), "Browse, install and manage Nexus mods", font=tag, fill=TEXT)
d.text((tx + 4 * S, ty + 154 * S), "for Minecraft Dungeons II  ·  free and open source", font=tag, fill=TEXT2)

cf = font("Inter-SemiBold", 18)
cx, cy = tx + 4 * S, ty + 206 * S
for c in ["One-click install", "Enable / disable", "Profiles", "Mod updates"]:
    tw = d.textlength(c, font=cf)
    d.rounded_rectangle((cx, cy, cx + tw + 28 * S, cy + 36 * S), radius=18 * S, fill=PANEL + (235,), outline=BORDER + (255,), width=S)
    d.text((cx + 14 * S, cy + 7 * S), c, font=cf, fill=TEXT2)
    cx += tw + 38 * S

d.rectangle((0, H - 5 * S, W, H), fill=ACCENT)
out = img.resize((1300, 372), Image.LANCZOS)
out.save("nexus/images/0-header.png", optimize=True)
print("header written", out.size)
