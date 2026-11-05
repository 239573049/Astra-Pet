# 02 触发系统与 Director 改造

## 1. 目标

- 所有「什么时候播什么」都变成 **事件 → 规则 → 动作请求**，不再在 `PetWindow`、各个 Behavior、`PetCompanion` 里直接调 `Director.Play(PetLibrary.Click)`。
- 内置一套默认规则，效果和现在完全一样；用户可以改、可以加。
- Agent、外部 Agent、控制台的 ▶ 按钮，都是往同一个入口提交「动作请求」，由 Director 按优先级决定打不打断。

```
 输入/系统/Agent           事件总线             触发引擎                 Director
 ─────────────────      ─────────────      ─────────────────       ────────────────
 PetWindow 单击  ─┐                        匹配规则（when / 概率 /    按优先级：打断 /
 番茄钟到点     ─┼──▶  PetEvent  ──────▶  冷却）→ 选动作 → ──────▶  排队 / 丢弃
 听歌开始       ─┤                        ActionRequest              ↓
 Agent 回复     ─┘                                                  播放、ClipStarted
 Agent 的 play_action、外部 API、控制台 ▶ ──────────(直接)────────▶ ActionRequest
```

## 2. 事件

```csharp
// Core/Triggers/PetEvent.cs
public sealed record PetEvent(string Type, IReadOnlyDictionary<string, string>? Data = null);
```

事件类型是字符串，方便规则文件引用、方便以后加。第一期的事件：

| 事件 | 发出方（现有代码位置） | 数据 |
|---|---|---|
| `pet.clicked` | `PetWindow` 松手且不是拖拽（现在直接播 click） | `zone`：`head` / `body` / `feet`（按画布上的身高比例划分，见 README 改进 3） |
| `pet.doubleClicked` | `PetWindow` | |
| `pet.petted` | `PetWindow` 摸头 | |
| `pet.dragStarted` / `pet.dragEnded` | `PetWindow`（现在直接设 `Director.Dragging`） | |
| `app.started` | `GreetingBehavior` | `hour` |
| `time.hour` | `ReminderBehavior` 整点 | `hour` |
| `reminder.drink` / `reminder.sit` | `ReminderBehavior` | |
| `pomodoro.started` / `pomodoro.focusEnded` / `pomodoro.restEnded` / `pomodoro.stopped` | `PomodoroBehavior` | `minutes` |
| `music.started` / `music.stopped` | `PresenceBehavior` | |
| `power.saving` / `power.resumed` | `PresenceBehavior` | |
| `chat.thinking` / `chat.action` / `chat.replied` / `chat.failed` | `PetAgent`（03 §1.6） | `chat.action`：`action`（`play_action` 的参数）；`chat.replied`：`acted`（这一轮是否调用过 `play_action`） |
| `memory.added` | `PetAgent.Remembered` | |
| `update.found` | `UpdateNotifyBehavior` | `version` |
| `hotkey.<名字>` | 全局快捷键（第二期，看平台能力） | |
| `external.<名字>` | 外部 Agent 自定义事件（见 03 §2） | 任意 |

总线就是一个同步的发布 / 订阅，在 UI 线程上分发：

```csharp
public sealed class PetEventBus
{
    public event Action<PetEvent>? Published;
    public void Publish(PetEvent e) => Published?.Invoke(e);
}
```

`PetContext` 加一个 `Events` 属性；Behavior 从「直接播动作」改成「发事件」。比如 `PomodoroBehavior.Tick` 里的
`Context.Director.Play(PetLibrary.Click, fromUser: true)` 换成 `Context.Events.Publish(new("pomodoro.focusEnded"))`。

## 3. 触发规则

### 3.1 schema

```jsonc
{
  "id": "click-react",              // 规则 id；用户规则同 id 覆盖内置规则
  "on": "pet.clicked",              // 事件类型；支持末尾通配：pomodoro.*
  "do": "role:click",               // 动作 id | role:<角色> | none（只说话不动）
  "say": ["嘿嘿", "干嘛呀"],         // 可选：冒一句气泡（不填用动作自己的 lines）
  "speak": false,                   // 气泡同时朗读
  "chance": 1.0,                    // 0~1
  "cooldown": 0,                    // 秒，这条规则两次生效的最小间隔
  "priority": "user",               // ambient | system | agent | user，见 §4
  "when": {                         // 全部可选，AND 关系
    "hours": [8, 23],               // 本地时间 [起, 止)，可跨午夜 [22, 6]
    "powerSaving": false,
    "music": null,
    "setting": "ClickReact",        // 某个布尔设置为真时才生效，用来接上现有的开关
    "event": { "acted": "false" }   // 事件数据逐项相等时才生效
  },
  "enabled": true
}
```

故意不做的：条件表达式语言、脚本、链式规则（规则 A 触发规则 B）。需求真出现了再加。

### 3.2 内置默认规则（等价于现在的行为）

| id | on | do | 其他 | 现在对应的代码 |
|---|---|---|---|---|
| `click-bubble` | `pet.clicked` | `none` | `say: $ClickLines`，`setting: ClickBubble`；排在 `click-react` 前面，动作自带台词时被它盖过 | `InteractionBehavior.OnClicked` |
| `click-react` | `pet.clicked` | `role:click` | `setting: ClickReact`；`priority: user` | `PetWindow` |
| `double-click-chat` | `pet.doubleClicked` | `none` | 由 `InteractionBehavior` 打开对话（这是命令，不是动作，不走规则） | — |
| `petted` | `pet.petted` | `role:click` | `say: $PettingLines`；`priority: user` | `InteractionBehavior.OnPetted` |
| `greet` | `app.started` | `role:greet` | 台词按时段（保留现在的文案） | `GreetingBehavior` |
| `pomodoro-end` | `pomodoro.focusEnded` | `role:celebrate` | | `PomodoroBehavior.Tick` |
| `pomodoro-rest-end` | `pomodoro.restEnded` | `role:click` | | 同上 |
| `drink` | `reminder.drink` | `role:drink` | 台词沿用 `DrinkLines` | `ReminderBehavior` |
| `sit` | `reminder.sit` | `role:stretch` | | 同上 |
| `music` | `music.started` | `role:sing` | `when.powerSaving: false`；播一次，之后由导演按设置续播（见下） | `PresenceBehavior` + `Director.NextCategory` |
| `think` | `chat.thinking` | `role:think` | 循环直到 `chat.action` / `chat.replied` / `chat.failed` | 无（现在只显示「…」气泡） |
| `agent-action` | `chat.action` | 事件里的 `action`（动作 id 或心情） | `priority: agent` | 无（新） |
| `reply` | `chat.replied` | `role:click` | `when.event: { acted: false }`；`priority: agent` | `PetCompanion.ChatAsync` |

注意：`role:drink`、`role:celebrate` 这类角色在老宠物里没有对应动作，靠回退链落到 `click`，所以老宠物行为不变。

**第一期实现时的调整：**

- **拖拽不是规则**，是导演的状态（`Director.BeginDrag` / `EndDrag`）：拖拽必须立即接管、松手立即结束，不该被用户规则关掉或改掉。
  `pet.dragStarted` / `pet.dragEnded` 照样发布，用户可以另外绑规则（比如拖拽时说一句）。
- **听歌续播留在导演里**：`music` 规则只负责「开始放歌时唱一次」，之后的随机链里，`MusicDetected && SingWhenMusic` 时继续抽 `sing`，
  和 0.1.4 一样；不做成「循环到 `music.stopped`」，因为听歌常常一放一小时，循环上限（10 分钟）会让它中途停下。
- **`sleep` 推迟到第三期**（分段动作要等处理管线能产出 enter / loop / exit 三段）；现在省电时随机链只抽 `idle`，和 0.1.4 一样。
- 单击拆成 `click-bubble`（说话）和 `click-react`（动作）两条，对应「行为」页的两个开关，可以分别关掉。
- `say` 里的特殊值：`$ClickLines`、`$PettingLines` 展开成设置里的台词列表；`do: "$action"` 表示动作取事件数据里的 `action`（`agent-action` 用）。
- 和 0.1.4 的细微差别：番茄钟结束用 `system` 优先级（原来按用户操作处理，播完不再强制回待机）；打招呼、喝水、久坐提醒现在也会做动作
  （老宠物没有 `greet` / `drink` / `stretch`，回退到 `click`）。

### 3.3 规则存放

- 内置规则：代码里（`DefaultTriggers.cs`），不写进设置文件，升级时可以改。
- 用户的全局规则（只引用 `role:` 的，和具体宠物无关）：`PetSettings.Triggers`。
- 只对某只宠物生效的规则（引用了具体动作 id）：这只宠物覆盖层的 `actions.user.json`（见 01 §4.2）。控制台「绑定触发」生成的规则 id 是 `bind-<动作 id>-<事件>`，`pet.*` 事件用 `user` 优先级，其余用 `system`。
- 合并顺序：内置 → 全局 → 宠物；同 id 后者覆盖前者，`enabled: false` 可以关掉内置规则。

控制台「动作」页的「绑定触发…」就是往宠物规则里加一条 `do: <动作 id>` 的规则；「行为」页的开关（单击反应、单击说话……）继续是设置项，
通过规则里的 `setting` 条件生效，所以老设置不用迁移。

## 4. 优先级与打断

| 优先级 | 来源 | 例子 |
|---|---|---|
| `ambient` (0) | 随机链 | 空闲时随机走动、东张西望 |
| `system` (1) | 定时和系统状态 | 提醒喝水、听歌、番茄钟、省电 |
| `agent` (2) | 内置 Agent 回复、外部 Agent | `play_action(wave)` |
| `user` (3) | 用户直接操作 | 单击、摸头、控制台 ▶ |
| （拖拽） | 特殊：总是立即打断，拖拽期间只认拖拽 | |

规则：

1. 新请求优先级 **≥** 当前片段的优先级，且当前动作的 `interrupt` 允许 → 立即打断。
2. 否则，`agent` 和 `system` 的请求进入 **待播队列**（最多 3 个，每个有效期 10 秒，过期丢弃），当前片段播完后依次播；`ambient` 直接丢弃。
3. 用户操作触发的片段播完后回待机（保留现在的「FromUser 播完回 idle」规则），然后再看队列。
4. 循环类动作（drag、think、sing、sleep 的 loop 段）带一个「结束条件」：结束事件到达、或被更高优先级打断、或超时。

## 5. PetDirector 改造

### 5.1 接口

```csharp
public enum ActionPriority { Ambient, System, Agent, User }

public sealed record ActionRequest(
    ActionDef Action,
    ActionPriority Priority,
    string Source,                     // "rule:click-react" / "agent" / "external:claude-code" / "console"
    TimeSpan? LoopFor = null,          // 循环类：最多循环多久
    string? UntilEvent = null);        // 循环类：等到这个事件结束

public sealed class PetDirector
{
    public PlayResult Request(ActionRequest request);   // Played / Queued / Rejected
    public void StopLoop(string untilEvent);            // 触发引擎收到结束事件时调
    public ActionDef CurrentAction { get; }
    public ActionPriority CurrentPriority { get; }
    public event Action<ActionDef, IFrameSource>? ActionStarted;   // 取代 ClipStarted，窗口用它处理走路和镜像
    // Tick()、Direction、UseLibrary() 保持
}
```

现有的 `Play(string category, bool fromUser)` 保留一个过渡版本，内部转成 `role:` 请求，迁移完再删。

### 5.2 下一段怎么选（取代 `NextCategory`）

```
播完一段后：
  1. 正在拖拽            → drag 角色
  2. 有循环中的请求且没满足结束条件 → 同一动作再来一遍（loop）或分段动作的下一段
  3. 刚播完的是用户触发的 → idle
  4. 队列里有没过期的请求 → 出队播放
  5. 在动作间隔（ActionInterval）内 → idle
  6. 否则按 ambient 权重从「启用、未在冷却、满足条件」的动作里抽：
       - StayStill 时排除 move
       - 省电时只有 idle（或 sleep 规则接管）
       - 权重全为 0 → idle
```

和现在一样，下一段在当前段开始时就预取（`_queued`），所以第 4 步的「出队」要在预取时就决定；
有新请求进队时，丢掉预取的那一段重新预取。

### 5.3 转身

`turn` 是必需角色（D14），所以每只宠物都有转身动作，这部分逻辑和现在一样：转身片段播完才翻转 `Direction`。

## 6. 触发引擎

```csharp
// Core/Triggers/TriggerEngine.cs —— 是一个 PetBehavior，跟其他行为一样由 PetHost 启动
public sealed class TriggerEngine : PetBehavior
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new();

    protected override void OnStart() => Context.Events.Published += OnEvent;

    private void OnEvent(PetEvent e)
    {
        foreach (var rule in Context.Triggers.Matching(e.Type))         // 已合并好的规则表
        {
            if (!rule.Enabled || !Conditions.Hold(rule.When, Context)) continue;
            if (OnCooldown(rule) || Context.Random.NextDouble() >= rule.Chance) continue;
            _lastFired[rule.Id] = Now;

            var action = Resolve(rule.Do, e);                          // id / role:x / 事件里的 action
            if (action is not null)
                Context.Director.Request(new ActionRequest(action, rule.Priority, "rule:" + rule.Id,
                    UntilEvent: rule.Until));
            if (PickLine(rule, action) is { } line)
                Context.Voice.Say(line, rule.Speak);
        }
        if (e.Type.StartsWith("music.stopped") || ...) Context.Director.StopLoop(e.Type);
    }
}
```

同一事件命中多条规则时都执行；动作请求按优先级在 Director 里自然去重（同优先级后到的打断先到的）。

## 7. 代码结构

```
src/AstraPet.Core/
  Actions/
    ActionDef.cs            ActionDef、ActionKind、InterruptPolicy、ActionPhases
    ActionSet.cs            查询、按角色挑选、回退链、按权重随机
    ActionManifest.cs       pet.json 格式 2 的 actions 解析；格式 1 合成（01 §5）
    ActionOverrides.cs      actions.user.json 的读写与合并
    Roles.cs                内置角色常量、默认权重、回退链
  Triggers/
    PetEvent.cs  PetEventBus.cs
    TriggerRule.cs          规则 + 条件的数据结构和 JSON（源生成，NativeAOT 友好）
    TriggerTable.cs         合并内置 / 全局 / 宠物规则，按事件类型索引
    DefaultTriggers.cs      §3.2 的表
    TriggerEngine.cs
  PetDirector.cs            §5
  PetLibrary.cs             持有 ActionSet；Load 支持格式 1 / 2
src/AstraPet.App/
  Pet/PetWindow.cs          输入改为发事件；用 ActionStarted 处理走路 / 镜像
  ControlPanel/ActionsPage.axaml(.cs)、ActionsViewModel.cs
```

JSON 一律用 `System.Text.Json` 源生成（`JsonSerializerContext`），保持 NativeAOT。

## 8. 测试

都在 `tests/AstraPet.Tests`，用 `MemoryFrameSource` 和固定种子的 `Random`、假时钟：

- 默认规则下，单击 → 播 click 角色的动作，播完回 idle（和 `PetDirectorTests` 现有断言一致）。
- 老角色包（格式 1）合成的动作集，随机链各角色的出现频率与现在的 30/10/40/20 一致（统计测试，固定种子）。
- 优先级：ambient 片段中来了 agent 请求 → 打断；user 片段中来了 agent 请求 → 排队，user 片段播完回 idle 后出队；队列项 10 秒过期。
- 循环：`chat.thinking` 播 think 循环，`chat.action` 到达后停止循环并播 Agent 选的动作；没有调用工具时 `chat.replied` 停止循环并播 click。
- 加载：五个必需角色缺任何一个都拒绝加载；覆盖层禁用了某必需角色的最后一个动作时，该覆盖项被忽略。
- 回退链：没有 `celebrate` 和 `happy` 的宠物，番茄钟结束播 click。
- 覆盖层：禁用的动作不会被随机到；权重改为 0 的动作只能被显式触发；孤儿覆盖项被忽略且有日志。
- 规则的时间条件跨午夜、冷却、概率。
