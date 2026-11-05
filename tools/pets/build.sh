#!/usr/bin/env bash
# 重新生成内置角色包（src/Assets/pets/*.astrapet）。素材更新后跑一遍，再把生成的包提交进仓库。
#
#   tools/pets/build.sh
#
# 两个都是格式 2：动作描述、角色和人设在 tools/pets/<id>.json 里，改完重新打包。
# 小t 的素材在仓库里（characters/小t/videos）。大肥鱼的源素材是第三方素材，没有提交进仓库（characters/deepseek/ 在 .gitignore 里），
# 仓库里打好的包就是它的素材来源：从现有的包读片段、套上元数据重新打包。要换片段时把第一个参数换成素材目录。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/Assets/pets"
PACK="python3 $ROOT/tools/pack_pet.py"

# 小t 的预览图源文件已经从仓库移除，包里那张沿用：从现有的包里取出来再放回去。
PREVIEW="$ROOT/characters/小t/preview.png"
if [ ! -f "$PREVIEW" ]; then
  PREVIEW="$(mktemp -d)/preview.png"
  unzip -p "$OUT/xiaot.astrapet" preview.png > "$PREVIEW"
fi

$PACK "$ROOT/characters/小t/videos" "$OUT/xiaot.astrapet" \
  --format 2 --meta "$ROOT/tools/pets/xiaot.json" \
  --id xiaot --name 小t --default \
  --author RoutinAI \
  --license "© 2026 RoutinAI，保留所有权利（见 LICENSE-ASSETS.md）" \
  --license-url "https://github.com/239573049/Astra-Pet/blob/main/LICENSE-ASSETS.md" \
  --homepage "https://routin.ai" \
  --credit "Astra Pet 官方角色：小t 和她脚边的水豚" \
  --add-clips "$ROOT/characters/小t/life" \
  --preview "$PREVIEW"

$PACK "$OUT/dafeiyu.astrapet" "$OUT/dafeiyu.astrapet" \
  --format 2 --meta "$ROOT/tools/pets/dafeiyu.json" \
  --id dafeiyu --name 大肥鱼 \
  --author "形象：@上善无形、@ZipZipPipe；动画：dsh-pet" \
  --license "CC BY-NC-SA 4.0（署名 · 非商业 · 相同方式共享），不属于 RoutinAI" \
  --license-url "https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hans" \
  --homepage "https://github.com/PC2005-cloud/dsh-pet" \
  --credit "形象原型「溟月」：B 站 @上善无形 https://space.bilibili.com/4456176" \
  --credit "DeepSeek 版（女仆鲸鱼娘）形象设计：B 站 @ZipZipPipe https://space.bilibili.com/4168597" \
  --credit "动画素材：dsh-pet https://github.com/PC2005-cloud/dsh-pet" \
  --credit "社区二创形象，不是 DeepSeek 官方形象；不得用于任何商业目的；人设和动作描述同样按 CC BY-NC-SA 4.0 发布" \
  --license-file "$ROOT/tools/pets/dafeiyu-LICENSE.txt" \
  --add-clips "$ROOT/characters/deepseek/life"
