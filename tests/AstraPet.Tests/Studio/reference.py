"""
C# 后处理管线的对照基准（design/pet-studio/06 §6），由 tests/AstraPet.Tests/Studio/ProcessingGoldenTests.cs 调用。
  python reference.py blur  <in.raw> <w> <h> <channels> <radius> <out.raw>
  python reference.py resize <in.raw> <w> <h> <out_w> <out_h> <out.raw>     # RGBA → 预乘 Lanczos → RGBA
  python reference.py key   <in.raw> <w> <h> <out.raw>                       # rgb24 绿幕 → RGBA
  python reference.py process <src.mp4> <dst.webm> <category> <report.json>
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools" / "petgen"))
import process as P  # noqa: E402


def main() -> None:
    cmd, *a = sys.argv[1:]
    if cmd == "blur":
        src, w, h, c, r, dst = a[0], int(a[1]), int(a[2]), int(a[3]), float(a[4]), a[5]
        arr = np.fromfile(src, np.uint8).reshape((h, w, c) if c > 1 else (h, w))
        im = Image.fromarray(arr, "RGB" if c == 3 else "L").filter(ImageFilter.GaussianBlur(r))
        np.asarray(im).tofile(dst)
    elif cmd == "resize":
        src, w, h, ow, oh, dst = a[0], int(a[1]), int(a[2]), int(a[3]), int(a[4]), a[5]
        arr = np.fromfile(src, np.uint8).reshape(h, w, 4)
        im = Image.fromarray(arr, "RGBA").convert("RGBa").resize((ow, oh), Image.LANCZOS).convert("RGBA")
        np.asarray(im).tofile(dst)
    elif cmd == "key":
        src, w, h, dst = a[0], int(a[1]), int(a[2]), a[3]
        P.key(np.fromfile(src, np.uint8).reshape(h, w, 3)).tofile(dst)
    elif cmd == "process":
        src, dst, category, report = Path(a[0]), Path(a[1]), a[2], Path(a[3])
        r = P.process(src, dst, {"category": category})
        report.write_text(json.dumps(r))
    else:
        sys.exit(f"未知命令 {cmd}")


if __name__ == "__main__":
    main()
