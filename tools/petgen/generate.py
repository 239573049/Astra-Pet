"""
用火山方舟 Seedance 的首尾帧模式，按 character.json 批量生成动作视频，下载到 raw/<分类>/<名字>.mp4。
只用标准库。

  python3 tools/petgen/generate.py characters/小t --dry-run          # 只打印请求和费用估算，不花钱
  python3 tools/petgen/generate.py characters/小t --only 待机呼吸      # 只生成指定动作
  python3 tools/petgen/generate.py characters/小t                    # 生成所有还没下载的动作

API key 先读环境变量 ARK_API_KEY，没有就读 ~/.config/astra-pet/ark_api_key。脚本不会打印 key。

提交成功后先把任务 ID 写进 raw/<分类>/<名字>.task.json 再查询。中途断了重跑，会接着查同一个任务，
不会重复提交（每次提交都要付费）。要重新生成某个动作，删掉它的 .mp4 和 .task.json 再跑。
视频链接只有 24 小时有效，任务成功后立刻下载。

首尾帧都用同一张图，这样每段都从同一个站姿开始、回到同一个站姿结束，片段之间能无缝衔接。
首帧、首尾帧、全能参考三种模式不能混用，所以这里不传设定图做 reference_image。
"""
import argparse
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import petnet

API = "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks"
POLL_SECONDS = 10
# 第三方文章转述的官方示例（16:9、无视频输入），只用于估算，以账单为准。
YUAN_PER_SECOND = {"480p": 0.67, "720p": 1.51}


def api_key() -> str:
    key = os.environ.get("ARK_API_KEY", "").strip()
    if not key:
        f = Path.home() / ".config" / "astra-pet" / "ark_api_key"
        if f.exists():
            key = f.read_text(encoding="utf-8").strip()
    if not key:
        sys.exit("没有 API key：设置环境变量 ARK_API_KEY，或写进 ~/.config/astra-pet/ark_api_key")
    return key


def call(method: str, url: str, key: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Content-Type": "application/json",
        "Authorization": f"Bearer {key}",
    })
    try:
        with petnet.urlopen(req, 60) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"HTTP {e.code}: {e.read().decode(errors='replace')}") from None


def build_request(cfg: dict, action: dict, image_url: str) -> dict:
    return {
        "model": cfg["model"],
        "content": [
            {"type": "text", "text": cfg["prompt_prefix"] + action["prompt"]},
            {"type": "image_url", "image_url": {"url": image_url}, "role": "first_frame"},
            {"type": "image_url", "image_url": {"url": image_url}, "role": "last_frame"},
        ],
        # 文档写 2.5 的首帧类任务只认 adaptive；首尾帧图本身是 16:9，输出也就是 16:9。
        "ratio": "adaptive",
        "resolution": cfg["resolution"],
        "duration": cfg["duration"],
        "generate_audio": False,
        "watermark": False,
    }


def redacted(body: dict) -> dict:
    body = json.loads(json.dumps(body))
    for item in body["content"]:
        if item["type"] == "image_url":
            item["image_url"]["url"] = item["image_url"]["url"][:40] + "…(base64 省略)"
    return body


def main() -> None:
    sys.stdout.reconfigure(line_buffering=True)  # 后台运行时也能实时看到进度
    p = argparse.ArgumentParser()
    p.add_argument("char_dir", type=Path)
    p.add_argument("--only", nargs="*", help="只生成这些名字的动作")
    p.add_argument("--dry-run", action="store_true", help="只打印请求和费用估算")
    args = p.parse_args()

    char_dir = args.char_dir
    cfg = json.loads((char_dir / "character.json").read_text(encoding="utf-8"))
    image = char_dir / cfg["first_frame"]
    image_url = "data:image/png;base64," + base64.b64encode(image.read_bytes()).decode()

    todo = []
    for action in cfg["actions"]:
        if args.only and action["name"] not in args.only:
            continue
        out = char_dir / "raw" / action["category"] / f"{action['name']}.mp4"
        if out.exists():
            print(f"跳过 {action['category']}/{action['name']}：已下载")
            continue
        todo.append((action, out))

    seconds = cfg["duration"] * len(todo)
    rate = YUAN_PER_SECOND.get(cfg["resolution"])
    estimate = f"约 {seconds * rate:.0f} 元" if rate else "单价未知"
    print(f"待生成 {len(todo)} 段 × {cfg['duration']} 秒，{cfg['resolution']}，估算 {estimate}（第三方转述的单价，以账单为准）")

    if args.dry_run:
        for action, _ in todo:
            print(json.dumps(redacted(build_request(cfg, action, image_url)), ensure_ascii=False, indent=2))
        return
    if not todo:
        return

    key = api_key()
    pending = {}
    for action, out in todo:
        task_file = out.with_suffix(".task.json")
        out.parent.mkdir(parents=True, exist_ok=True)
        if task_file.exists():
            task = json.loads(task_file.read_text(encoding="utf-8"))
            if task.get("status") not in ("failed", "cancelled", "expired"):
                print(f"继续查询 {action['name']}：{task['id']}")
                pending[task["id"]] = (action, out, task_file)
                continue
        body = build_request(cfg, action, image_url)
        resp = call("POST", API, key, body)
        task = {"id": resp["id"], "status": "submitted", "request": redacted(body), "submitted_at": time.time()}
        task_file.write_text(json.dumps(task, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"已提交 {action['category']}/{action['name']}：{resp['id']}")
        pending[resp["id"]] = (action, out, task_file)

    total_tokens = 0
    while pending:
        time.sleep(POLL_SECONDS)
        for task_id in list(pending):
            action, out, task_file = pending[task_id]
            resp = call("GET", f"{API}/{task_id}", key)
            status = resp.get("status")
            task = json.loads(task_file.read_text(encoding="utf-8"))
            task.update(status=status, response=resp)
            task_file.write_text(json.dumps(task, ensure_ascii=False, indent=2), encoding="utf-8")
            if status == "succeeded":
                tmp = out.with_suffix(".part")
                petnet.download(resp["content"]["video_url"], tmp)
                tmp.rename(out)
                tokens = (resp.get("usage") or {}).get("completion_tokens", 0)
                total_tokens += tokens
                print(f"完成 {action['category']}/{action['name']} → {out}（{tokens} tokens）")
                del pending[task_id]
            elif status in ("failed", "cancelled", "expired"):
                print(f"失败 {action['category']}/{action['name']}：{status} {resp.get('error')}")
                del pending[task_id]
        if pending:
            print(f"  还在生成 {len(pending)} 段…")
    print(f"本次消耗 {total_tokens} tokens")


if __name__ == "__main__":
    main()
