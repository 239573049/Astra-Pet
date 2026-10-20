#!/usr/bin/env bash
# 从 LobeHub Icons（@lobehub/icons-static-png）取提供商图标，缩成 96px 放进 src/Assets/providers。
# 有彩色版（xxx-color）就用彩色版，没有就用 light 目录里的黑色单色版（界面上垫浅色底板显示）。
# 名字后面写 :mono 表示强制用单色版：kimi 的彩色版是白色 K，垫在浅色底板上看不见。
#
# 用法：tools/fetch_provider_icons.sh <输出目录>
# 例：  tools/fetch_provider_icons.sh src/Assets/providers
#
# NextCoWork 不在 LobeHub 里，图标取自它的官网 https://nextco.work/icon.png（1024px 的应用图标，四周各有 100px 透明边，裁掉再缩）。
#
# 依赖：curl、tar、sips（macOS 自带）。图标版权归各品牌所有，LobeHub Icons 本身是 MIT 协议。
set -euo pipefail

out="${1:?用法: $0 <输出目录>}"
version="1.97.1"
# 要下载的图标名，对应 ProviderPresets 里的 Icon 字段。
icons=(
  deepseek qwen kimi:mono zhipu minimax doubao wenxin hunyuan spark stepfun baichuan yi
  siliconcloud modelscope openai claude gemini grok openrouter mistral groq perplexity
  ollama lmstudio
)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/pkg" "$out"

echo "下载 @lobehub/icons-static-png@$version"
curl -fsSL -o "$work/icons.tgz" "https://registry.npmjs.org/@lobehub/icons-static-png/-/icons-static-png-$version.tgz"
tar -xzf "$work/icons.tgz" -C "$work/pkg"

for entry in "${icons[@]}"; do
  name="${entry%%:*}"
  src="$work/pkg/package/light/$name-color.png"
  [ "$entry" = "$name:mono" ] && src=""
  [ -f "$src" ] || src="$work/pkg/package/light/$name.png"
  if [ ! -f "$src" ]; then
    echo "缺少图标: $name" >&2
    exit 1
  fi
  sips -Z 96 "$src" --out "$out/$name.png" >/dev/null
  echo "  $name <- $(basename "$src")"
done

echo "下载 NextCoWork 官网图标"
curl -fsSL -o "$work/nextcowork.png" "https://nextco.work/icon.png"
sips -c 824 824 --cropOffset 100 100 "$work/nextcowork.png" --out "$work/nextcowork-crop.png" >/dev/null
sips -Z 96 "$work/nextcowork-crop.png" --out "$out/nextcowork.png" >/dev/null
echo "  nextcowork <- nextco.work/icon.png"
