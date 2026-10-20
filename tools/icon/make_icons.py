#!/usr/bin/env python3
"""
从角色立绘生成各平台的应用图标，写到 src/Assets/icons/：

  app.png       512x512，圆角方块，Linux 图标和窗口图标用
  app.ico       Windows：16/24/32/48/64/128/256 多尺寸
  AppIcon.icns  macOS：用系统的 iconutil 生成（只能在 macOS 上跑）

图标是「小t」的头像（来自 characters/小t/reference/figure.png，透明底）放在浅蓝渐变的圆角方块上。
形象版权归 RoutinAI，见 LICENSE-ASSETS.md。重新生成：python3 tools/icon/make_icons.py
"""
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FIGURE = os.path.join(ROOT, "characters/小t/reference/figure.png")
OUT = os.path.join(ROOT, "src/Assets/icons")

SS = 4  # 超采样倍数，缩小时边缘才平滑


def squircle_mask(size: int, radius_ratio: float = 0.2237) -> Image.Image:
    """圆角方块遮罩；0.2237 是 macOS 图标的圆角比例。"""
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size - 1, size - 1), radius=int(size * radius_ratio), fill=255)
    return m


def render(size: int, inset: float) -> Image.Image:
    """画一个 size 大小的图标。inset 是四周留白占边长的比例（macOS 要留 10%，其他平台不留）。"""
    big = size * SS
    body = int(big * (1 - 2 * inset))
    off = (big - body) // 2

    # 渐变背景：上浅下深的蓝
    bg = Image.new("RGB", (body, body))
    px = bg.load()
    top, bottom = (222, 236, 255), (158, 190, 238)
    for y in range(body):
        t = y / (body - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(body):
            px[x, y] = c
    # 头像后面一团柔光，让头发和背景分开
    glow = Image.new("L", (body, body), 0)
    ImageDraw.Draw(glow).ellipse((body * 0.12, body * 0.10, body * 0.88, body * 0.86), fill=150)
    glow = glow.filter(ImageFilter.GaussianBlur(body * 0.08))
    bg = Image.composite(Image.new("RGB", (body, body), (246, 250, 255)), bg, glow)

    # 立绘裁出头部和肩膀：脸大约在裁剪框的 (49%, 58%)
    fig = Image.open(FIGURE).convert("RGBA")
    head = fig.crop((175, 15, 855, 695))
    scale = body * 1.02 / head.width
    head = head.resize((int(head.width * scale), int(head.height * scale)), Image.LANCZOS)
    canvas = bg.convert("RGBA")
    canvas.alpha_composite(head, ((body - head.width) // 2, int(body * 0.045)))

    out = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    mask = squircle_mask(body)
    out.paste(canvas, (off, off), mask)
    return out.resize((size, size), Image.LANCZOS)


def main() -> None:
    os.makedirs(OUT, exist_ok=True)

    render(512, 0.0).save(os.path.join(OUT, "app.png"))

    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    largest = render(256, 0.0)
    largest.save(os.path.join(OUT, "app.ico"), sizes=[(s, s) for s in ico_sizes])

    if sys.platform == "darwin" and shutil.which("iconutil"):
        with tempfile.TemporaryDirectory() as tmp:
            iconset = os.path.join(tmp, "AppIcon.iconset")
            os.makedirs(iconset)
            for base in (16, 32, 128, 256, 512):
                render(base, 0.10).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
                render(base * 2, 0.10).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
            subprocess.run(["iconutil", "-c", "icns", iconset, "-o", os.path.join(OUT, "AppIcon.icns")], check=True)
    else:
        print("不是 macOS，跳过 AppIcon.icns（用仓库里已有的那份）")

    for name in sorted(os.listdir(OUT)):
        print(f"{name:14} {os.path.getsize(os.path.join(OUT, name)):>8} 字节")


if __name__ == "__main__":
    main()
