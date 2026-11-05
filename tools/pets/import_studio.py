#!/usr/bin/env python3
"""
把工作台导出的角色包里的「宠物体系」动作并进内置角色（design/pet-life/08-builtin-clips.md）。

  python3 tools/pets/import_studio.py <工作台工程目录 或 导出的.astrapet> <片段目录> tools/pets/<角色>.json [--only 动作id ...]

推荐直接给工程目录（<数据目录>/studio/<工程 id>/，工作台首页「打开文件夹」能看到）：只拿 ⑥ 里已经采用的片段，
不用为了导出把五个基础动作也生成一遍。给导出的包也行。

做三件事：
  1. 包里的 clips/<动作id>/a.webm 拷成 <片段目录>/<动作id>.webm（build.sh 用 --add-clips 把这个目录打进包）；
  2. 动作定义（名字、描述、角色、stance / transition / anchor / gait / lines）合并进元数据 json 的 actions：
     同 id 的替换，新的追加，clips 写成 [动作id]；
  3. 包里 profile.body（工作台从待机、贴墙待机、倒挂待机量出来的身体范围和接触点）合并进元数据的 profile.body。
默认只导入宠物体系的动作（不在地面姿态的、过渡动作，以及 eat/fall/land/dizzy/hungry/yawn/pounce 角色的），
不动角色原来的五类必需动作。--only 可以指定只导哪些 id。
"""
import argparse
import json
import os
import sys
import zipfile

LIFE_ROLES = {"eat", "fall", "land", "dizzy", "hungry", "yawn", "pounce", "climb", "sleep"}
KEYS = ("name", "description", "roles", "stance", "transition", "anchor", "gait", "lines", "kind", "ambient", "mirror", "unlock")


def is_life(action: dict) -> bool:
    return (action.get("stance", "ground") != "ground" or "transition" in action
            or bool(LIFE_ROLES & set(action.get("roles", []))))


DEFAULT_STRIDE, DEFAULT_CLIMB_STRIDE = 220, 100


def read_project(directory: str):
    """工作台工程 → (manifest 形状的动作列表, {相对名: 片段字节}, profile.body)，规则同 StudioEngine.BuildSpec。"""
    with open(os.path.join(directory, "project.json"), encoding="utf-8") as fh:
        data = json.load(fh)

    def report(a):
        p = os.path.join(directory, "qa", a["id"], f"take-{a['adopted']:03d}.json")
        if not os.path.isfile(p):
            return {}
        with open(p, encoding="utf-8") as fh:
            return json.load(fh)

    actions, clips, body = [], {}, {}
    for a in data.get("actions", []):
        if a.get("adopted") is None:
            continue
        clip = os.path.join(directory, "clips", a["id"], f"take-{a['adopted']:03d}.webm")
        if not os.path.isfile(clip):
            continue
        stance, to, roles = a.get("stance", "ground"), a.get("to"), a.get("roles", [])
        r = report(a)
        entry = {"id": a["id"], "name": a.get("name", ""), "description": a.get("description", "")[:60], "roles": roles}
        if to:
            entry["transition"] = {"from": stance, "to": to}
        elif stance != "ground":
            entry["stance"] = stance
        if a.get("lines"):
            entry["lines"] = a["lines"]
        if a.get("mirror") is False:
            entry["mirror"] = False
        if "move" in roles or "climb" in roles:
            gait = {"stride": a.get("stride") or (DEFAULT_CLIMB_STRIDE if "climb" in roles and stance == "wall" else DEFAULT_STRIDE)}
            if stance != "ceiling" and r.get("curve"):
                gait["curve"] = [round(v, 4) for v in r["curve"]]
            if a.get("vertical"):
                gait["axis"] = "y"
            entry["gait"] = gait
        contact = r.get("contactX") if (stance == "wall" and not to) else r.get("lastContactX") if to == "wall" else None
        if contact is not None:
            entry["anchor"] = {"contactX": round(contact, 1)}
        rel = f"clips/{a['id']}/a.webm"
        entry["clips"] = [rel]
        with open(clip, "rb") as fh:
            clips[rel] = fh.read()
        actions.append(entry)
        if not to and "idle" in roles and r.get("bounds"):
            if stance == "wall":
                body["wall"] = {"contactX": round(r.get("contactX", 113), 1), "feetY": r["bounds"][3]}
            elif stance == "ceiling":
                body["ceiling"] = {"contactY": r["bounds"][1]}
    return actions, clips, body


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pack")
    ap.add_argument("clips_dir")
    ap.add_argument("meta")
    ap.add_argument("--only", nargs="*", help="只导入这些动作 id")
    args = ap.parse_args()

    if os.path.isdir(args.pack):
        all_actions, files, body = read_project(args.pack)
    else:
        with zipfile.ZipFile(args.pack) as zf:
            manifest = json.loads(zf.read("pet.json"))
            if manifest.get("format", 1) < 2:
                sys.exit("不是格式 2 的包（工作台导出的才是）")
            all_actions = manifest.get("actions", [])
            files = {rel: zf.read(rel) for a in all_actions for rel in a.get("clips", [])}
            body = (manifest.get("profile") or {}).get("body") or {}
    actions = [a for a in all_actions if (a["id"] in args.only if args.only else is_life(a))]
    if not actions:
        sys.exit("没有要导入的动作（工程目录要在 ⑥ 里采用过片段）")
    os.makedirs(args.clips_dir, exist_ok=True)
    for a in actions:
        clips = a.get("clips") or []
        if len(clips) != 1:
            sys.exit(f"{a['id']} 应该正好有一个片段，实际 {len(clips)} 个")
        with open(os.path.join(args.clips_dir, a["id"] + ".webm"), "wb") as fh:
            fh.write(files[clips[0]])

    with open(args.meta, encoding="utf-8") as fh:
        meta = json.load(fh)
    existing = meta.setdefault("actions", [])
    for a in actions:
        entry = {"id": a["id"], **{k: a[k] for k in KEYS if k in a}, "clips": [a["id"]]}
        entry.setdefault("roles", [])
        for i, old in enumerate(existing):
            if old["id"] == a["id"]:
                existing[i] = entry
                break
        else:
            existing.append(entry)

    if body:
        profile = meta.setdefault("profile", {})
        merged = profile.setdefault("body", {})
        # 站姿包围盒保留原角色的（原来的片段不是工作台做的）；贴墙、倒挂的接触点用新量的。
        for key in ("wall", "ceiling"):
            if key in body:
                merged[key] = body[key]

    with open(args.meta, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(meta, ensure_ascii=False, indent=2) + "\n")
    print(f"导入 {len(actions)} 个动作：{', '.join(a['id'] for a in actions)}")
    print(f"片段在 {args.clips_dir}；接着跑 tools/pets/build.sh 重新打包")


if __name__ == "__main__":
    main()
