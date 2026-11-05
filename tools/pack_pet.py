#!/usr/bin/env python3
"""
把一个角色的素材打成单个角色包（.astrapet），给程序内置或者分发给用户。只用标准库。

格式 1（原版，分类文件夹）：

  python3 tools/pack_pet.py characters/小t/videos src/Assets/pets/xiaot.astrapet \\
      --id xiaot --name 小t --default \\
      --author RoutinAI --license "© RoutinAI，保留所有权利" --license-url https://routin.ai \\
      --preview characters/小t/preview.png

格式 2（动作清单 + 人设，见 design/pet-studio/07-package-format.md）：再加 --format 2 --meta tools/pets/xiaot.json。
元数据文件里写 persona 和 actions，actions[].clips 写素材的文件名（不带 .webm）。素材来源可以是：
  - 格式 1 的素材目录（<分类>/*.webm，可带 move_strides.json、text_clips.json）；
  - 一个已有的 .astrapet（格式 1 或 2），用来给已经打好的包补元数据、重新打包。

角色包就是一个 zip：
  pet.json              清单：格式版本、id、名字、作者、许可、署名（credits）；格式 2 还有 persona、actions
  videos/<分类>/*.webm    格式 1：idle、click、drag、move、turn 必须有，sing 可选
  videos/*.json          格式 1：move_strides.json、text_clips.json（可选）
  clips/<动作 id>/*.webm  格式 2
  preview.png           预览图（可选）
  LICENSE.txt           许可说明（可选，--license-file）
  prompt.md             自定义提示词（可选，--prompt）：完整的人设，pet.json 的 prompt 指向它，程序用它代替 persona 拼出来的人设

webm 本来就是压缩过的视频，用 ZIP_STORED 原样存，json 用 deflate。条目按名字排序、时间戳固定，
同样的输入打出来的包逐字节相同，提交进仓库时 diff 干净。程序第一次用到时解压到缓存目录。
"""
import argparse
import json
import os
import re
import sys
import zipfile

REQUIRED = ["idle", "click", "drag", "move", "turn"]
OPTIONAL = ["sing"]
EXTRA_FILES = ["move_strides.json", "text_clips.json"]
FIXED_TIME = (2026, 1, 1, 0, 0, 0)
ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,31}$")
MAX_DESCRIPTION = 60


def add(zf: zipfile.ZipFile, arcname: str, data: bytes, compress: bool) -> None:
    info = zipfile.ZipInfo(arcname, FIXED_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED if compress else zipfile.ZIP_STORED
    info.external_attr = 0o644 << 16
    zf.writestr(info, data)


class Source:
    """素材来源：clip 名 → 数据、分类、步态、能否镜像。"""

    def __init__(self):
        self.clips = {}       # name -> {"data": bytes, "category": str|None, "gait": dict|None, "mirror": bool}
        self.extra = {}       # 格式 1 的 videos/*.json，name -> bytes

    @staticmethod
    def load(path: str) -> "Source":
        src = Source()
        if os.path.isdir(path):
            src._from_dir(path)
        else:
            src._from_pack(path)
        return src

    def _apply_meta_files(self, strides: dict, no_mirror: set) -> None:
        for name, clip in self.clips.items():
            gait = strides.get(name)
            clip["gait"] = gait if isinstance(gait, dict) and "stride" in gait else None
            clip["mirror"] = name not in no_mirror

    def _from_dir(self, videos: str) -> None:
        for category in REQUIRED + OPTIONAL:
            folder = os.path.join(videos, category)
            if not os.path.isdir(folder):
                continue
            for f in sorted(os.listdir(folder)):
                if f.endswith(".webm"):
                    with open(os.path.join(folder, f), "rb") as fh:
                        self.clips[f[:-5]] = {"data": fh.read(), "category": category}
        for name in EXTRA_FILES:
            p = os.path.join(videos, name)
            if os.path.isfile(p):
                with open(p, "rb") as fh:
                    self.extra[name] = fh.read()
        self._apply_meta_files(*self._parse_extra())

    def _parse_extra(self):
        strides = json.loads(self.extra["move_strides.json"]) if "move_strides.json" in self.extra else {}
        texts = json.loads(self.extra["text_clips.json"]) if "text_clips.json" in self.extra else {}
        return strides, set(texts.get("no_mirror", []))

    def _from_pack(self, pack: str) -> None:
        with zipfile.ZipFile(pack) as zf:
            manifest = json.loads(zf.read("pet.json"))
            if manifest.get("format", 1) >= 2:
                for action in manifest.get("actions", []):
                    for rel in action.get("clips", []):
                        name = os.path.splitext(os.path.basename(rel))[0]
                        self.clips[name] = {
                            "data": zf.read(rel),
                            "category": (action.get("roles") or [None])[0],
                            "gait": action.get("gait"),
                            "mirror": action.get("mirror", True),
                        }
                return
            for info in zf.infolist():
                parts = info.filename.split("/")
                if len(parts) == 3 and parts[0] == "videos" and parts[2].endswith(".webm"):
                    self.clips[parts[2][:-5]] = {"data": zf.read(info), "category": parts[1]}
                elif len(parts) == 2 and parts[0] == "videos" and parts[1] in EXTRA_FILES:
                    self.extra[parts[1]] = zf.read(info)
        self._apply_meta_files(*self._parse_extra())


def build_format1(src: Source) -> tuple:
    clips = {}
    for category in REQUIRED + OPTIONAL:
        names = sorted(n for n, c in src.clips.items() if c["category"] == category)
        if not names and category in REQUIRED:
            sys.exit(f"缺少必需的分类 {category}")
        if names:
            clips[category] = names
    files = []
    for category in sorted(clips):
        for name in clips[category]:
            files.append((f"videos/{category}/{name}.webm", src.clips[name]["data"], False))
    for name, data in src.extra.items():
        files.append((f"videos/{name}", data, True))
    summary = ", ".join(f"{k} {len(v)}" for k, v in sorted(clips.items()))
    return files, sum(len(v) for v in clips.values()), summary


def build_format2(src: Source, meta: dict, manifest: dict) -> tuple:
    actions, files, used, ids = [], [], set(), set()
    for a in meta.get("actions", []):
        aid = a.get("id", "")
        if not ID_RE.match(aid):
            sys.exit(f"动作 id 不合法：{aid!r}（小写字母、数字、短横线，1～32 个字符）")
        if aid in ids:
            sys.exit(f"动作 id 重复：{aid}")
        ids.add(aid)
        roles = a.get("roles") or []
        if not roles and not a.get("transition"):
            sys.exit(f"{aid} 没有角色")
        if len(a.get("description", "")) > MAX_DESCRIPTION:
            sys.exit(f"{aid} 的描述超过 {MAX_DESCRIPTION} 字")
        rels, gait, mirror = [], None, a.get("mirror")
        for name in a.get("clips", []):
            clip = src.clips.get(name)
            if clip is None:
                sys.exit(f"{aid} 引用的素材不存在：{name}")
            used.add(name)
            rel = f"clips/{aid}/{name}.webm"
            rels.append(rel)
            files.append((rel, clip["data"], False))
            gait = gait or clip.get("gait")
            if mirror is None and not clip.get("mirror", True):
                mirror = False
        if not rels:
            sys.exit(f"{aid} 没有片段")
        entry = {"id": aid, "name": a.get("name", ""), "description": a.get("description", ""), "roles": roles, "clips": rels}
        # 宠物体系（design/pet-life）：姿态、过渡、接触点、亲密度解锁原样带上；lines 可以有 {"bond": n, "text": …}。
        for key in ("kind", "ambient", "cooldown", "interrupt", "lines", "stance", "transition", "anchor", "unlock"):
            if key in a:
                entry[key] = a[key]
        if mirror is False:
            entry["mirror"] = False
        if a.get("gait") or gait:
            entry["gait"] = a.get("gait") or gait
        actions.append(entry)

    unused = sorted(set(src.clips) - used)
    if unused:
        sys.exit(f"这些素材没有被任何动作引用（格式 2 的包里不能有多余的片段）：{', '.join(unused)}")
    for role in REQUIRED:
        if not any(role in a["roles"] and a.get("stance", "ground") == "ground" and "transition" not in a for a in actions):
            sys.exit(f"缺少必需角色 {role} 的动作")

    if meta.get("version"):
        manifest["version"] = meta["version"]
    if meta.get("minAppVersion"):
        manifest["minAppVersion"] = meta["minAppVersion"]
    manifest["render"] = {"width": 640, "height": 360}
    if meta.get("persona"):
        manifest["persona"] = meta["persona"]
    if meta.get("profile"):
        manifest["profile"] = meta["profile"]
    manifest["actions"] = actions
    if meta.get("triggers"):
        manifest["triggers"] = meta["triggers"]
    summary = ", ".join(a["id"] for a in actions)
    return files, len(files), summary


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source", help="素材目录（<分类>/*.webm），或已有的 .astrapet")
    ap.add_argument("out", help="输出的 .astrapet 文件")
    ap.add_argument("--format", type=int, choices=[1, 2], default=1)
    ap.add_argument("--meta", help="格式 2 的元数据（persona、actions），JSON")
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
    ap.add_argument("--prompt", help="自定义提示词 .md（完整人设），打成包里的 prompt.md")
    ap.add_argument("--add-clips", action="append", default=[],
                    help="再加一个放 *.webm 的目录（文件名就是片段名），可以给多次。宠物体系的新动作用它（见 tools/pets/import_studio.py）")
    args = ap.parse_args()

    if not ID_RE.match(args.id):
        sys.exit(f"id 只能是小写字母、数字和短横线：{args.id}")
    if args.format == 2 and not args.meta:
        sys.exit("格式 2 需要 --meta")

    src = Source.load(args.source)
    for extra in args.add_clips:
        if not os.path.isdir(extra):
            continue
        for f in sorted(os.listdir(extra)):
            if f.endswith(".webm"):
                with open(os.path.join(extra, f), "rb") as fh:
                    src.clips[f[:-5]] = {"data": fh.read(), "category": None, "gait": None, "mirror": True}
    # 输出可能就是输入（给已有的包补元数据），先把素材读进内存再写。
    preview = None
    if args.preview:
        with open(args.preview, "rb") as f:
            preview = f.read()
    elif not os.path.isdir(args.source):
        with zipfile.ZipFile(args.source) as zf:
            if "preview.png" in zf.namelist():
                preview = zf.read("preview.png")
    license_text = None
    if args.license_file:
        with open(args.license_file, "rb") as f:
            license_text = f.read()
    prompt_text = None
    if args.prompt:
        with open(args.prompt, "rb") as f:
            prompt_text = f.read()
        if len(prompt_text) > 4 * 1024 * 1024:
            sys.exit("提示词文件不能超过 4 MB")

    manifest = {
        "format": args.format,
        "id": args.id,
        "name": args.name,
        "default": args.default,
        "author": args.author,
        "license": args.license,
        "licenseUrl": args.license_url,
        "homepage": args.homepage,
        "credits": args.credit,
    }
    if args.format == 2:
        with open(args.meta, encoding="utf-8") as f:
            meta = json.load(f)
        files, count, summary = build_format2(src, meta, manifest)
    else:
        files, count, summary = build_format1(src)
    if prompt_text is not None:
        manifest["prompt"] = "prompt.md"

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    tmp = args.out + ".tmp"
    with zipfile.ZipFile(tmp, "w") as zf:
        add(zf, "pet.json", json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8"), True)
        for arcname, data, compress in sorted(files, key=lambda f: f[0]):
            add(zf, arcname, data, compress)
        if preview is not None:
            add(zf, "preview.png", preview, False)
        if license_text is not None:
            add(zf, "LICENSE.txt", license_text, True)
        if prompt_text is not None:
            add(zf, "prompt.md", prompt_text, True)
    os.replace(tmp, args.out)

    size = os.path.getsize(args.out) / 1024 / 1024
    print(f"{args.out}: {args.name}，格式 {args.format}，{count} 段动画（{summary}），{size:.1f} MB")


if __name__ == "__main__":
    main()
