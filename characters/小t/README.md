# 小t 桌宠素材

从 `reference/sheet.png`（角色设定图）生成的一套桌宠动作：小t，脚边常驻她的宠物水豚。

流程照 dsh-pet（github.com/PC2005-cloud/dsh-pet）的做法：同一张标准站姿图同时做首帧和尾帧，用 Seedance 生成纯绿底的 10 秒动作，抠绿后统一大小，编码成 640×360 的 VP9 带 alpha webm。

## 目录

```
character.json             模型、分辨率、时长、各版本立绘的参考图和提示词、视频公共提示词、每个动作的提示词
reference/sheet.png        原始设定图
reference/front_view.png   从设定图三视图裁出的正面像，给立绘定 Q 版比例用
reference/capybara.png     从设定图左下角裁出的水豚
reference/figure.png       正在用的立绘：小t + 脚边的水豚，透明底
reference/first_frame.png  1280×720 纯绿底首尾帧，头顶在 20% 高度、脚底在 85%
raw/<分类>/<名字>.mp4       Seedance 原始输出；同名 .task.json 是任务记录
videos/                    桌宠程序用的素材目录（ASTRA_PET_VIDEOS 指向它）：<分类>/*.webm、move_strides.json、text_clips.json
preview.png                全部动作的抽帧预览，preview.py 生成
```

## 在桌宠里使用

把整个角色文件夹拷进桌宠的角色目录（macOS：`~/Library/Application Support/AstraPet/characters/`，Windows：`%APPDATA%\AstraPet\characters\`），在控制台「桌宠 → 角色」里点「刷新」就能选。控制台里的「打开目录」按钮会直接打开这个文件夹。程序只读 `videos/` 和 `character.json` 的 `name`，`raw/`、`reference/` 不用拷。

目录里只放在用的文件。试过的旧版本（480p 全套、没加水豚的单人待机、720p 第一次生成里不合格的两段、被淘汰的几版立绘）已经删除，下面只留结论。

## 立绘的几个版本

| 文件 | 说明 |
|---|---|
| figure.png | 单人立绘 v2 加上脚边的水豚，女孩保持不变。**正在用** |

已经删除的版本和淘汰原因：

- 直接用 Vision 从设定图左侧大图抠出来的：张嘴大笑、双臂张开，不适合做每段的起止帧。
- 第一轮只给设定图，Sunburst 和 Flare 都画成了 5 到 6 头身，太高。所以 v2 加了三视图正面像来定比例。
- v2（Sunburst）：Q 版约 4 头身，头部占全身约 36%，设定图正面像约 38%。它只是生成 v3 的中间产物，v3 定稿后删了。要重做 v3，先跑 `make_figure.py "$C" --variant v2 --model sunburst` 重新生成 v2，再跑 v3。
- v3 的 Flare 版：水豚画成了正面还闭着一只眼，和设定图差得比较多。

`make_figure.py` 把新生成的立绘写到 `reference/figure_<模型>_<版本>.png`。选定哪张，就把它改名成 `figure.png`（`character.json` 的 `figure` 指向它），其余的删掉，这样文件名里不带版本号。

模型分工：Sunburst 编辑精度高、更能保住参考图里的人物，用来出正式立绘；Flare 快两到四倍、价格相同，适合先试提示词。high 质量每张约 0.028 美元。

## 步骤

工具在仓库的 `tools/petgen/`。`generate.py` 和 `make_figure.py` 只用标准库，其余脚本用 `tools/petgen/.venv` 里的 numpy 和 Pillow。

```bash
cd /Users/token/code/Astra-Pet
C="characters/小t"

# 1. 立绘和首尾帧（已做好，换角色或改站姿时才需要重跑）
python3 tools/petgen/make_figure.py "$C" --variant v3 --model flare --quality medium   # 试提示词
python3 tools/petgen/make_figure.py "$C" --variant v3 --model sunburst                 # 出正式立绘，选定后改名成 reference/figure.png
tools/petgen/.venv/bin/python tools/petgen/make_first_frame.py "$C"

# 2. 生成视频（要花钱）。先 --dry-run 看请求和费用估算，建议先只生成一段看效果
python3 tools/petgen/generate.py "$C" --dry-run
python3 tools/petgen/generate.py "$C" --only 待机呼吸
python3 tools/petgen/generate.py "$C"

# 3. 抠绿、统一大小、编码，打印质量报告
tools/petgen/.venv/bin/python tools/petgen/process.py "$C"

# 4. 运行桌宠
cd src && ASTRA_PET_VIDEOS="$PWD/../$C/videos" dotnet run
```

不想调图片接口时，也可以直接从设定图里抠：

```bash
swiftc -O tools/petgen/extract_figure.swift -o /tmp/extract_figure
/tmp/extract_figure "$C/reference/sheet.png" 0 0 580 1320 "$C/reference/figure_extracted.png" 290 420
```

API key：

- Seedance 先读环境变量 `ARK_API_KEY`，没有就读 `~/.config/astra-pet/ark_api_key`。
- GPT Image（routin.ai）先读 `ROUTIN_API_KEY`，没有就读 `~/.config/astra-pet/routin_api_key`。

这台机器上的 Python 是从 python.org 装的，不带 CA 证书，`/etc/ssl/cert.pem` 里又缺 routin.ai 用的 ISRG Root X2。所以 `tools/petgen/petnet.py` 在证书库为空时从 macOS 钥匙串导出系统根证书，证书校验始终开着。

## 质量报告怎么看

- **首尾 IoU**：第一帧和最后一帧轮廓的重合度。低于 0.9 时，循环或切换片段会看到跳变，这段要重新生成。
- **横向漂移**：人物重心在画面里最多偏离了多少。非走路片段超过 25px，说明模型让人物在画面里走动了。
- **脚底偏移**：最低点离基准线多远。跳跃、被拎起这类动作偏移大是正常的。
- **边缘残留**：画面左右两侧 80px 内的非透明像素占比。偏高说明背景没抠干净（阴影、地面），或者道具出了界。
- **背景雾**：透明度不到 10% 但不为零的像素占比，正常只有抗锯齿边缘，占比很低。超过 5% 说明绿幕颜色和预期差太多，背景残留了一层雾。

## 当前结果（2026-10-20，720p）

7 段全部用 720p 生成并处理完，每段 10 秒。`preview.png` 是全部动作的抽帧预览（`tools/petgen/preview.py` 生成）。

| 片段 | 首尾 IoU | 横向漂移 | 脚底偏移 | 边缘残留 | 背景雾 | 备注 |
|---|---|---|---|---|---|---|
| idle/待机呼吸 | 0.996 | 10.9px | 0 | 0 | 0.9% | 4~6 秒冒出一颗小爱心 |
| turn/东张西望 | 0.995 | 17.2px | 0 | 0 | 0.8% | 8 秒冒出一个「?」气泡 |
| drag/被拎起悬空 | 0.995 | 3.9px | 35px | 0 | 4.4% | 水豚抱住她的脚一起被拎起；悬空时有一圈白色光晕，深色桌面上明显 |
| click/开心挥手 | 0.995 | 4.0px | 0 | 0 | 1.6% | 第二次生成的 |
| click/害羞捂脸 | 0.996 | 2.2px | 0 | 0 | 2.1% | |
| click/抱水豚 | 0.995 | 19.9px | 2px | 0 | 3.8% | 第二次生成的；爱心是模型画的半透明玻璃泡泡，抠出来偏淡黄 |
| move/原地小跑 | 0.995 | 2.3px | 13px | 0 | 0.7% | 2~8 秒侧身朝左跑 |

第一次 720p 生成有两段不合格，已经删除重做：

- **开心挥手：** 提示词里的「像被轻轻戳了一下」被画成了一根真人手指伸进画面，横向漂移 140px。
- **抱水豚：** 3~7 秒镜头推成了特写，人物撑满整个画面。

为此在公共提示词里加了三条规则：画面里只有女孩和水豚、全程全身远景不切特写、特效画成不透明的实心卡通样式。改完这两段重新生成就正常了。不过最后一条模型不一定照做，抱水豚的爱心仍然是半透明的。

用 `src/bin` 里已编译的桌宠加载这个目录，7 段全部通过启动检查，窗口正常透明显示。

费用：

- Seedance 480p 共 8 段，每段 96475 tokens；720p 共 9 段（含重做的 2 段），每段 216900 tokens。合计 2723900 tokens，按约 70 元/百万 tokens 约 191 元。
- GPT Image 共 5 张，约 0.14 美元。

## 清晰度

同一帧缩到身高 270px 后比较拉普拉斯方差，越大越清楚。首尾帧原图约 1690。

| 片段与时刻 | 480p | 720p | 提升 |
|---|---|---|---|
| 待机 0 秒 | 690 | 1187 | +72% |
| 待机 5 秒 | 565 | 974 | +72% |
| 原地小跑 0 秒 | 699 | 1203 | +72% |
| 原地小跑 5 秒（侧身跑动，有运动模糊） | 429 | 520 | +21% |

在屏幕上的效果还取决于桌宠的显示缩放：3440×1440 这种 1 倍屏上，缩放 0.5 时窗口只有 320×180 物理像素，人物 135px 高，素材又被缩小了一半。在控制台的「显示缩放」里调到 100%，素材就按原尺寸显示，人物 270px 高。Retina 屏上 0.5 本来就是原尺寸。

抠像做过的改进，不用重新生成：

- 半透明边缘改用附近实心像素的颜色，不再只压绿色，边缘不发暗。
- 每帧按背景实际的绿色程度归一化，不同片段绿幕深浅不一也能抠干净。
- 附近没有实心像素的半透明特效（淡出的爱心、光晕），按「观测色 = a·前景 + (1-a)·绿幕」反解前景色，不再发灰发暗。

## 待调

- 走路片段 `原地小跑` 的 `stride`（240）是估算值，要在桌面上看脚和移动对不对得上再调。改完重跑 `process.py --only 原地小跑 --force`。
- 水豚常驻在画面里，点到水豚身上也会触发宠物的点击和拖拽。
- 费用按第三方转述的官方示例估算：480p 约 0.67 元/秒，720p 约 1.51 元/秒。以账单为准。
