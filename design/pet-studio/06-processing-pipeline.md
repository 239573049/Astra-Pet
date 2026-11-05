# 06 后处理管线

把视频模型输出的纯色背景 mp4，处理成宠物能直接用的 640×360 VP9 带 alpha webm，并给出质检报告。
算法以 `tools/petgen/process.py` 为准（已在小t 的 7 段素材上验证过），这里讨论搬到哪、怎么搬、补哪些。

## 1. 在哪跑【待定 D10】

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **A. 移植到 C#** | 解码 / 编码交给 ffmpeg 子进程，抠像、缩放、指标在进程内算 | 用户什么都不用装；NativeAOT 发布；能复用 `FramePool`、能单元测试 | 要把 numpy 代码改写一遍（约 250 行 Python → 600 行 C#） |
| B. 随程序带 Python | 嵌入式 Python + numpy + Pillow | 脚本原样能用 | 安装包增加 50 MB 以上，三平台打包和签名都麻烦 |
| C. 只用 ffmpeg 滤镜 | `chromakey` / `despill` / `scale` / `pad` | 实现最快 | 边缘质量明显差（`process.py` 里做的边缘取色、半透明特效反解都没有），人物定位缩放要先跑一遍分析 |

**推荐 A。** Python 脚本保留在 `tools/petgen/` 作为开发者工具和对照基准（§6 的金标准测试）。

## 2. 管线

```
mp4 ──ffmpeg 解码 rgb24──▶ ① 抠像 ──▶ ② 定位缩放 ──▶ RGBA 640×360 ──ffmpeg 编码 VP9 yuva420p──▶ webm
                              │            │
                              └────────────┴──▶ ③ 指标（首尾 IoU、漂移、脚底、边缘、背景雾、腿部运动）
                                                    └──▶ move：步态曲线 → gait
```

两遍读：第一遍读前 5 帧算站姿包围盒（决定缩放和平移），第二遍全量处理。一段 10s 24fps 720p 是 240 帧。

### 2.1 抠像（`key`）

照搬 `process.py`，参数化抠像色：

1. 设抠像色为 K（绿 / 蓝 / 品红，04 §3 ③ 自动选的）。把像素变换到「K 通道超出量」：
   绿幕时 `excess = clamp(G - max(R, B))`；蓝幕时 `clamp(B - max(R, G))`；品红时 `clamp(min(R, B) - G)`。
2. 按这一帧背景的实际超出量中位数归一化（Seedance 的绿幕不一定是纯 #00FF00）。
3. `alpha = clamp(((1 - excess) - 0.12) / (0.85 - 0.12))`。
4. 去溢色：K 通道压到不超过其余通道的较大值（品红则相反方向）。
5. 半透明边缘：实心区颜色高斯扩散（半径 2px）后回填，边缘不发暗。
6. 附近没有实心像素的半透明特效：按 `观测 = a·前景 + (1-a)·背景色` 反解。
7. 全透明处颜色清零（防止 4:2:0 色度采样渗色）。

实现要点：

- 实现时没有做 SIMD：单精度标量运算和 numpy 的结果逐字节对得上（个别像素差 1），帧之间互不相关，攒一小批（核数 - 2，最多 6）`Parallel.For`
  并行处理再按顺序编码就够快了——10 秒 720p 的片段 Release 下约 5～10 秒，比 `process.py` 快。
- 高斯模糊按 Pillow 的三遍扩展盒式滤波（见 §2.2）。
- 中位数直接排序（只在 1/16 的采样点上算）。
- 超过 1080p 的源视频让 ffmpeg 先缩到 1080 高；最多处理开头 30 秒。

### 2.2 定位缩放（`standing_box` + `place`）

- 前 5 帧 alpha > 0.5 的包围盒，取头顶、脚底、水平中心的中位数。
- 缩放到身高 270、脚底 y = 330、水平中心 x = 320（和 `PetClip` 的 640×360 以及原版素材的 body_box 对齐）。这三个数提成常量放进 Core，和 `PetClip.Width/Height` 放一起。
- 预乘 alpha 下做 Lanczos 缩放，边缘不发灰。~~用 SkiaSharp~~ **实现时改为纯 C#**：按 Pillow 的 `Resample.c` 逐字节复刻（22 位定点、两遍、中间截 8 位），
  边缘取色的高斯模糊也按 Pillow 的 `BoxBlur.c` 复刻。这样和 `process.py` 能做逐字节对照（金标准测试里直接比），Infrastructure 也不用引用 SkiaSharp。
- 包围盒的宽度超过画布（比如张开双臂、有伙伴）时，身高 270 可能装不下宽度——按宽度再限一次，并在报告里提示「人物太宽，已缩小」。

### 2.3 编码

```
ffmpeg -f rawvideo -pix_fmt rgba -s 640x360 -r {fps} -i - \
       -c:v libvpx-vp9 -pix_fmt yuva420p -crf 32 -b:v 0 -row-mt 1 -an out.webm
```

和 `process.py` 一致。编码写到 `.part`，成功后改名。

### 2.4 步态（move 角色）

照搬 `gait_curve`：在 `walk_window`（动作提示词里侧身跑的时间段，模板里自带）内，按腿部区域（脚底往上 35% 身高）的 alpha 帧间变化累加，
小于中位数 20% 的帧算「脚没动」不推进；窗口外曲线走平。`stride` 先用模板默认值（240），在工作台 ⑦ 里用滑块现场调。

## 3. 补充的能力

### 3.1 抠像色可选
见 §2.1。首尾帧、视频提示词、抠像参数三处共用工程里的同一个抠像色。

### 3.2 导入任意视频（01 §4.3）
用户导入的视频不一定是纯色背景，也不一定是 16:9：

- 已经带 alpha 的 webm / mov（ProRes 4444）：跳过抠像，只做 ②③ 和编码。
- 纯色背景：自动采样四角判断背景色，作为抠像色。
- 其他背景（实拍、复杂背景）：不支持，提示用户。抠人像分割模型不在本期范围。

### 3.3 预览图
每个动作取中间帧，拼成网格（`preview.py` 的做法），用于导出的 `preview.png` 和控制台缩略图。

### 3.4 首尾修复（可选）
首尾 IoU < 0.9 时，提供「自动修复」：把最后 N 帧（默认 6 帧）和第一帧做 alpha 交叉淡化。免费、立刻见效，但淡化期间会有轻微「重影」，
所以默认不开，由用户在 ⑥ 里手动选择；修复后的质检报告里注明。

## 4. 质检指标

沿用 `characters/小t/README.md`「质量报告怎么看」：

| 指标 | 算法 | 阈值 | 超了说明什么 | 给用户的建议 |
|---|---|---|---|---|
| 首尾 IoU | 首帧和末帧 alpha>0.5 轮廓的交并比 | < 0.9 | 循环或切换片段时会跳 | 重新生成，或用首尾修复 |
| 横向漂移 | 重心 x 偏离首帧的最大值 | 非 move > 25px | 模型让人物在画面里走动了 | 重新生成；提示词强调「原地」 |
| 脚底偏移 | 最低点离 y=330 的最大距离 | 仅提示 | 跳跃、被拎起是正常的 | — |
| 边缘残留 | 左右各 80px 内 alpha>0.1 的比例 | > 1% | 背景没抠干净或道具出界 | 调抠像参数；重新生成 |
| 背景雾 | 0 < alpha < 0.1 的像素比例 | > 5% | 背景色和预期差太多 | 调抠像参数 |
| 主体缺失 | 任意帧 alpha>0.5 像素过少 | 有 | 人物出画或被抠掉 | 重新生成；换抠像色 |
| 镜头变化（新增） | 包围盒高度相对首帧的最大变化 | > 25%（drag 除外） | 模型推了特写 | 重新生成 |

报告存成 `qa/<动作>/<take>.json`，工作台 ⑥ 和控制台都读它。（第三期的导入只在对话里显示报告和中间一帧，不落盘；工作台再做。）

主体缺失的判定：实心像素少于 min(200, 首帧的 10%)。人物太宽时（站姿宽度缩放后超过 640 − 2×80）按宽度再缩，报告里提示。

## 5. ffmpeg【已定 D11：扩展现有构建】

现状：`tools/ffmpeg/build.sh` 构建的 ffmpeg 只开了 libvpx 的 VP9 **解码**、matroska 解封装、rawvideo 输出，**不能**：

- 解码 Seedance 输出的 mp4（需要 `mov` 解封装 + `h264` 解码器；如果服务端改用 H.265，还要 `hevc`）；
- 编码 VP9（需要 libvpx 打开 `--enable-vp9-encoder`，ffmpeg 打开 `libvpx_vp9` 编码器 + `webm` 封装器）；
- 读 PNG（首尾帧合成用 SkiaSharp 做，不需要 ffmpeg）。

| 方案 | 做法 | 取舍 |
|---|---|---|
| **A. 扩展现有构建** | 一个 ffmpeg 同时满足播放和工作台 | 最简单；体积会增加（libvpx 编码器是大头，实际增加多少要构建出来量）；所有用户都带 |
| B. 工作台组件按需下载 | 播放用的 ffmpeg 不变；第一次打开工作台时下载「工作台版 ffmpeg」（由 release 工作流构建并签名，校验 SHA-256） | 普通用户安装包不变大；多一套构建、下载和校验逻辑；离线用户用不了工作台 |
| C. 用系统 ffmpeg | 让用户自己装 | 不可控，排障成本高。只作为开发者的环境变量覆盖 |

**决定：A，扩展现有构建。** 所有用户都带同一个 ffmpeg，工作台开箱即用、离线可用。要改的地方：

- `tools/ffmpeg/build.sh`：
  - libvpx：去掉 `--disable-vp9-encoder`（保留 `--disable-vp8`）；
  - ffmpeg：增加 `--enable-encoder=libvpx_vp9`、`--enable-decoder=h264`（服务端改用 H.265 时再加 `hevc`）、
    `--enable-demuxer=mov`、`--enable-muxer=webm`、`--enable-parser=h264`；滤镜沿用现有的 `scale,format`。
  - 末尾的 `-version` 检查保留（确认没有 gpl / nonfree / version3）。
- `tools/ffmpeg/selftest.sh`：加两项——解一段 H.264 mp4、编一段带 alpha 的 VP9 webm 再解回来检查 alpha。
- CI / release：三平台各量一次产物体积，写进 CHANGELOG；体积增量记在本节，作为以后是否拆分（方案 B）的依据。
- 程序里 `FfmpegClipDecoder` 不受影响；工作台用同一个可执行文件的不同参数。

**实现记录（第三期）：** 实际打开的组件是 `--enable-decoder=libvpx_vp9,h264,hevc,prores,rawvideo`、`--enable-parser=vp9,h264,hevc`、
`--enable-demuxer=matroska,mov,rawvideo`、`--enable-encoder=rawvideo,wrapped_avframe,libvpx_vp9`、`--enable-muxer=rawvideo,null,webm`、
`--enable-bsf=vp9_superframe`。`hevc` 是不得不加的：FFmpeg 7.1.1 里 h264 解码器引用的 `aom_film_grain` 只随 hevc 解码器编进来，
只开 h264 会链接失败；顺带支持了手机录的 H.265 视频。

体积（macOS arm64，`--enable-small`）：1.58 MB → 3.64 MB（+2.06 MB，libvpx 编码器是大头）。Linux / Windows 等 CI 产物出来后补上。

许可：仍然不开 `--enable-gpl` / `--enable-nonfree`；ffmpeg 原生的 H.264 解码器和 libvpx（BSD）都在 LGPL 的范围内，`THIRD_PARTY_NOTICES.md` 和 `tools/gen_third_party.py` 要同步更新。
H.264 有专利问题的争议，发布前确认分发策略；如果有顾虑，备选是在支持的平台上用系统解码（macOS AVFoundation、Windows Media Foundation）——工作量大，非必要不做。

## 6. 代码结构与测试

```
src/AstraPet.Infrastructure/Studio/Processing/
  ChromaKeyer.cs          §2.1
  FigureLocator.cs        §2.2 站姿包围盒
  FramePlacer.cs          §2.2 站姿包围盒、缩放定位、贴图（FigureLocator、Placement 也在这里）
  Lanczos.cs / BoxBlur.cs 复刻 Pillow 的缩放和高斯模糊
  QaMeter.cs              §4，步态曲线 §2.4 也在这里
  ClipProcessor.cs        串起来：解码 → 处理 → 编码 → 报告；支持取消和进度回调
  PreviewSheet.cs         §3.3（第三期未做，留给工作台 / 导出）
```

测试：

- 单元：合成帧（纯绿背景上画一个已知颜色的圆，边缘按已知 alpha 混合）→ 抠像后 alpha 和颜色误差在阈值内；三种抠像色各一组。
- 金标准：用仓库里 `characters/小t/raw/*.mp4` 跑 C# 管线，和 `process.py` 的结果比较——质检指标差 < 2%，逐帧 alpha 的平均绝对差 < 1/255×3。
  这组测试依赖 ffmpeg 和较大的素材文件，标成 `[Trait("Category", "Slow")]`，CI 里只在 macOS 任务上跑。
- 处理出来的 webm 用 `PetLibrary.Load` 真加载一次，确认帧率、尺寸、alpha 都对。
