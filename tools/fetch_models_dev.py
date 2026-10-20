#!/usr/bin/env python3
"""从 models.dev 取模型目录，裁成 Astra 用得上的部分，写成 src/Assets/models.dev.json（内嵌进程序）。

models.dev/api.json 是各家提供商的模型目录：地址、环境变量、每个模型的上下文窗口、最大输出、
是否支持 temperature / 推理 / 工具调用、输入输出模态、价格。完整文件有 5~6 MB，这里只留：
  - 预设用到的提供商（见 WHITELIST，含它们的国际版，导入时按地址认提供商要用）
  - 对话模型（type 缺省或 chat；去掉向量、重排、生图、语音、实时）
  - 每个模型只留 id、名称、能力开关、发布日期、状态、模态、limit、cost

App 里的「更新模型库」按钮在运行时做同样的裁剪（Core/ModelCatalog.cs 的 Trim），两边的白名单和字段要保持一致。

用法：tools/fetch_models_dev.py <输出文件> [本地 api.json]
例：  tools/fetch_models_dev.py src/Assets/models.dev.json
下载用 curl（macOS 上 python.org 的 Python 没有系统根证书，urllib 会校验失败）；
给了第二个参数就直接读那个本地文件，不联网。
"""
import json
import subprocess
import sys
from datetime import datetime, timezone

SOURCE = "https://models.dev/api.json"

# 与 src/Core/ModelCatalog.cs 的 Whitelist 保持一致。
WHITELIST = [
    "deepseek", "alibaba-cn", "alibaba", "moonshotai-cn", "moonshotai", "zhipuai", "zai",
    "minimax-cn", "minimax", "volcengine", "stepfun", "stepfun-ai", "siliconflow-cn", "siliconflow",
    "modelscope", "openai", "anthropic", "google", "xai", "openrouter", "mistral", "groq", "perplexity",
]


def trim_model(m: dict) -> dict | None:
    if m.get("type", "chat") != "chat":
        return None
    modalities = m.get("modalities") or {}
    if "text" not in (modalities.get("output") or []):
        return None
    out = {"id": m["id"], "name": m.get("name", m["id"])}
    for flag in ("reasoning", "tool_call", "attachment", "temperature"):
        if flag in m:
            out[flag] = bool(m[flag])
    for key in ("release_date", "status"):
        if m.get(key):
            out[key] = m[key]
    out["modalities"] = {"input": modalities.get("input") or ["text"], "output": modalities.get("output") or ["text"]}
    limit = m.get("limit") or {}
    out["limit"] = {k: limit[k] for k in ("context", "output") if isinstance(limit.get(k), (int, float))}
    cost = m.get("cost") or {}
    cost = {k: cost[k] for k in ("input", "output") if isinstance(cost.get(k), (int, float))}
    if cost:
        out["cost"] = cost
    return out


def main() -> None:
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    if len(sys.argv) == 3:
        with open(sys.argv[2], encoding="utf-8") as f:
            full = json.load(f)
    else:
        body = subprocess.run(["curl", "-fsSL", "--max-time", "90", SOURCE], check=True, capture_output=True).stdout
        full = json.loads(body)

    result = {"_meta": {"source": SOURCE, "fetched": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}}
    total = 0
    for pid in WHITELIST:
        p = full.get(pid)
        if p is None:
            print(f"models.dev 里没有 {pid}", file=sys.stderr)
            continue
        models = [t for t in (trim_model(m) for m in p.get("models", {}).values()) if t]
        entry = {"id": pid, "name": p.get("name", pid), "npm": p.get("npm", ""), "models": models}
        for key in ("api", "doc"):
            if p.get(key):
                entry[key] = p[key]
        result[pid] = entry
        total += len(models)

    with open(sys.argv[1], "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(result) - 1} 家提供商、{total} 个对话模型 → {sys.argv[1]}")


if __name__ == "__main__":
    main()
