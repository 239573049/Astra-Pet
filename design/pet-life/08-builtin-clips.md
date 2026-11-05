# 08 内置角色补片段 · 跨平台验收

代码已经支持宠物体系的全部动作（07 的 P1～P4），但内置的小t、大肥鱼还没有这些动作的片段：
现在它们不爬墙、不坐、不睡、不倒挂（姿态图里没有这些姿态，自动降级），吃东西 / 下落 / 落地 / 晕 / 饿 / 打哈欠走回退链。
片段要用工作台调付费的视频模型生成，由人生成和验收（决定：2026-11-05，用户自己在工作台里做）。本文是清单和步骤。

## 1. 要生成什么

每只角色一样的清单（模板 id 就是工作台 ④「宠物体系的动作」里的按钮，时长是模板默认值）：

| 组 | 动作（模板 id，秒） | 关键帧（③） | 小计 |
|---|---|---|---|
| 地面常用 | eat 8、fall 4、land 3、dizzy 6、hungry 6、yawn 6 | 站姿（已有） | 33 秒 |
| 墙组 | wall-idle 8、climb 6、wall-enter 4、wall-exit 4、let-go 3 | 贴墙 | 25 秒 |
| 坐 | sit-enter 4、sit-idle 8、sit-exit 4 | 坐 | 16 秒 |
| 睡 | sleep-enter 6、sleep-loop 8、sleep-exit 6（要先有坐） | 睡 | 20 秒 |
| 倒挂 | ceiling-idle 8、ceiling-crawl 6、ceiling-enter 4、ceiling-exit 4、ceiling-drop 3（要先有墙组） | 倒挂 | 25 秒 |

地面常用和墙组在工作台里是必需的（L13），至少要做这两组：约 58 秒，720p 约 87 元。全部五组一只约 120 秒视频，按 `characters/小t/README.md` 记的单价，720p 约 180 元、480p 约 80 元（以账单为准）；关键帧 4 张图另算。
**推荐顺序**：先只做「地面常用」里的 eat + fall（喂食和甩是最常用的），再做墙组（整组做完才能用），坐 / 睡 / 倒挂最后。
每组先只生成一段（比如贴墙待机）看效果，满意再批量。

## 2. 小t

1. 控制台「角色 → 打开角色工作台」，新建工程「小t（宠物体系）」。
2. ① 描述照 `characters/小t/character.json` 填；伙伴填「脚边一只头顶小番茄的水豚」；参考图上传 `characters/小t/reference/sheet.png`、`front_view.png`。
   性格与口味照 `tools/pets/xiaot.json` 的 `profile` 填（活泼 65、黏人 75、贪吃 60、胆量 40、好奇 70；喜欢蛋糕、饼干、牛奶，讨厌青菜）。
3. ② 不生成，「上传…」`characters/小t/reference/sheet.png` 并选定；③ 不生成，「上传…」`characters/小t/reference/figure.png` 并选定。
   看一下合成的首尾帧和仓库里的 `characters/小t/reference/first_frame.png` 是否一致（脚底线、大小）。
4. ③「其他姿态」依次生成贴墙、坐、睡、倒挂的关键帧，各选一张。贴墙的检查提示「左侧不齐」就重新生成。
   水豚：贴墙、倒挂时让它趴在小t背上或者干脆不出现，在关键帧提示词末尾补一句（模板默认「只画这一个角色」）。
5. ④ 保留基础套装（工作台要求五个必需角色才能确认规划），但**不生成它们**——小t 已经有这些片段；
   点「+ 地面常用」「+ 墙组」等按组加宠物体系的动作，确认规划。
6. ⑤ 只对宠物体系的动作逐个点「生成这一段」（不要点「生成全部」），⑥ 处理并采用（贴墙看「接触线晃动」，过渡动作首尾不同是正常的），
   这种「只补动作」的做法下工作台的 ⑦ 试玩和 ⑧ 导出用不了（它们要求每个动作都有采用的片段，包括没生成的基础套装），
   试玩放到第 9 步：打进内置包以后在桌面上试。
7. **不用导出**：直接从工程目录导入已经采用的片段（工程目录在 `<数据目录>/studio/<工程 id>/`，工作台首页能打开）：
   ```
   python3 tools/pets/import_studio.py <工程目录> characters/小t/life tools/pets/xiaot.json
   ```
   只导入宠物体系的动作（非地面姿态、过渡，以及 eat / fall / land / dizzy / hungry / yawn / pounce / climb / sleep 角色），
   片段拷成 `characters/小t/life/<动作id>.webm`；动作定义（姿态、过渡、步态、接触点）和量出来的贴墙 / 倒挂身体数据合并进 `tools/pets/xiaot.json`。
   规则和工作台导出（`StudioEngine.BuildSpec`）一致；给导出的 `.astrapet` 也可以。
8. 把 `tools/pets/xiaot.json` 的 `version` 加一，跑 `tools/pets/build.sh`（它用 `--add-clips` 把 `characters/小t/life` 打进包）。
9. 验收：`dotnet test`（PetPackTests 会加载内置包）、启动后控制台「位置」页「允许爬墙」可用、「去爬墙」能爬上去；「状态」页喂食播吃东西的片段。

## 3. 大肥鱼

源素材是第三方的（`characters/deepseek/` 不在仓库里），仓库里的包就是素材来源；**形象按 CC BY-NC-SA 4.0 授权**，新片段同样按这个许可发布，
导出时许可选「CC BY-NC-SA 4.0」，署名沿用 `tools/pets/build.sh` 里的。

1. 新建工程「大肥鱼（宠物体系）」，① 照 `tools/pets/dafeiyu.json` 的 persona 和 profile 填（活泼 70、黏人 65、贪吃 80、胆量 70、好奇 55；喜欢小鱼干、冰淇淋，讨厌青菜）。
2. ② ③ 没有现成的设定图和站姿：用 `characters/deepseek/`（有的话）里的原图上传；没有就从包里 `idle-breathe` 片段截一帧（640×360 透明）放大后上传当站姿——清晰度不够，界面会提示，最好找原作者的立绘。
3. 其余同小t 的 4～6。
4. 并回：
   ```
   python3 tools/pets/import_studio.py <工程目录> characters/deepseek/life tools/pets/dafeiyu.json
   tools/pets/build.sh
   ```
   第一次打包后片段就在 `src/Assets/pets/dafeiyu.astrapet` 里了，以后重打不再需要 `characters/deepseek/life`。

## 4. Windows / Linux 验收（CI 或真机）

本机（macOS）只能确认这两个目标能编译（`dotnet build src/AstraPet.App -r win-x64` / `-r linux-x64` 都通过）；
新写的原生调用（窗口枚举、全局快捷键）没在这两个系统上跑过。在真机或 CI 的 release 构建上按下面验收：

| # | 步骤 | 预期 |
|---|---|---|
| 1 | `AstraPet --smoke`（release / NativeAOT 构建） | 输出 `smoke ok` |
| 2 | 新装（删掉数据目录）启动 | 宠物站在任务栏上方（Linux：面板上方）；日志「窗口位置」的 y + 窗口高 × 330/360 ≈ 工作区底边 |
| 3 | 拖到半空松手 / 快速甩 | 掉下来 / 飞出去撞屏幕边反弹再落地；日志有 `身体：pet.thrown`、`身体：pet.landed` |
| 4 | 控制台「位置」页 | 「可以站在窗口上」「召唤快捷键」两个开关可用（Linux Wayland 会话下置灰并有说明） |
| 5 | 打开「可以站在窗口上」，开一个普通窗口（不要最大化），把宠物拖到它上方松手 | 落在窗口顶边上；拖动那个窗口，宠物跟着走；最小化窗口，宠物掉下去 |
| 6 | 别的程序在前台时按 Ctrl+Alt+P | 宠物出现在光标下方的地面上，说「我来啦！」之类；关掉开关后按无反应 |
| 7 | 多显示器、150% / 200% 缩放 | 3、5 的位置都对（Windows 的窗口坐标是物理像素，和 Avalonia 一致；有偏差先看 DPI 感知设置） |
| 8 | 资源占用 | 打开「站在窗口上」后 CPU 不明显上升（每半秒枚举一次窗口） |

已知限制：Windows 的召唤快捷键是轮询三个键的按下状态，被别的程序注册成系统热键时两边都会响应；
Linux 只支持 X11（`_NET_CLIENT_LIST_STACKING` 要窗口管理器支持 EWMH，大多数都支持）。
