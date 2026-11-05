# 动作系统 · Agent 触发 · 宠物工作台

这组文档设计三件相互关联的事：

1. **动作系统**：动作从「分类文件夹里随机抽一段」升级为有 id、有描述、可自定义的一等实体，配一套事件驱动的触发规则。
2. **Agent 触发与系统提示词**：对话智能体（以及可选的外部 Agent）可以让宠物做指定动作；系统提示词按层重写。
3. **宠物工作台（Studio）**：在程序里从「一段描述 / 一张图」做出一只能用的宠物：设定图 → 站姿首尾帧 → 用 Seedance 协议生成动作视频 → 抠像处理 → 试玩 → 导出 `.astrapet`。

文档最初只写设计；决策清单已全部定下（D3、D4、D11、D14 逐项选定，其余按推荐项），第一期（08）开始实现。
正文里保留的 **【待定】** 表格作为方案对比存档，结论以下方决策清单为准。

| 文档 | 内容 |
|---|---|
| [01-action-model.md](01-action-model.md) | 动作、片段、语义角色（role）的模型；用户自定义的层叠与存储；控制台「动作」页 |
| [02-triggers.md](02-triggers.md) | 事件总线、触发规则、优先级与打断、`PetDirector` 改造，代码结构与接口草案 |
| [03-agent-and-prompt.md](03-agent-and-prompt.md) | Agent 怎么触发动作（工具调用 `play_action`、三种协议的映射、外部 Agent 的 HTTP + MCP）；系统提示词的分层与新默认文本 |
| [04-studio-workflow.md](04-studio-workflow.md) | 工作台的步骤、界面、工程目录、动作模板、费用控制 |
| [05-generation-providers.md](05-generation-providers.md) | 图片模型与视频模型的提供商抽象；火山方舟 Seedance 任务协议；其他提供商的接入方式 |
| [06-processing-pipeline.md](06-processing-pipeline.md) | 后处理：抠像、统一尺寸、编码、步态、质检；从 Python 脚本移植到 C#；ffmpeg 构建 |
| [07-package-format.md](07-package-format.md) | `.astrapet` 格式 2：清单 schema、目录、兼容、安全约束 |
| [08-roadmap.md](08-roadmap.md) | 分期计划、验收标准、测试、风险 |

## 现状（v0.1.4）

从代码里读出来的、和这次设计有关的事实：

- **动作 = 分类文件夹。** `PetLibrary` 固定认 `idle / click / drag / move / turn / sing` 六个分类，前五个必需；
  同一分类里的片段完全等价、随机抽一段。片段没有名字以外的任何元数据（不知道是「挥手」还是「害羞」）。
- **随机链写死在设置里。** `PetSettings.Chain` 是 `待机 30 / 转向 10 / 动作 40 / 移动 20`，用户不能按动作调。
- **触发散落在各处，全部硬编码。** `PetWindow` 单击 → `click`、拖拽 → `drag`；`InteractionBehavior` 摸头 → `click`；
  `PomodoroBehavior` 到点 → `click`；`PresenceBehavior` 听歌 → `sing`；`PetCompanion.ChatAsync` 回复后 → `click`。
- **Agent 只会说话。** `PetAgent.AskAsync` 返回纯文本，宠物不管说什么都播一段随机 `click`；
  `ILlmClient` 只有非流式文本补全，不支持工具调用。
- **系统提示词和角色无关。** 默认是一句「你是 Astra，一只住在用户桌面上的小宠物……」，换成小t 或大肥鱼也还是 Astra；
  不知道自己有哪些动作，没有情境（番茄钟、听歌、久坐），也没有说话边界。
- **生成流水线是开发者脚本。** `tools/petgen/` 下的 Python：`make_figure.py`（GPT Image）→ `make_first_frame.py`
  → `generate.py`（Seedance 首尾帧）→ `process.py`（numpy 抠绿、缩放、VP9 编码、步态曲线、质检）→ `pack_pet.py`。
  依赖 numpy / Pillow、硬编码 `/opt/homebrew/bin/ffmpeg`、密钥放环境变量，普通用户用不了。
- **随程序分发的 ffmpeg 只能解码。** `tools/ffmpeg/build.sh` 关掉了 VP9 编码器，也没有 H.264 解码和 mp4 解封装。
  工作台要把 Seedance 的 mp4 转成带 alpha 的 webm，这两样都需要（见 06）。
- **角色包格式 1** 只有 `videos/<分类>/*.webm` 加两个 json；`PetPack.IsAllowed` 要求分类名全是小写字母。

## 决策清单

| # | 要决定的事 | 选项 | 结论 | 详见 |
|---|---|---|---|---|
| D1 | 动作怎么组织 | A. 保留分类文件夹，外加一个 actions.json 补元数据<br>B. 动作是一等实体（id + 片段 + 描述 + 语义角色），分类降级为「角色」标签 | ✅ **B** | 01 §1 |
| D2 | 用户的自定义存在哪 | A. 覆盖层：包只读，用户改动和新增动作存在数据目录，按包 id 关联<br>B. 先「复制为可编辑角色」再改<br>C. A 为主，工作台里提供「以此为基础新建」 | ✅ **C** | 01 §4 |
| D3 | 内置 Agent 怎么触发动作 | A. 回复开头的内联标签 `<act>wave</act>`<br>B. 工具调用（function calling）<br>C. A 为默认，模型支持时用 B | ✅ **已定：B 工具调用**（不支持工具的模型退化为随机 click） | 03 §1 |
| D4 | 是否允许外部 Agent（Claude Code、Codex 等）控制宠物 | A. 不做<br>B. 本地 HTTP 接口<br>C. 本地 HTTP + MCP 服务 | ✅ **已定：C，第二期，默认关闭** | 03 §2 |
| D5 | 系统提示词结构 | A. 维持单段、整段可编辑<br>B. 分层：人设可编辑，平台准则 / 动作协议 / 记忆 / 情境由程序拼 | ✅ **B** | 03 §3 |
| D6 | 人设从哪来 | A. 只在设置里<br>B. 角色包自带人设，用户可以覆盖 | ✅ **B** | 03 §3.3 |
| D7 | Seedance 用哪种生成模式 | A. 首尾帧同一张图（现行方案，天然可循环）<br>B. 参考图模式（喂设定图）<br>C. 首尾帧用不同的图（做「躺下 / 起身」这种状态切换） | ✅ **A 为主；C 用于分段动作（含分段走路，见下方改进 1）** | 04 §5、05 §2 |
| D8 | 视频提供商 | A. 只实现火山方舟任务协议，地址 / 模型 / 密钥可配（BytePlus、兼容网关都能接）<br>B. 每家一个适配器 | ✅ **A，接口按 B 预留** | 05 §2 |
| D9 | 图片提供商协议 | OpenAI Images（generations / edits）、火山 Seedream、Gemini | ✅ **OpenAI Images + Seedream** | 05 §3 |
| D10 | 后处理在哪跑 | A. 移植到 C#，进程内跑<br>B. 随程序带 Python 运行时<br>C. 只用 ffmpeg 自带的 chromakey 滤镜 | ✅ **A** | 06 §1 |
| D11 | ffmpeg 怎么带 | A. 扩展现有构建（加 VP9 编码、H.264 解码），所有用户都带<br>B. 工作台第一次打开时按需下载一个「工作台组件」<br>C. 用用户系统里的 ffmpeg | ✅ **已定：A** | 06 §5 |
| D12 | 工作台的形态 | A. 程序内的独立窗口<br>B. 单独的程序<br>C. 网页 | ✅ **A** | 04 §1 |
| D13 | 导出格式 | A. 沿用 `.astrapet`，升到格式 2；可选附带「源文件」（设定图、站姿、首尾帧、提示词）<br>B. 新扩展名 | ✅ **A** | 07 |
| D14 | 必需动作集 | A. 维持五类必需<br>B. 放宽为 idle + drag + 至少一个 click 类动作；move、turn 缺了自动降级 | ✅ **已定：A 维持五类必需** | 01 §2 |
| D15 | API 密钥存哪 | A. 和现在的 LLM 密钥一样存在设置文件<br>B. 加 `ISecretStore`，存系统钥匙串 / 凭据管理器 | ✅ **A 保持一致；B 作为独立改进统一处理所有密钥** | 05 §4 |

## 讨论后追加的改进（已采纳）

| # | 改进 | 内容 | 落在 |
|---|---|---|---|
| 1 | 分段走路 | `move` 可以是 `phased` 动作：起步（站姿 → 侧身）/ 行走循环 / 收步（侧身 → 站姿）。侧身关键帧在 ③ 里和站姿一起生成，走路段不再需要在一段视频里「原地转身再跑」，步态更稳。老包的单段 move 照旧 | 01 §3.3、04 §3 ③、第五期提前到第四期可选 |
| 2 | 更高的渲染分辨率 | `pet.json` 加 `render: { width, height }`，默认 640×360；工作台默认导出 960×540，高分屏缩放时不糊。`PetClip.Width/Height` 从常量改成按包读取 | 07、06 §2.2 |
| 3 | 点击分区 | `pet.clicked` 带 `zone`（`head` / `body` / `feet`，按站姿包围盒的比例划分），规则可以用 `when.event: { zone: "head" }` 区分摸头和戳肚子 | 02 §2 |
| 4 | 回放提供商 | 工作台的图片 / 视频提供商有一个「回放」实现：从本地目录读预先录好的结果，不发请求、不花钱。用于测试、演示和 CI | 05 §2.4 |

## 设计原则（沿用 design/README.md，并补充）

- Core 不认识平台和 Avalonia；新加的动作模型、触发引擎、工作台的工程模型和步骤状态机都在 Core，能单元测试。
- **现有行为不回退。** 第一期上线后，不改任何配置的用户看到的行为要和现在一样（同样的随机链权重、同样的点击 / 拖拽反应）。
- **每一次付费调用都要用户确认并记账。** 提交前显示估算，任务 id 先落盘再查询，断线重连不重复提交（沿用 `generate.py` 的做法）。
- **角色包是数据，不是代码。** 包里只能有声明式的配置，不能带脚本；包里的人设文本视为不可信输入。
