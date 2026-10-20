"""
把处理好的素材拼成一张预览图：每段一行，按 0、2、4、6、8、10 秒各取一帧，贴在深色底上。
  tools/petgen/.venv/bin/python tools/petgen/preview.py characters/小t [输出.png]
默认写到 <角色目录>/preview.png。看一眼就知道哪段人物走样、哪段背景没抠干净、首尾帧接不接得上。
"""
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FFMPEG = "/opt/homebrew/bin/ffmpeg"
W, H = 640, 360
CROP = (150, 30, 490, 350)            # 角色所在区域（身体框 y 60..330，水平居中）
TILE = (170, 160)
SECONDS = [0, 2, 4, 6, 8, 10]
BG = (36, 36, 44, 255)


def frame_at(path: Path, second: float) -> Image.Image:
    raw = subprocess.run([FFMPEG, "-v", "error", "-c:v", "libvpx-vp9", "-i", str(path),
                          "-vf", f"select=gte(t\\,{second}),setpts=N", "-frames:v", "1",
                          "-f", "rawvideo", "-pix_fmt", "rgba", "-"], capture_output=True, check=True).stdout
    if not raw:  # 秒数超过片段长度时取最后一帧
        raw = subprocess.run([FFMPEG, "-v", "error", "-sseof", "-0.1", "-c:v", "libvpx-vp9", "-i", str(path),
                              "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "-"],
                             capture_output=True, check=True).stdout
    return Image.frombytes("RGBA", (W, H), raw[: W * H * 4])


def main() -> None:
    char_dir = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else char_dir / "preview.png"
    cfg = json.loads((char_dir / "character.json").read_text(encoding="utf-8"))
    clips = [(a, char_dir / "videos" / a["category"] / f"{a['name']}.webm") for a in cfg["actions"]]
    clips = [(a, p) for a, p in clips if p.exists()]

    label_w = 150
    sheet = Image.new("RGB", (label_w + TILE[0] * len(SECONDS), 24 + TILE[1] * len(clips)), (20, 20, 26))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("/System/Library/Fonts/Hiragino Sans GB.ttc", 16)
    except OSError:
        font = ImageFont.load_default()
    for i, s in enumerate(SECONDS):
        draw.text((label_w + i * TILE[0] + 70, 3), f"{s}s", fill=(200, 200, 200), font=font)
    for row, (action, path) in enumerate(clips):
        y = 24 + row * TILE[1]
        draw.text((10, y + 60), f"{action['category']}\n{action['name']}", fill=(230, 230, 230), font=font)
        for i, s in enumerate(SECONDS):
            tile = Image.new("RGBA", (CROP[2] - CROP[0], CROP[3] - CROP[1]), BG)
            tile.alpha_composite(frame_at(path, s).crop(CROP))
            sheet.paste(tile.resize(TILE).convert("RGB"), (label_w + i * TILE[0], y))
    sheet.save(out)
    print(f"写出 {out}（{len(clips)} 段）")


if __name__ == "__main__":
    main()
