#!/usr/bin/env bash
# 重新生成内置角色包（src/Assets/pets/*.astrapet）。素材更新后跑一遍，再把生成的包提交进仓库。
#
#   tools/pets/build.sh
#
# 小t 的素材在仓库里（characters/小t/videos）。大肥鱼的源素材 characters/deepseek/ 是第三方素材的工作目录，
# 没有提交进仓库（.gitignore），只提交打好的包；没有这个目录时跳过大肥鱼、保留仓库里现有的包。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/Assets/pets"
PACK="python3 $ROOT/tools/pack_pet.py"

$PACK "$ROOT/characters/小t/videos" "$OUT/xiaot.astrapet" \
  --id xiaot --name 小t --default \
  --author RoutinAI \
  --license "© 2026 RoutinAI，保留所有权利（见 LICENSE-ASSETS.md）" \
  --license-url "https://github.com/239573049/Astra-Pet/blob/main/LICENSE-ASSETS.md" \
  --homepage "https://routin.ai" \
  --credit "Astra Pet 官方角色：小t 和她脚边的水豚" \
  --preview "$ROOT/characters/小t/preview.png"

if [ -d "$ROOT/characters/deepseek/videos" ]; then
  $PACK "$ROOT/characters/deepseek/videos" "$OUT/dafeiyu.astrapet" \
    --id dafeiyu --name 大肥鱼 \
    --author "形象：@上善无形、@ZipZipPipe；动画：dsh-pet" \
    --license "CC BY-NC-SA 4.0（署名 · 非商业 · 相同方式共享），不属于 RoutinAI" \
    --license-url "https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hans" \
    --homepage "https://github.com/PC2005-cloud/dsh-pet" \
    --credit "形象原型「溟月」：B 站 @上善无形 https://space.bilibili.com/4456176" \
    --credit "DeepSeek 版（女仆鲸鱼娘）形象设计：B 站 @ZipZipPipe https://space.bilibili.com/4168597" \
    --credit "动画素材：dsh-pet https://github.com/PC2005-cloud/dsh-pet" \
    --credit "社区二创形象，不是 DeepSeek 官方形象；不得用于任何商业目的" \
    --license-file "$ROOT/tools/pets/dafeiyu-LICENSE.txt"
else
  echo "没有 characters/deepseek/videos，跳过大肥鱼（保留 $OUT/dafeiyu.astrapet）"
fi
