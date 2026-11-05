#!/usr/bin/env bash
# 重新生成内置角色包（src/Assets/pets/*.astrapet）。素材更新后跑一遍，再把生成的包提交进仓库。
#
#   tools/pets/build.sh
#
# 两个都是格式 2：动作描述、角色和人设在 tools/pets/<id>.json 里，改完重新打包。
# 仓库里打好的包就是素材来源：从现有的包读片段（预览图也沿用包里的）、套上元数据和人设重新打包。
# 要整套换片段时把第一个参数换成素材目录（小t 的原始素材在 characters/小t/videos）。
# 新补的动作（tools/pets/import_studio.py 导出的片段）先放进 characters/<角色>/life/，用 --add-clips 打进包；
# 打进去以后包里就有了，life/ 只是中转目录，不提交（.gitignore），打完可以删。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/Assets/pets"
PACK="python3 $ROOT/tools/pack_pet.py"

$PACK "$OUT/xiaot.astrapet" "$OUT/xiaot.astrapet" \
  --format 2 --meta "$ROOT/tools/pets/xiaot.json" \
  --id xiaot --name 小t --default \
  --author RoutinAI \
  --license "© 2026 RoutinAI，保留所有权利（见 LICENSE-ASSETS.md）" \
  --license-url "https://github.com/239573049/Astra-Pet/blob/main/LICENSE-ASSETS.md" \
  --homepage "https://routin.ai" \
  --credit "Astra Pet 官方角色：小t 和她脚边的水豚" \
  --add-clips "$ROOT/characters/小t/life" \
  --prompt "$ROOT/tools/pets/xiaot-prompt.md"

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
  --add-clips "$ROOT/characters/deepseek/life" \
  --prompt "$ROOT/tools/pets/dafeiyu-prompt.md"
