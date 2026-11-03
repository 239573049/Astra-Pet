#!/usr/bin/env python3
"""
把一个角色的素材目录打成单个角色包（.astrapet），给程序内置或者分发给用户。只用标准库。

  python3 tools/pack_pet.py characters/小t/videos src/Assets/pets/xiaot.astrapet \\
      --id xiaot --name 小t --default \\
      --author RoutinAI --license "© RoutinAI，保留所有权利" --license-url https://routin.ai \\
      --preview characters/小t/preview.png

角色包就是一个 zip：
  pet.json            清单：格式版本、id、名字、作者、许可、署名（credits）
  videos/<分类>/*.webm  idle、click、drag、move、turn 必须有，sing 可选
  videos/move_strides.json、videos/text_clips.json（可选）
  preview.png         预览图（可选）
  LICENSE.txt         许可说明（可选，--license-file）

webm 本来就是压缩过的视频，用 ZIP_STORED 原样存，json 用 deflate。条目按名字排序、时间戳固定，
同样的输入打出来的包逐字节相同，提交进仓库时 diff 干净。程序第一次用到时解压到缓存目录。
"""
import argparse
import json
import os
import sys
import zipfile

FORMAT = 1
REQUIRED = ["idle", "click", "drag", "move", "turn"]
OPTIONAL = ["sing"]
EXTRA_FILES = ["move_strides.json", "text_clips.json"]
FIXED_TIME = (2026, 1, 1, 0, 0, 0)


def add(zf: zipfile.ZipFile, arcname: str, data: bytes, compress: bool) -> None:
    info = zipfile.ZipInfo(arcname, FIXED_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED if compress else zipfile.ZIP_STORED
    info.external_attr = 0o644 << 16
    zf.writestr(info, data)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("videos", help="素材目录：<分类>/*.webm，可带 move_strides.json、text_clips.json")
    ap.add_argument("out", help="输出的 .astrapet 文件")
    ap.add_argument("--id", required=True, help="角色 id，小写字母数字和短横线")
    ap.add_argument("--name", required=True, help="显示名")
    ap.add_argument("--default", action="store_true", help="内置的默认角色")
    ap.add_argument("--author", default="")
    ap.add_argument("--license", default="", help="许可的简短说明，显示在控制台里")
    ap.add_argument("--license-url", default="")
    ap.add_argument("--homepage", default="")
    ap.add_argument("--credit", action="append", default=[], help="署名的一行，可以给多次")
    ap.add_argument("--preview", help="预览图 png")
    ap.add_argument("--license-file", help="随包附带的许可说明文本")
    args = ap.parse_args()

    if not all(c.isalnum() or c == "-" for c in args.id) or args.id != args.id.lower():
        sys.exit(f"id 只能是小写字母、数字和短横线：{args.id}")

    clips = {}
    for category in REQUIRED + OPTIONAL:
        folder = os.path.join(args.videos, category)
        files = sorted(f for f in os.listdir(folder) if f.endswith(".webm")) if os.path.isdir(folder) else []
        if not files and category in REQUIRED:
            sys.exit(f"缺少必需的分类 {category}：{folder}")
        if files:
            clips[category] = files

    manifest = {
        "format": FORMAT,
        "id": args.id,
        "name": args.name,
        "default": args.default,
        "author": args.author,
        "license": args.license,
        "licenseUrl": args.license_url,
        "homepage": args.homepage,
        "credits": args.credit,
    }

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    tmp = args.out + ".tmp"
    with zipfile.ZipFile(tmp, "w") as zf:
        add(zf, "pet.json", json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8"), True)
        for category in sorted(clips):
            for name in clips[category]:
                with open(os.path.join(args.videos, category, name), "rb") as f:
                    add(zf, f"videos/{category}/{name}", f.read(), False)
        for name in EXTRA_FILES:
            path = os.path.join(args.videos, name)
            if os.path.isfile(path):
                with open(path, "rb") as f:
                    add(zf, f"videos/{name}", f.read(), True)
        if args.preview:
            with open(args.preview, "rb") as f:
                add(zf, "preview.png", f.read(), False)
        if args.license_file:
            with open(args.license_file, "rb") as f:
                add(zf, "LICENSE.txt", f.read(), True)
    os.replace(tmp, args.out)

    count = sum(len(v) for v in clips.values())
    size = os.path.getsize(args.out) / 1024 / 1024
    print(f"{args.out}: {args.name}，{count} 段动画（{', '.join(f'{k} {len(v)}' for k, v in sorted(clips.items()))}），{size:.1f} MB")


if __name__ == "__main__":
    main()
