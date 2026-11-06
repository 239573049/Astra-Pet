# 外部智能体联动：识别 · 一键绑定 · Hook 驱动宠物

让宠物跟用户正在用的编程智能体（Claude Code、Codex、DeepSeek Harness……）联动：智能体开始干活时宠物「思考」，
干完一件耗时的事时庆祝并冒一句，等用户确认权限时招手提醒，出错时沮丧。

做法：控制台新增「智能体」页，**自动识别**本机装了哪些智能体，用户点「绑定互动」后，程序往该智能体的 hook 配置里
**追加一条自己的 hook**；智能体运行时触发 hook → 调用 `AstraPet hook …` 转发命令 → 本地 HTTP 接口 → 宠物反应。

决策清单全部按推荐项定下。P1（Claude Code）和 P2（Codex）已实现（2026-11-06），和设计的出入见 §10；DeepSeek Harness 待查。

## 1. 现状（main，v0.2.3，2026-11-06）

和这次设计有关、**已经有的**东西：

- **本地 HTTP 接口**（pet-studio 03 §2.1、§2.4）：`LocalHttpServer` 只听 `127.0.0.1:47615`，`ExternalApi` 负责路由、Bearer 令牌、
  每秒 5 个请求的限流、拒绝带 `Origin` 的请求。默认关闭，在「模型与记忆 → 外部控制」里打开。
- **约定事件 + 内置规则**（`DefaultTriggers.cs`）：`external.started` → 循环 `think` 直到 done / failed；
  `external.done` → `celebrate`；`external.failed` → `sad`。用户能在「动作 → 绑定触发」里改。
- **`AstraPet mcp` 子命令**：不起界面，每次从设置文件读端口和令牌，转发到正在运行的宠物。hook 转发命令照这个模式做。
- **智能体配置目录的定位**：`Import/CodexSource`（`CODEX_HOME`，默认 `~/.codex`）、`Import/DshSource`（`DSH_HOME`，默认 `~/.dsh`），可以复用。
- **说话排队**：`IPetVoice.SayLater`，用户在聊天或上一句没收起时先攒着（最多 3 句，10 分钟过期）。

**缺的**：

- 用户想联动，得自己去 hooks 配置里手写 `curl` 并把令牌贴进去（03 §2.2 只提了一句「hooks 也可以直接 curl」）。
- 宠物端没有「会话」的概念：两个 Claude Code 会话同时跑，一个发了 `external.done`，另一个的思考也被停掉。
- Claude Code 的 `Stop` 每一轮对话都会触发，直接映射到 `external.done` 会每轮都庆祝，太吵。
- 没有「智能体在等你确认」这种事件，而这恰恰是用户最需要被提醒的时刻。

### 1.1 各智能体的 hook 机制（2026-11-06 在本机核实）

| | Claude Code | Codex（codex-cli 0.166.0） | DeepSeek Harness（dsh） |
|---|---|---|---|
| 配置目录 | `CLAUDE_CONFIG_DIR`，默认 `~/.claude` | `CODEX_HOME`，默认 `~/.codex` | `DSH_HOME`，默认 `~/.dsh` |
| hook 配置文件 | `settings.json` 的 `hooks` 字段（用户级） | `hooks.json`（独立文件） | **待查**：本机未装 `dsh`，`~/.dsh/profiles` 下没有 hook 相关配置 |
| 格式 | `{"hooks": {"<事件>": [{"matcher"?, "hooks": [{"type":"command","command","timeout"}]}]}}` | **与 Claude Code 相同** | 待查 |
| 输入 | stdin 一段 JSON（`hook_event_name`、`session_id`、`cwd`……） | stdin JSON，字段待逐个核实 | 待查 |
| 我们关心的事件 | `UserPromptSubmit`、`Stop`、`StopFailure`、`PermissionRequest`、`Notification`、`SessionEnd` | `UserPromptSubmit`、`Stop`、`PermissionRequest`、`Interrupt` | 待查 |
| 特殊之处 | 已打开的会话不会重新读 hooks，要新开会话才生效 | **新 hook 要用户信任**：`config.toml` 里 `[hooks.state."<文件>:<事件>:<i>:<j>"]` 存 `enabled` 和 `trusted_hash`。这个我们**不能代写**，要让用户在 Codex 里确认 | 待查 |

另外两点：

- Codex 还有一个 `notify = [...]` 配置（一轮结束时调用），但**只有一个位置**，用户机器上常被其他工具占用（本机就被 Computer Use 占了），所以不用它，只用 `hooks.json`。
- 用户的 hooks 里常有别的工具的条目（本机 Claude Code 和 Codex 都装了 Orca 的 hook）。**只追加、只删除自己那一条，绝不改动别人的。**

## 2. 决策清单

| # | 要决定的事 | 选项 | 推荐 | 结论 | 详见 |
|---|---|---|---|---|---|
| H1 | 入口放哪 | A. 侧栏新页「智能体」（「陪伴」组）<br>B. 放进「模型与记忆 → 外部控制」 | **A**：绑定是独立功能，外部控制页只放底层接口设置 | ✅ A | §6 |
| H2 | hook 里写什么命令 | A. 转发子命令 `"<程序路径>" hook <agent> <event>`<br>B. 内联 `curl`，令牌写进配置<br>C. 释放一个 shell / ps1 脚本 | **A**：令牌不落到别人的配置里，重新生成后不用改；Windows 没有引号转义问题；不依赖 curl | ✅ A | §3 |
| H3 | 会话聚合放哪 | A. 宠物端新接口 `/v1/agent-hooks` + Core 里的会话追踪，聚合后再发 `external.*` 事件<br>B. 转发命令直接发 `/v1/events` | **A**：只有宠物端知道同时有几个会话、这一轮跑了多久 | ✅ A（聚合后发 agent.*，见 §10） | §4 |
| H4 | 每轮 `Stop` 都庆祝吗 | A. 每轮都庆祝<br>B. 这一轮超过阈值（默认 30 秒）才庆祝并冒泡，短的只安静收起思考<br>C. 从不庆祝，只冒泡 | **B**，阈值可调 | ✅ B | §4.3 |
| H5 | 写到哪一级配置 | A. 用户级（`~/.claude/settings.json`、`~/.codex/hooks.json`）<br>B. 项目级（`.claude/settings.json`） | **A**：一次绑定对所有项目生效，也不会把我们的条目提交进别人的仓库 | ✅ A | §5 |
| H6 | 冒泡里带项目名吗 | A. 带 `cwd` 的最后一级目录名<br>B. 不带 | **A**，设置里可关 | ✅ A | §4.4 |
| H7 | 首批支持哪些 | A. Claude Code + Codex，dsh 查清格式再接<br>B. 三个一起 | **A** | ✅ A（Codex 一并做了） | §7 |

## 3. 转发命令 `AstraPet hook`

```
"<程序路径>" hook <agent> <event>
```

- `agent`：`claude` / `codex` / `dsh`；`event`：我们自己的事件名（`turn-start`、`turn-end`、`turn-failed`、`waiting`、`interrupted`、`session-end`）。
  **事件名放在命令行参数里，不依赖 stdin 里的 `hook_event_name`**：各家字段名不一定一样，映射在写配置时就确定了。
- stdin：读完（最多 64 KB，读不完直接丢弃），只取 `session_id`、`cwd`；Claude Code 的 `Notification` 额外取 `notification_type`。
  **不取 prompt、工具参数、回复内容。**
- 读设置文件拿端口和令牌（同 `mcp`：只读，不走 `SettingsStore`）。外部接口没开、读不到设置，就直接退出。
- POST `http://127.0.0.1:<端口>/v1/agent-hooks`，**总超时 1 秒**。
- **任何情况都 `exit 0`、stdout 不输出**：宠物没开、端口不通、超时都不能卡住或打断智能体，也不能往智能体的上下文里塞东西
  （Claude Code 会把一部分 hook 的 stdout 喂给模型）。错误只写日志。
- 在 `Program.Main` 里和 `mcp` 一样最先分流，不初始化 Avalonia、不加载素材。NativeAOT 下启动应在几十毫秒内（实现后实测，写进验收）。
- hook 配置里同时写 `"timeout": 5`，作为第二道保险。

### 3.1 事件映射（写进配置时确定）

| 我们的事件 | Claude Code | Codex | 说明 |
|---|---|---|---|
| `turn-start` | `UserPromptSubmit` | `UserPromptSubmit` | 一轮开始 |
| `turn-end` | `Stop` | `Stop` | 一轮正常结束 |
| `turn-failed` | `StopFailure` | —（待查） | 一轮因 API 错误等结束 |
| `waiting` | `PermissionRequest`；`Notification` 且 `notification_type` 为权限类 | `PermissionRequest` | 在等用户确认 |
| `interrupted` | — | `Interrupt` | 用户中断，安静收尾 |
| `session-end` | `SessionEnd` | — | 会话结束，清掉追踪 |

**不订阅** `PreToolUse` / `PostToolUse` / `PostToolUseFailure` / `Subagent*`：频率太高（会撞每秒 5 个的限流，还要为每次工具调用起一个进程），
而且工具失败很常见、多半能自己恢复（测试没过、grep 没搜到），让宠物为这个难过是误报。`SessionStart` 先不订阅（每次开会话都打招呼也吵）。

## 4. 宠物端：接口与会话追踪

### 4.1 新接口 `POST /v1/agent-hooks`

```json
{ "agent": "claude", "event": "turn-end", "session": "<session_id>", "project": "Astra-Pet" }
```

- 鉴权、拒绝浏览器、请求体大小同其他接口。`agent` 白名单，`event` 只能是 §3 的六个，`session` ≤ 128 字符，`project` ≤ 64 字符。
- **单独的限流桶**（每秒 10 个）：用户多开几个会话不该挤掉脚本的 `/v1/say`，反过来也一样。
- 只交给 `AgentSessions`（Core），不直接操作 Director。

### 4.2 `AgentSessions`（Core，可单测，用 `TimeProvider`）

按 `(agent, session)` 记录：状态（工作中 / 等确认）、本轮开始时间、项目名、最后一次事件时间。

| 收到 | 会话状态 | 宠物端发出的事件 |
|---|---|---|
| `turn-start` | → 工作中，记开始时间 | 工作中的会话从 0 变 1 时发 `external.started` |
| `turn-end` | → 空闲 | 本轮 ≥ 阈值：`external.done`；否则：若已无工作中的会话，发 `external.idle`（新，只收起思考） |
| `turn-failed` | → 空闲 | `external.failed` |
| `waiting` | → 等确认 | `external.waiting`（新） |
| `interrupted` | → 空闲 | 同 `turn-end` 的短轮次处理，不庆祝 |
| `session-end` | 删除 | 若已无工作中的会话，发 `external.idle` |
| 30 分钟没有任何事件 | 删除（智能体被强杀、hook 丢了） | 同上 |

事件带 `data`：`agent`（显示名）、`project`、`seconds`、`active`（还剩几个在工作），用户写触发规则时能用。

### 4.3 默认规则的调整

- `external-started` 的 `Until` 加上 `external.idle`。
- 新增 `external-waiting` → `role:greet`（招手），优先级 `agent`。
- 两个会话同时跑时：一个庆祝完，另一个还在工作，应该回到思考循环。**实现时先确认** Director 被同优先级动作打断后，带 `Until` 的循环是否会恢复；
  不会的话，由 `AgentSessions` 在 done / waiting 之后、仍有工作中的会话时补发一次 `external.started`。
- 新事件 `external.idle`、`external.waiting` 加进 `PetEvents` 的事件列表，让「绑定触发」里能选。

### 4.4 冒泡文案

走 `SayLater`（不打断用户聊天）：

- done：「Claude Code 在 Astra-Pet 里忙完了（3 分 12 秒）」
- failed：「Codex 那边出错了，去看看？」
- waiting：「Claude Code 在等你确认」（这条最有用：**默认同时朗读**，如果用户开了语音）
- 多个会话时文案带上项目名区分；H6 关掉时只说智能体名。
- 以后可以交给内置 Agent 用人设语气改写，第一期用固定模板。

## 5. 识别与绑定

### 5.1 `IAgentIntegration`（Infrastructure，每个智能体一个实现）

```csharp
public interface IAgentIntegration
{
    string Id { get; }            // claude / codex / dsh
    string Name { get; }          // Claude Code
    AgentDetection Detect();      // 没装 / 装了（配置目录、命令路径）
    BindState State(string exePath);  // 未绑定 / 已绑定 / 已绑定但路径过期
    void Bind(string exePath);
    void Unbind();
    string? AfterBindHint { get; }    // Codex：「下次打开 Codex 时请确认信任这个 hook」
}
```

- **识别**：配置目录存在，或命令（`claude` / `codex` / `dsh`）在 `PATH` 里，任一满足就算装了。只看存不存在，不运行它们。
- **认出自己的条目**：`type` 为 `command`，且 `command` 里有 `hook <agent> ` 并以我们的程序名结尾的路径开头。不加额外字段，免得对方校验 schema 时报错。
- **绑定**：
  1. 读文件（不存在就从 `{}` 开始），解析失败**不写**，界面提示「配置文件格式有误，请先修好」。
  2. 先删掉我们旧的条目（覆盖路径过期的情况），再按 §3.1 每个事件追加一组 `{"hooks":[{"type":"command","command":…,"timeout":5}]}`，追加在已有条目的后面。
  3. 第一次绑定时备份原文件为 `<文件>.astrapet.bak`（已存在就不覆盖，保留最早的原样）。
  4. 写临时文件再原子替换；写之前再读一次，内容和第 1 步不一样就从头重来（对方正好在写自己的设置），最多重试 3 次。
  5. 外部接口没开就顺手打开（生成令牌）。
- **解绑**：只删我们的条目；某个事件下删空了就把这个事件的键也删掉；`hooks` 删空了就删掉 `hooks`。
- **路径过期**：每次启动对已绑定的智能体检查一次，程序路径变了（更新、移动、Windows 换了安装目录）就静默改写，记日志。
- JSON 用 `JsonNode` 读写，保留键的顺序；缩进统一为 2 空格（和两家默认一致），不保证保留原有格式里的空行。

### 5.2 Codex 的信任

我们写入 `hooks.json` 后，Codex 要用户确认才会执行（`config.toml` 的 `hooks.state` 里有 `trusted_hash`）。

- **不代写 `trusted_hash`**：这是 Codex 的安全机制，替用户点「信任」不合适，而且哈希算法随版本变化。
- 绑定后卡片显示「等待在 Codex 里确认」，直到收到第一个来自 Codex 的事件才变成「已连接」。
- **待核实**：Codex 启动时具体怎么提示信任新 hook；没有提示的话，文案要告诉用户去哪确认。

### 5.3 卸载

- Windows 安装器卸载时运行 `AstraPet hook --unbind-all`。
- macOS 没有卸载流程：控制台「智能体」页底部放「全部解除绑定」，README 里写清楚。
- 兜底：程序已经删了、hook 还在时，命令找不到会让智能体显示一条 hook 错误（不阻塞）。**待核实**两家对此的具体表现，太吵的话命令改为经 shell 包一层 `|| true`（Windows 上要另外处理）。

## 6. 控制台「智能体」页

放在「陪伴」组，`Ai` 后面。页面说明：「让 Claude Code、Codex 这些编程智能体干活时，宠物也跟着反应。」

每个智能体一张卡片：

```
[图标] Claude Code                                  [ 绑定互动 ○ ]
       已检测到 · ~/.claude/settings.json
       已连接 · 最近一次：2 分钟前（Astra-Pet，完成）
```

- 没检测到：灰色卡片，「没有找到 Claude Code」，开关禁用，附官网链接。
- 状态：未绑定 / 已绑定，等待第一个事件 / 已连接 / 等待在 Codex 里确认 / 配置文件有误。
- 下面一组公共设置：庆祝阈值（秒）、冒泡里显示项目名（H6）、等确认时朗读。
- 底部：「全部解除绑定」、「测试一下」（假装收到一组 start → 5 秒 → end，不经过智能体）。
- 「模型与记忆 → 外部控制」保留不动，加一行「想让编程智能体联动？去『智能体』页一键绑定」。

## 7. 分期

1. **P1**：`AstraPet hook` 转发命令、`/v1/agent-hooks`、`AgentSessions`、默认规则调整；Claude Code 绑定 / 解绑；「智能体」页。
2. **P2**：Codex（大部分代码和 Claude Code 共用，主要是不同的文件和信任提示）。
3. **P3**：查清 DeepSeek Harness 的 hook 机制后接入；如果它不支持命令型 hook，再评估 MCP（已有）或别的办法。
4. 以后：内置 Agent 用人设语气改写冒泡；更多智能体（Cursor、Gemini CLI……按同一接口加）。

## 8. 测试

- `AgentSessions`：单会话长短轮次、两个会话交错、waiting、超时清理、`interrupted`。用假的 `TimeProvider`。
- `/v1/agent-hooks`：鉴权、白名单、长度限制、单独的限流桶。沿用 `ExternalApiTests` 的写法。
- 绑定 / 解绑：用临时目录模拟配置文件，覆盖——文件不存在、空对象、已有别人的 hook（解绑后原样保留）、已有我们旧路径的条目、JSON 损坏（不写）、重复绑定（不重复追加）、解绑后清掉空键。
- 转发命令：宠物没开 / 端口不通 / 超时时 1 秒内 `exit 0` 且 stdout 为空。
- 手工验收：三个平台各用真的 Claude Code 跑一轮长任务、一轮短任务、一次权限确认；Codex 走一遍信任流程。

## 9. 不做的事

- 不读、不转发 prompt、工具参数、回复内容和对话记录。
- 不代替用户信任 Codex 的 hook。
- 不改智能体的其他设置（MCP、权限、模型）；MCP 仍由用户在「外部控制」里自己加。
- 不往项目级配置里写。

## 10. 实现时的调整（2026-11-06）

- **事件改用 `agent.*` 命名空间**，不复用 `external.*`：`agent.working`（第一个会话开始）、`agent.idle`（最后一个停下）、
  `agent.done`（一轮够久地完成）、`agent.failed`、`agent.waiting`。`external.started/done/failed` 留给脚本，语义不变，老脚本不受影响。
  默认规则：working → 循环 `think` 直到 `agent.idle`；done → `celebrate`；failed → `sad`；waiting → `greet`。都加进了「绑定触发」的事件列表。
- **多会话**：Director 本来就是「插播完回到循环」，所以庆祝、招手打断思考后会自己回到思考；只有最后一个会话停下才发 `agent.idle` 收起循环
  （长轮次先发 idle 再发 done，庆祝不会被循环盖掉）。§4.3 担心的「补发 started」不需要。
- **等确认**：等确认的会话不算「工作中」，只剩它时先收起思考再招手。确认之后到 `Stop` 之间没有事件，思考不会恢复；这一轮的时长仍从 turn-start 算。
- **Claude Code 不订阅 `Notification`**：`PermissionRequest` 已经覆盖权限确认，两个都订会重复提醒。
- **代码位置**：`Core/External/AgentSessions.cs`（聚合、冒泡文案）、`ExternalApi` 的 `/v1/agent-hooks`（`IExternalPet.Agents` 为空时 404）、
  `Infrastructure/External/AgentHooks.cs`（`HookJsonAgent` 读写配置、`AgentIntegrations`、`AgentHookCommand` 转发命令）、
  `App/ControlPanel/AgentsViewModel.cs` 与控制台「智能体」页。设置：`AgentCelebrateSeconds`（默认 30）、`AgentShowProject`、`AgentSpeakWaiting`。
- **配置文件**：解析时容忍注释和尾逗号，但写回时注释会丢（两家的文件本来就是严格 JSON）；缩进统一 2 空格。
- **还没做**：Windows 安装器卸载时调用 `AstraPet hook --unbind-all`（命令已经有了）；Windows 上 hook 命令经哪个 shell 执行、引号是否可用要实测；
  Codex 的信任提示流程、stdin 字段要用真的 Codex 走一遍；转发命令的启动耗时要在 NativeAOT 发布版上实测。
