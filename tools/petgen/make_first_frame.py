"""
把抠好的透明人物放到纯绿画布上，做成 Seedance 首尾帧模式用的那张图。
  python tools/petgen/make_first_frame.py characters/小t
读 character.json 里的 figure（透明 PNG），写 first_frame（RGB PNG）。

站位照 dsh-pet 的提示词约定：头顶在画面高度 20%，脚底在 85%，水平居中。
所有动作都用同一张首尾帧，所以每段视频里人物的大小和位置天然一致，切换时不会跳。
"""
import json
import sys
from pathlib import Path

from PIL import Image

W, H = 1280, 720
HEAD, FEET = 0.20, 0.85
GREEN = (0, 255, 0, 255)


def main(char_dir: Path) -> None:
    cfg = json.loads((char_dir / "character.json").read_text(encoding="utf-8"))
    fig = Image.open(char_dir / cfg["figure"]).convert("RGBA")
    # GPT Image 出的透明底：人物身上 alpha 只有 253，周围还有一圈 1~15 的淡雾。
    # 低于 16 归零、高于 248 补满，中间线性拉开，边缘的抗锯齿保留。
    fig.putalpha(fig.getchannel("A").point(
        lambda v: 0 if v < 16 else 255 if v > 248 else round((v - 16) * 255 / 232)))
    fig = fig.crop(fig.getchannel("A").getbbox())

    h = round(H * (FEET - HEAD))
    w = round(fig.width * h / fig.height)
    fig = fig.resize((w, h), Image.LANCZOS)

    canvas = Image.new("RGBA", (W, H), GREEN)
    canvas.alpha_composite(fig, ((W - w) // 2, round(H * HEAD)))
    out = char_dir / cfg["first_frame"]
    canvas.convert("RGB").save(out)
    print(f"写出 {out}  {W}x{H}，人物 {w}x{h}，头顶 y={round(H * HEAD)}，脚底 y={round(H * HEAD) + h}")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
