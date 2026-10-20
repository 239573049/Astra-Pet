"""
用 GPT Image 2.5 照设定图画一张标准站姿立绘（透明底），代替从设定图里直接抠图。只用标准库。
  python3 tools/petgen/make_figure.py characters/小t --variant v3 --model sunburst
  python3 tools/petgen/make_figure.py characters/小t --variant v3 --model flare --quality medium

两个模型价格相同（按 token 计费），分工：
- sunburst：编辑精度高、更能保住参考图里的人物，出正式立绘用它。
- flare：快两到四倍，用来先试提示词、出草稿。
参考图要走 /v1/images/edits（multipart 上传）；/v1/images/generations 只能传文字。

接口是 routin.ai 转发的 OpenAI 兼容接口，没有公开文档，参数照 OpenAI 官方的 GPT Image 2.5 说明：
background=transparent 配 png 出透明底；size 自定义时宽高都要是 16 的倍数。
key 先读环境变量 ROUTIN_API_KEY，没有就读 ~/.config/astra-pet/routin_api_key。
每个版本的参考图和提示词写在 character.json 的 figures 里，参考图按 refs 顺序上传，提示词里用「参考图1、参考图2」指代。
输出写到 reference/figure_<模型>_<版本>.png，选定哪张后改 character.json 的 figure 字段。
"""
import argparse
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

import petnet

API = "https://api.routin.ai/v1/images/edits"
MODELS = {"sunburst": "gpt-image-2.5-sunburst", "flare": "gpt-image-2.5-flare"}


def api_key() -> str:
    key = os.environ.get("ROUTIN_API_KEY", "").strip()
    f = Path.home() / ".config" / "astra-pet" / "routin_api_key"
    if not key and f.exists():
        key = f.read_text(encoding="utf-8").strip()
    if not key:
        sys.exit("没有 API key：设置 ROUTIN_API_KEY，或写进 ~/.config/astra-pet/routin_api_key")
    return key


def multipart(fields: dict[str, str], files: list[tuple[str, str, bytes]]) -> tuple[bytes, str]:
    boundary = uuid.uuid4().hex
    body = bytearray()
    for name, value in fields.items():
        body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
    for name, filename, data in files:
        body += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                 f"Content-Type: image/png\r\n\r\n").encode() + data + b"\r\n"
    body += f"--{boundary}--\r\n".encode()
    return bytes(body), f"multipart/form-data; boundary={boundary}"


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("char_dir", type=Path)
    p.add_argument("--model", choices=MODELS, default="sunburst")
    p.add_argument("--quality", default="high", help="low / medium / high / xhigh / max / auto")
    p.add_argument("--size", default="1024x1536")
    p.add_argument("--variant", required=True, help="character.json 里 figures 的键，比如 v2、v3")
    args = p.parse_args()

    cfg = json.loads((args.char_dir / "character.json").read_text(encoding="utf-8"))
    figure = cfg["figures"][args.variant]
    refs = [args.char_dir / r for r in figure["refs"]]
    out = args.char_dir / "reference" / f"figure_{args.model}_{args.variant}.png"

    body, content_type = multipart(
        {
            "model": MODELS[args.model],
            "prompt": figure["prompt"],
            "background": "transparent",
            "output_format": "png",
            "quality": args.quality,
            "size": args.size,
            "n": "1",
        },
        [("image[]", r.name, r.read_bytes()) for r in refs],
    )
    req = urllib.request.Request(API, data=body, method="POST", headers={
        "Content-Type": content_type,
        "Authorization": f"Bearer {api_key()}",
    })
    started = time.time()
    try:
        with petnet.urlopen(req, 600) as resp:
            result = json.loads(resp.read())
    except urllib.error.HTTPError as e:
        sys.exit(f"HTTP {e.code}: {e.read().decode(errors='replace')[:2000]}")

    item = result["data"][0]
    if item.get("b64_json"):
        out.write_bytes(base64.b64decode(item["b64_json"]))
    else:
        petnet.download(item["url"], out)
    usage = result.get("usage")
    print(f"{MODELS[args.model]} {args.quality} 用时 {time.time() - started:.0f}s → {out}" + (f"  usage {usage}" if usage else ""))


if __name__ == "__main__":
    main()
