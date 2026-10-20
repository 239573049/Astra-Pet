#!/usr/bin/env python3
"""
生成 THIRD_PARTY_NOTICES.md：读 src/obj/project.assets.json 里的全部 NuGet 包，从本机 NuGet 缓存的 .nuspec 取许可证，
再拼上手写的说明（AtomUI 的 LGPL、ffmpeg、数据来源）。依赖变了就重新跑一遍：

  dotnet restore src && python3 tools/gen_third_party.py
"""
import glob
import html
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = json.load(open(os.path.join(ROOT, "src/obj/project.assets.json")))
cache = os.path.expanduser("~/.nuget/packages")

# 这些包的 nuspec 只写了 “LICENSE 文件”，实际许可证已核对过
FILE_LICENSES = {
    "AtomUI.": "LGPL-3.0-only",
    "Avalonia.Angle.Windows.Natives": "BSD-3-Clause",
}

rows = []
for key, lib in sorted(assets["libraries"].items()):
    if lib["type"] != "package":
        continue
    name, ver = key.split("/")
    spec = glob.glob(f"{cache}/{name.lower()}/{ver}/*.nuspec")
    lic, url = "?", ""
    if spec:
        text = open(spec[0], encoding="utf-8").read()
        m = re.search(r'<license[^>]*type="expression"[^>]*>([^<]+)</license>', text)
        if m:
            lic = m.group(1)
        else:
            for prefix, known in FILE_LICENSES.items():
                if name.startswith(prefix):
                    lic = known
        m = re.search(r"<projectUrl>([^<]+)</projectUrl>", text)
        if m:
            url = html.unescape(m.group(1)).split("?")[0]
    if lic == "?":
        sys.exit(f"没找到 {name} 的许可证，请先在 FILE_LICENSES 里补上")
    rows.append((name, ver, lic, url))

table = "\n".join(f"| {n} | {v} | {l} | {u} |" for n, v, l, u in rows)
head = open(os.path.join(ROOT, "tools/third_party_head.md"), encoding="utf-8").read()
tail = open(os.path.join(ROOT, "tools/third_party_tail.md"), encoding="utf-8").read()
out = head + "\n## NuGet 依赖 / NuGet dependencies\n\n| 包 / Package | 版本 | 许可证 / License | 项目地址 |\n|---|---|---|---|\n" + table + "\n\n" + tail
open(os.path.join(ROOT, "THIRD_PARTY_NOTICES.md"), "w", encoding="utf-8").write(out)
print(f"{len(rows)} 个包 -> THIRD_PARTY_NOTICES.md")
