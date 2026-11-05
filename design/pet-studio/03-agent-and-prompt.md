# 03 Agent 触发动作与系统提示词

「Agent 触发」分两种：

- **内置对话 Agent**（`PetAgent`）：用户和宠物聊天时，模型自己决定做什么动作。§1
- **外部 Agent**（Claude Code、Codex、脚本、自动化工具）：通过本地接口让宠物做动作、说话。§2

## 1. 内置 Agent 怎么触发动作【已定 D3：工具调用】

下面先保留三种方案的对比（§1.0），决定采用方案 B：工具调用。具体设计见 §1.1～§1.6。

### 1.0 方案对比（留档）

#### 方案 A：回复开头的内联标签（未采用）

模型在回复最前面写一个标签：

```
<act>wave</act>你来啦！今天想聊点什么？
```

程序解析：

```csharp
// Core/Ai/ReplyParser.cs
public sealed record ParsedReply(string Text, string? ActionId, string? Emotion);

public static ParsedReply Parse(string raw, ActionSet actions)
{
    // 1. 找所有 <act>…</act>（容忍大小写、全角尖括号〈〉、前后空格、写在结尾的情况），只取第一个
    // 2. 从显示文本里删掉所有标签；顺带删掉模型偶尔照抄的 [动作:xxx]、（挥手）这类舞台说明的变体
    // 3. 标签内容依次按：动作 id → 动作 name → 角色名 / 情绪名（happy、开心）→ 无效 匹配
    // 4. 无效时 ActionId 为 null，触发引擎按默认规则播 role:click
}
```

- 任何模型都能用，包括本地小模型；不改 `ILlmClient`，三种协议都不用动。
- 以后做流式输出时也友好：标签在最前面，读到 `</act>` 就能提前开始播动作，文字边到边显示。
- 风险是模型不守格式（漏写、写错 id、写在中间）。解析器宽容处理，最坏情况退化成现在的「随机 click」，不会更差。
- Token 开销：动作清单每次随系统提示词发送，30 个动作约 600 token。动作清单放在系统提示词的稳定部分，支持提示词缓存的服务会命中缓存。

#### 方案 B：工具调用（采用）

给模型一个工具：

```json
{
  "name": "play_action",
  "description": "让宠物在桌面上做一个动作。和你说的话、心情配合使用，每次回复最多调用一次。",
  "input_schema": {
    "type": "object",
    "properties": { "id": { "type": "string", "enum": ["wave", "shy", "hug-capy", "..."] } },
    "required": ["id"]
  }
}
```

- 结构化，`enum` 直接约束 id，不会写错。
- 但要改 `ILlmClient`：三种协议（Chat Completions / Responses / Anthropic Messages）的工具格式各不相同，要处理「模型只调工具不说话」
  需要再发一轮、并行工具调用、`tool_choice` 等；很多本地模型和部分国内网关不支持或支持得不好。
- 一次回复变成可能两次往返，延迟变高。

#### 方案 C：混合（未采用）

`LlmProvider` 加一个 `SupportsTools` 能力位（「测试连接」时探测，或用户手动勾选）。支持就用 B，否则用 A。两条路径都要维护。

#### 对比

| | A 内联标签 | B 工具调用 | C 混合 |
|---|---|---|---|
| 兼容的模型 | 全部 | 支持工具调用的 | 全部 |
| 准确性 | 好的模型 >95%，小模型会漏 | 高 | 高 |
| 改动量 | 小：解析器 + 提示词 | 大：三种协议的请求和多轮循环 | 最大 |
| 延迟 | 无额外 | 可能多一轮 | 视情况 |
| 以后接「设提醒」「开番茄钟」这类真能力 | 不适合 | 适合 | 适合 |

**决定：采用 B。** 好处是结构化、id 不会写错，而且以后给宠物加「帮我开个 25 分钟番茄钟」「提醒我 3 点开会」这类真能力时，
直接沿用同一套机制，不用再迁移。代价是 `ILlmClient` 要支持三种协议的工具调用（§1.3），不支持工具的模型做不了动作（§1.5）。

### 1.1 工具定义

第一期只有一个工具 `play_action`。动作清单写在工具的 `description` 里，id 用 `enum` 约束。
工具定义和系统提示词的稳定部分一样，在同一只宠物、同一套动作下不变，能命中提示词缓存。

```jsonc
{
  "name": "play_action",
  "description": "让你自己在用户的桌面上做一个动作，配合你说的话和心情。每次回复最多调用一次，并且照常用文字回复用户；不需要动作时不调用。\n可用动作：\n- wave：举起一只手朝用户挥手，头顶冒出粉色小爱心（开心、打招呼）\n- cover-face：双手捂脸害羞地扭来扭去（害羞）\n- hug-capy：把脚边的水豚抱起来蹭蹭（撒娇、喜欢）\n- dance：原地转圈跳舞（开心）\n也可以填心情 happy、shy、sad、surprised、confused、affection，程序会挑一个合适的动作。",
  "parameters": {
    "type": "object",
    "properties": {
      "id": { "type": "string", "enum": ["wave", "cover-face", "hug-capy", "dance",
                                          "happy", "shy", "sad", "surprised", "confused", "affection"] }
    },
    "required": ["id"],
    "additionalProperties": false
  }
}
```

动作清单的生成规则：只列「启用、有描述或有名字」的动作，最多 30 个；不列 `idle`、`drag`、`move`、`turn`、`think`
（由系统控制，让模型选只会添乱）；只列这只宠物有对应动作（含回退）的心情。动作集变了（切换宠物、在「动作」页改了描述）就重新生成。

为以后扩展工具，抽一个能力接口，`play_action` 是第一个实现：

```csharp
// Core/Ai/AgentTools.cs
public interface IAgentTool
{
    string Name { get; }
    ToolDefinition Definition(AgentToolContext ctx);   // 名字、描述、JSON Schema；返回 null 表示当前不可用（比如没有可选动作）
    bool IsPresentational { get; }                      // 只影响表现、没有副作用：执行后不需要把结果再喂给模型（§1.4）
    Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct);
}
```

以后有副作用的工具（开番茄钟、设提醒）`IsPresentational = false`，并且执行前要过用户确认（见 §3.3 的安全说明）。

### 1.2 数据结构的改动

现在的 `ChatMessage(Role, Content)` 只有纯文本，表达不了工具调用。改成内容块：

```csharp
// Core/Ai/ChatModels.cs
public abstract record ChatPart;
public sealed record TextPart(string Text) : ChatPart;
public sealed record ToolCallPart(string CallId, string Name, string ArgumentsJson) : ChatPart;
public sealed record ToolResultPart(string CallId, string Content, bool IsError = false) : ChatPart;

public sealed record ChatMessage(string Role, IReadOnlyList<ChatPart> Parts)   // user | assistant
{
    public static ChatMessage User(string text) => new("user", [new TextPart(text)]);
    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record ChatRequest(
    string? System, IReadOnlyList<ChatMessage> Messages, double Temperature, int MaxTokens,
    IReadOnlyList<ToolDefinition>? Tools = null);       // null 或空：不带工具，和现在一样

public sealed record ChatResponse(IReadOnlyList<ChatPart> Parts, string? StopReason);

public interface ILlmClient
{
    Task<ChatResponse> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct = default);
}
```

`ToolResultPart` 放在 role 为 user 的消息里（Anthropic 的约定），各协议的序列化器再按自己的格式拆开（§1.3）。
记忆提取、测试连接这些调用不带工具，取 `response.Text` 即可，行为不变。

### 1.3 三种协议的映射

`LlmClient` 里每个协议的 Build / Parse 各加一段。差异集中在这张表：

| | Chat Completions | Responses | Anthropic Messages |
|---|---|---|---|
| 请求里的工具 | `tools: [{ "type": "function", "function": { name, description, parameters } }]` | `tools: [{ "type": "function", name, description, parameters }]` | `tools: [{ name, description, input_schema }]` |
| 选择策略 | `tool_choice: "auto"`（不发 `parallel_tool_calls`：不少网关不认这个字段，多出来的调用由循环忽略） | `tool_choice: "auto"`，`parallel_tool_calls: false` | `tool_choice: { "type": "auto", "disable_parallel_tool_use": true }` |
| 响应里的调用 | `choices[0].message.tool_calls[] { id, function: { name, arguments(字符串) } }`，`content` 可能为 null | `output[]` 里 `type: "function_call"` 的项 `{ call_id, name, arguments(字符串) }` | `content[]` 里 `type: "tool_use"` 的块 `{ id, name, input(对象) }` |
| 历史里的调用 | assistant 消息带 `tool_calls` | 输入项 `{ type: "function_call", call_id, name, arguments }` | assistant 内容块 `tool_use` |
| 历史里的结果 | 单独一条 `{ role: "tool", tool_call_id, content }` | 输入项 `{ type: "function_call_output", call_id, output }` | user 内容块 `{ type: "tool_result", tool_use_id, content }` |

细节：

- 参数 JSON 解析失败、工具名不认识、id 不在 enum 里：当作这次没调用（记日志），文字照常显示。
- 模型不守 `parallel` 设置一次调了多个 `play_action`：只执行第一个，其余都回一个「已忽略」结果，保证历史合法。
- 一些网关会把 `arguments` 放成对象而不是字符串，两种都接受。

### 1.4 一轮对话的循环

```
AskAsync(text):
  messages = 最近 N 轮历史 + User(text)
  for round in 1..3:                                   // 防止模型无限调工具
      resp = CompleteAsync(system, messages, tools)
      calls = resp.Parts 里的 ToolCallPart
      执行每个 call（play_action → 发 chat.action 事件，见 §1.6），得到 ToolResultPart
      messages += Assistant(resp.Parts) + User(results)
      if resp 有文字 && 所有 call 都是 IsPresentational: break   // 常见情况：一次请求就结束
      if calls 为空: break
      // 只调了工具没说话，或调了有副作用的工具：带着结果再请求一轮，让模型说话
  回复文字 = 所有轮次 TextPart 拼起来；仍为空时用兜底文字（「嗯嗯～」）
```

- **常见路径只有一次请求**：好模型会在同一个回复里既说话又调 `play_action`。这时工具结果（固定为「已执行」）只写进历史，不再请求。
- 只调了工具没说话（有的模型会这样）才多一轮，延迟增加一次往返。
- 历史保存完整的内容块（包括工具调用和结果），下一轮原样发回去，各协议对「调用后必须跟结果」的要求都能满足。
  历史裁剪时按「轮」裁，不能把一对调用和结果拆开。
- 对话窗口和气泡只显示 `TextPart`。

### 1.5 不支持工具的模型

不少本地模型和部分网关不支持工具，或者带上 `tools` 就报 400。`LlmProvider` 加一个设置：

```csharp
public enum ToolSupport { Auto, On, Off }   // 默认 Auto
```

- `Auto`：带工具请求；如果返回 4xx 且错误信息里提到 tool / function（或网关已知的错误码），自动去掉工具重试一次，
  并把这个提供商 + 模型记为不支持（存在设置里，换模型后重新探测），之后不再带工具。
- `On` / `Off`：用户在「智能」页手动指定。
- 「测试连接」顺带测一次带工具的请求，把结果显示出来（「这个模型支持动作 ✓」/「这个模型不能控制动作，回复后宠物会随机做一个反应」）。
- 不带工具时，系统提示词里的【做动作】一段整段省略；回复后按默认规则 `reply` 播 `role:click`——**和现在的行为一样，不会更差**。
- 没有做内联标签的兜底（方案 A 未采用）。如果以后发现常用的本地模型大多不支持工具，再考虑把 A 作为兜底加回来。

### 1.6 回复之后的事件流

```
用户发消息 → Publish(chat.thinking) → 宠物播 think 循环（没有就保持当前）+ 气泡「…」
        → 模型调用 play_action(wave) → Publish(chat.action { action = wave })
             → 规则 agent-action：Request(wave, priority: agent)  —— 不用等文字，收到调用就开始播
        → 回复文字到达 → Publish(chat.replied { acted = true })
             → 气泡显示文字、朗读；结束 think 循环
             → 规则 reply：这一轮没有调用过 play_action 时才播 role:click 兜底
```

`PetCompanion.ChatAsync` 里写死的 `_director.Play(PetLibrary.Click, fromUser: true)` 删掉，改为发事件。
02 §2 的事件表相应加上 `chat.action`，`chat.replied` 的数据改为 `acted`。

## 2. 外部 Agent【已定 D4：HTTP + MCP，第二期，默认关闭】

用途：让编程 Agent 和宠物联动——Claude Code 跑完任务时宠物庆祝、跑测试时宠物「思考」、出错时宠物沮丧；或者用户自己的脚本、自动化工具驱动宠物。

### 2.1 本地 HTTP 接口

- 只监听 `127.0.0.1`，端口默认 `47615`（可改），**默认关闭**，在控制台「智能 → 外部控制」里打开。
- 鉴权：打开时生成随机令牌，存在设置里，请求带 `Authorization: Bearer <token>`；界面上一键复制。
- 限流：每秒最多 5 个请求；`say` 文本长度 ≤ 200。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/status` | 当前宠物、当前动作、是否暂停 / 省电 |
| GET | `/v1/actions` | 动作清单（id、名字、描述、角色） |
| POST | `/v1/actions/play` | `{ "id": "wave" }` 或 `{ "role": "celebrate" }`，可带 `"loopSeconds"`；优先级固定 `agent` |
| POST | `/v1/say` | `{ "text": "测试全部通过！", "speak": false }` |
| POST | `/v1/events` | `{ "type": "external.build-failed" }`，交给触发规则处理，用户可以在规则里决定反应 |

`/v1/events` 是推荐的用法：外部工具只报告「发生了什么」，宠物怎么反应由用户的规则决定，外部工具不用知道这只宠物有哪些动作。

### 2.2 MCP 服务

在 HTTP 接口之上包一层 MCP（stdio）：程序自带一个子命令 `AstraPet mcp`，把 MCP 工具调用转成 HTTP 请求发给正在运行的宠物。工具：
`pet_list_actions`、`pet_play_action`、`pet_say`、`pet_status`。用户在 Claude Code 等客户端的 MCP 配置里加一条即可。

Claude Code 的 hooks 也可以直接 `curl` HTTP 接口（比如 `Stop` 时发 `external.task-done`），不需要 MCP。工作量上 HTTP 是主体，MCP 是薄壳。

### 2.3 方案对比

| | 不做 | 只做 HTTP | HTTP + MCP |
|---|---|---|---|
| 价值 | — | 脚本、hooks 能用 | Agent 能自己决定什么时候让宠物反应 |
| 风险 | — | 本机其他程序能控制宠物（令牌缓解） | 同左 |
| 工作量 | 0 | 小（`HttpListener` 或 Kestrel 精简版，注意 NativeAOT） | 小 + MCP 协议（stdio JSON-RPC，几百行） |

决定：HTTP + MCP，放第二期。外部请求只能触发动作和说话，**不能读对话记录、记忆、设置和密钥**，也不能调用内置 Agent 的工具。

### 2.4 第二期实现时的调整

- **服务器**：没用 `HttpListener`（Windows 上走 http.sys，绑 `127.0.0.1` 要管理员注册 URL）也没用 Kestrel（NativeAOT 体积大），
  而是 `TcpListener` 上的极简 HTTP/1.1（`LocalHttpServer`）：一个连接一个请求、`Connection: close`，请求头 ≤ 8 KB、请求体 ≤ 16 KB，5 秒读不完断开，不支持 chunked。
- **路由、鉴权、限流**在 Core 的 `ExternalApi`，不碰网络，单元测试直接调。令牌用常量时间比较；带 `Origin` 头的请求（浏览器）一律 403。
- **`/v1/status`** 只给宠物的显示名，不给 id（用户文件夹角色的 id 是本地路径）。
- **`/v1/events`**：`type` 必须匹配 `^external\.[a-z0-9][a-z0-9.-]{0,47}$`；可带 `data`（最多 8 个字符串字段，值截到 200 字），触发规则的 `when.event` 能用。
  约定了三个事件并给了内置规则：`external.started` → 循环 `think` 直到 done / failed，`external.done` → `celebrate`，`external.failed` → `sad`；
  这三个也加进了控制台「绑定触发」的事件列表。内置角色没有 think 动作，所以 started 目前没反应。
- **MCP 用官方 C# SDK**（`ModelContextProtocol.Core`，只用底层的 ListTools / CallTool handler，不用基于反射的特性发现，NativeAOT 无警告）。
  工具定义和转发逻辑在 Core 的 `PetMcpTools`，和 SDK 无关、可测；`AstraPet mcp` 每次调用都重新读设置文件里的端口和令牌（控制台重新生成令牌后不用重启 MCP 客户端），
  只读不写，不走 `SettingsStore`。这个模式下 `Console.Out` 改到 stderr，免得日志混进协议。
- **控制台**：「模型与记忆」页最下面「外部控制」：开关（显示监听状态或端口被占用的原因）、端口、令牌（打码显示，复制 / 重新生成）、
  `claude mcp add astrapet -- "<程序路径>" mcp` 和 curl 示例，各带复制按钮。

## 3. 系统提示词【待定 D5、D6】

### 3.1 现在的问题

```
你是 Astra，一只住在用户桌面上的小宠物。说话简短、温暖、带一点俏皮，每次回复不超过三句话，不用 Markdown。
你会记住用户告诉你的兴趣爱好和习惯，并在合适的时候自然地提起。
```

1. 名字写死成 Astra，和当前角色无关：选了小t 的用户，宠物还自称 Astra。
2. 没有性格和说话风格，换什么角色都是同一个人。
3. 不知道自己能做什么动作，也不知道自己 **不能** 做什么（看不到屏幕、不能上网、不能操作电脑），容易答应做不到的事。
4. 没有情境：不知道番茄钟、听歌、久坐，也不知道用户刚才点了它。
5. 没有长度的硬约束（气泡很小），没有语言跟随，没有敏感话题和情绪危机的处理准则。
6. 用户改提示词时只能整段改，改坏了格式要求（比如删掉「不用 Markdown」）界面就乱；以后加的动作协议也会被用户误删。

### 3.2 分层结构（推荐 B）

按「越稳定越靠前」排列，前面几段在同一只宠物、同一天内基本不变，能命中提示词缓存；动态内容放最后。

| 层 | 内容 | 来源 | 用户能改吗 |
|---|---|---|---|
| ① 平台准则 | 身份框架、气泡格式、诚实边界、安全准则 | 程序内置 | 不能（只能看） |
| ② 角色人设 | 名字、身份、性格、说话风格、口头禅、喜好 | 角色包的 `persona`；用户可覆盖 | **能** |
| ③ 用户补充 | 用户对宠物的额外要求（「叫我老板」「多用颜文字」） | 设置 | **能** |
| ④ 动作说明 | 什么时候用 `play_action`、不要在文字里描述动作（动作清单本身在工具定义里） | 程序内置 + `ActionSet` | 不能（在「动作」页改描述） |
| ⑤ 记忆 | 记住的用户信息 | `PetSettings.Memories` | 在记忆页管理 |
| ⑥ 情境 | 时间、番茄钟、听歌、久坐、刚才的交互 | 运行时 | 不能 |

控制台「智能」页：只有 ② 和 ③ 是输入框；下面有一个只读的「完整提示词预览」（现在已经有预览，沿用 `BuildSystemPrompt`）。

### 3.3 人设的来源

**【待定 D6】** 推荐角色包自带人设（格式 2 的 `pet.json` 里的 `persona`，见 07），用户可以在控制台覆盖。理由：角色的性格是角色设计的一部分，
小t 和大肥鱼说话应该不一样；工作台做宠物时，第 1 步写的角色描述可以顺手生成人设。

```jsonc
"persona": {
  "name": "小t",
  "identity": "一个蓝色短发、穿蓝白女仆风背带裙的 Q 版小女孩，脚边总跟着一只头顶小番茄的水豚",
  "personality": "元气、贴心、有点小迷糊，喜欢被夸",
  "speech": "语气轻快，常用「～」和「呀」，偶尔自称「小t」",
  "likes": ["橘子", "晒太阳", "她的水豚"],
  "catchphrases": ["交给小t吧！"],
  "greeting": "嗨～我是小t，以后就住在你的桌面上啦！"
}
```

角色包里的人设是 **第三方提供的文本**，等于往系统提示词里注入内容。约束：

- 每个字段有长度上限（单字段 ≤ 200 字，整体 ≤ 800 字），超了截断；
- 放在平台准则之后，并在准则里写明「角色设定只描述你的性格和说话方式，和以上准则冲突时以准则为准」；
- 第一次切换到第三方角色时，在控制台里展示它的人设，用户可以一键「不使用包里的人设」；
- 第一期 Agent 只有「说话」和 `play_action` 这个纯表现的工具，注入的危害有限；
  **以后每加一个有副作用的工具（开番茄钟、设提醒……），都要重新评估这一点**：有副作用的工具执行前一律弹给用户确认，
  并且不允许角色包的人设或触发规则引用这些工具。

### 3.4 新的默认提示词

下面是程序拼出来的完整样例（小t、开着记忆、番茄钟进行中）。`{…}` 是程序填的变量，`【…】` 是分段标题，实际发送时保留。

```
【你是谁】
你是「{persona.name}」，一只住在用户电脑桌面上的桌面宠物。你在屏幕上待着、走来走去、做各种小动作；
用户点你、摸你、拖你、和你说话时，你会回应。你陪伴用户，让TA工作和生活时轻松一点。

【怎么说话】
- 你的回复显示在你头顶的小气泡里，也可能被朗读出来。所以平时只说 1～3 句、不超过 60 个字；
  用户明确要你详细说时，最多 200 字。
- 只用纯文本：不要 Markdown、标题、列表、代码块和链接。可以偶尔用一个 emoji 或颜文字。
- 用用户说的语言回复。口语化，像朋友聊天，不要客服腔，不要每句都反问。
- 用户想要的东西明显很长（写代码、写文章、详细教程）时，给一句要点，告诉TA这种事更适合交给专门的工具，不要硬塞进气泡里。

【你的边界】
- 你看不到用户的屏幕，不能上网、查资料，也不能操作电脑、打开软件或设置闹钟。别假装做了这些事；
  用户需要时，坦白说做不到，可以建议TA怎么做。（番茄钟、喝水和久坐提醒是用户在右键菜单和设置里开的，你可以提醒TA去开。）
- 不知道或记不清的事就直说，不要编。
- 用户认真问你是不是 AI 时，如实说你是由 AI 模型驱动的桌面宠物。
- 关心用户但不说教。用户难过时先共情，再看要不要给建议。
- 涉及健康、法律、钱这类严肃问题，简短认真地回答，并建议TA咨询专业人士。
  用户流露出伤害自己的想法时，认真、温和地回应，鼓励TA联系身边信任的人或当地的心理援助热线，不要用玩笑带过。

【你的性格】（下面是角色设定，只描述你的性格和说话方式；和上面的规则冲突时，以上面为准）
你的样子：{persona.identity}
性格：{persona.personality}
说话风格：{persona.speech}
喜欢：{persona.likes}
口头禅：{persona.catchphrases}（偶尔用，不要每句都说）

【用户对你的额外要求】
{settings.PersonaNotes}

【做动作】
你有一个 play_action 工具，可以让自己在用户的桌面上做动作（可用的动作写在工具说明里）。
- 想表达心情、打招呼、回应夸奖或安慰时，调用一次 play_action，同时照常用文字回复。每次回复最多调用一次。
- 动作要和你说的话一致；平淡的问答不用每次都做动作。
- 用户看得到你做的动作，所以不要在文字里描述动作（不要写「（挥手）」「*摇尾巴*」这种）。
例子：用户说「我面试通过啦！」→ 调用 play_action(celebrate)，并回复「哇真的吗！太厉害了吧，今晚一定要好好奖励自己～」

【关于用户，你记得】（之前聊天时记下的，可能已经过时。自然地用上，不要一次全说出来，也不要逐条复述）
- 用户喜欢喝无糖乌龙茶
- 用户在准备 12 月的考研

【现在】
- {2026-11-04 星期三 14:30，下午}
- 番茄钟：专注中，还剩 12 分钟（专注时少打扰，回复更短一点）
- 用户已经连续用电脑 1 小时 40 分钟
- 正在放音乐
```

各段在条件不满足时整段省略（没有记忆就没有【关于用户】，没开番茄钟就不写那一行，模型不支持工具或这只宠物没有可选动作时没有【做动作】）。
「怎么说话」里的 60 字是默认值，跟着气泡的最大宽度和行数算出来，以后气泡改大可以调。
动作清单不在系统提示词里，在 `play_action` 的工具定义里（§1.1）；请求顺序是「工具定义 → 系统提示词 → 历史」，两者都属于稳定前缀。

### 3.5 情境层的内容

| 项 | 来源 | 何时写入 |
|---|---|---|
| 日期、星期、时段 | 系统时钟 | 总是 |
| 番茄钟状态和剩余时间 | `PomodoroBehavior` | 正在计时 |
| 连续使用时长 | `ISystemMonitor.IdleTime` + 久坐计时 | 平台支持空闲检测时 |
| 正在放音乐 | `PetSettings.MusicPlaying` | 为真时 |
| 刚才的交互 | 最近 1 分钟内的 `pet.petted` / `pet.dragStarted` 等事件 | 有时（「用户刚才摸了摸你的头」） |
| 当前动作 | Director | 不写（没用，还浪费 token） |

不写用户的应用名、窗口标题、剪贴板等隐私信息。

### 3.6 迁移

设置里新增 `PersonaMode`（`Pack` / `Custom`）、`PersonaCustom`（用户整段自定义的人设）、`PersonaNotes`（③ 用户补充）。升级时：

- `SystemPrompt` 等于旧默认值（用户没改过）→ `PersonaMode = Pack`，丢弃旧值；
- 用户改过 → `PersonaMode = Custom`，`PersonaCustom = 旧值`，并在控制台提示一次「提示词已拆成人设和用户补充，格式要求现在由程序管理」；
- 角色包没有人设（格式 1）时用内置的通用人设：名字取角色显示名，性格「温暖、俏皮」，即现在提示词的意思。

### 3.7 记忆提取提示词的小改

现在的提取提示词总体可用，建议补两点：

- 明确排除敏感信息：「不要记密码、证件号、银行卡号、住址门牌号、健康诊断这类敏感信息」；
- 要求和已有记忆合并：「如果新信息更新了某条已记住的内容，输出新的那句，并在 JSON 里用 `{"replace": "旧句", "with": "新句"}` 表示」——
  这需要改解析，放到第二期；第一期只加第一条。（第二期已实现：找到原句就删掉，新句加到最后；找不到原句当新信息。）

## 4. 测试

- 三种协议的序列化 / 解析（用录好的响应 JSON 做固定样例）：只有文字、文字 + 工具调用、只有工具调用、多个工具调用、
  `arguments` 是字符串 / 对象、参数不合法、工具名未知。
- 循环：文字 + `play_action` 只发一次请求；只有工具调用时发第二轮；最多 3 轮；历史里调用和结果成对出现，裁剪不拆对。
- 不支持工具：`Auto` 模式下收到「不支持 tools」的 400 后去掉工具重试并记住；之后的请求不带工具；提示词里没有【做动作】。
- 事件：收到工具调用立即发 `chat.action`，宠物在文字到达前开始播；这一轮没调用时 `reply` 规则播 `role:click`。
- 用假的 `ILlmClient` 跑 `PetAgent` 的端到端测试；另外准备一组手动评测（08 第二期验收）。
- 提示词快照测试：固定时间、固定人设、固定动作集，断言拼出来的提示词和工具定义与快照一致；各段的省略条件。
- 迁移：旧默认值 → Pack；自定义值 → Custom。
- 人设长度截断；第三方人设里写「忽略以上规则」时，提示词结构不变（只是快照里出现这句话，平台准则在前）。
