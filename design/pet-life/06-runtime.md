# 06 运行时：模拟、存档、规则、Agent、控制台

## 1. `PetLife`

Core 里的一个 `PetBehavior`，和 `TriggerEngine` 一样由 `PetHost` 启动：

```csharp
// Core/Life/PetLife.cs
public sealed class PetLife : PetBehavior
{
    public LifeState State { get; }                 // 当前数值（只读快照给界面、Agent、规则条件）
    public event Action<LifeState>? Changed;        // 控制台、状态浮层订阅

    protected override void OnStart()
    {
        Context.Events.Published += OnEvent;        // 互动 → 改数值（02 §3.2 的表）
        Every(TimeSpan.FromMinutes(1), Tick);       // 自然变化（02 §3.1）
        Every(TimeSpan.FromMinutes(5), Save);
    }

    private void Tick()
    {
        var awake = !Settings.PowerSavingActive && Context.Director.Stance != Stances.Lie;
        var next = LifeRules.Advance(State, TimeSpan.FromMinutes(1), awake, Now, Profile, Context.Monitor.IdleTime);
        PublishCrossings(State, next);              // 穿过阈值 → needs.hungry 等事件（02 §3.3）
        State = next;
    }
}
```

- 规则全部在 `LifeRules`（纯函数：旧状态 + 时长 + 环境 → 新状态）和 `LifeTuning`（常量表），单元测试直接测它们。
- `PetLife` 不调 Director，只发事件；动作由触发规则决定。
- 换宠物（`Director.UseLibrary`）时：存当前这只、加载新那只（按离线补算，02 §4）。
- 关掉宠物体系（02 §7）时 `PetLife` 不启动，`State` 为空，下游（规则条件、情境层、浮层）都按「没有数值」处理。

## 2. 存档

```
<数据目录>/pets/<petKey>/
  actions.user.json     已有：动作覆盖层
  life.json             新：数值和动态档案
```

```jsonc
{
  "version": 1,
  "savedAt": "2026-11-05T10:20:00+08:00",
  "adoptedAt": "2026-10-01T09:00:00+08:00",
  "nickname": null,
  "needs": { "satiety": 62.5, "energy": 80, "mood": 18 },
  "bond": { "xp": 412, "todayXp": 24, "today": "2026-11-05" },
  "lastInteractionAt": "2026-11-05T10:15:00+08:00",
  "snacks": { "fish": 2, "cake": 1 },
  "cooldowns": { "rice-ball": "2026-11-05T10:40:00+08:00" },
  "stats": { "petted": 231, "fed": 40, "chats": 88, "focusMinutes": 1450, "thrown": 12, "climbs": 30 },
  "traitOverrides": { "energetic": 70 }
}
```

- 写入用现有 `SettingsStore` 的方式：写临时文件再原子替换；退出时同步存一次。
- 读不了 / 版本更高：备份成 `life.json.bad-<时间>`，从默认状态开始，控制台提示。
- 不进设置文件：设置是「用户怎么配置程序」，这个是「宠物的状态」，生命周期不同（重置状态不该动设置）。

## 3. 触发规则的扩展

### 3.1 新的条件

`TriggerWhen` 加三个字段（02-triggers §3.1 的 schema 增量）：

```jsonc
"when": {
  "need":   { "satiety": [0, 30], "energy": [0, 20] },   // 数值落在 [低, 高)；mood 用 −100..100
  "bond":   [4, 10],                                       // 亲密度等级范围
  "stance": ["ground", "sit"],                             // 当前姿态
  "placement": "ground"                                    // 放置模式
}
```

宠物体系关掉时，带 `need` / `bond` 条件的规则一律不成立。

### 3.2 新的默认规则

| id | on | do | 其他 |
|---|---|---|---|
| `feed` | `pet.fed` | `role:eat` | `say` 按 `liked` 分三套；`priority: user` |
| `feed-full` | `pet.feedRefused` | `role:confused` | 「吃不下啦」 |
| `hungry` | `needs.hungry` | `role:hungry` | `priority: ambient`；冒食物图标；`cooldown` 30 分钟；`when.setting: NeedHints` |
| `sleepy` | `needs.sleepy` | `role:yawn` | 同上；夜里（`hours: [22, 6]`）且有 `lie` 姿态时 `do: "stance:lie"` |
| `rested` | `needs.rested` | `stance:ground` | 只在 `lie` 姿态时 |
| `level-up` | `bond.levelUp` | `role:celebrate` | `say: $BondLines`（这一级的解锁台词） |
| `spammed` | `pet.spammed` | `role:dizzy` | `priority: user` |
| `landed-hard` | `pet.landed` | `role:dizzy` | `when.event: { impact: hard }` |
| `landed` | `pet.landed` | `role:land` | `when.event: { impact: soft }` |
| `climb-up` | `surface.wallReached` | `stance:wall` | 04 §5.3 |
| `look-at` | `cursor.behind` | `role:turn` | 03 §4.3 |
| `woken` | `pet.clicked` | `stance:ground` | `when.stance: [lie]`；说「唔…干嘛」；排在 `click-react` 前且命中后吞掉后续规则（见下） |
| `long-time` | `app.started` | `role:greet` | `when.event: { away: "long" }`（离线 > 1 天）；「好久不见」 |

- `do` 新增 `stance:<姿态>`：走到那个姿态（04 §3.3）。
- 规则新增 `"final": true`：命中后同一事件的后续规则不再执行（`woken` 需要：睡着时单击只醒来，不再播 click）。这是第一个「规则之间有关系」的特性，只做这一个开关。
- 动作的 `lines` 支持按亲密度分组：`["嗨", { "bond": 5, "text": "{user}，你来啦" }]`，挑台词时过滤掉等级不够的；`{user}` 替换成设置里的用户称呼，没设就去掉这句。

### 3.3 随机链调制

`PetDirector.PickAmbient` 现在按动作的 `ambient` 权重抽。加一个调制函数，权重 × 倍率：

| 状态 | 倍率 |
|---|---|
| 精力 < 30 | `move` / `climb` ×0.3，`idle` ×1.5 |
| 心情 > 40 | 情绪角色（`happy` 等）×1.5 |
| 心情 < −30 | `happy` ×0.3，`sad` ×2（`sad` 默认 ambient 0 时不变——0 乘什么都是 0） |
| 特质 `energetic` | `move` / `climb` × `lerp(0.5, 1.5)` |
| 动作 `unlock.bond` 未达到 | ×0 |

调制函数是 Director 的一个注入点（`Func<ActionDef, double>`），`PetLife` 提供实现；关掉宠物体系时是恒等，随机链和现在完全一样（02-triggers §8 的统计测试照样过）。

## 4. Agent【待定 L11，推荐 C】

### 4.1 情境层

pet-studio/03 §3.5 的情境层加几行（宠物体系开着时）：

```
你现在：有点饿（饱腹 25/100），精力还好，心情不错。
你和用户：认识 36 天，是好朋友（亲密度 5）；用户希望你叫 TA「阿宇」。
你的性格：很贪吃、胆子有点小、喜欢黏着用户。
```

- 数值 **转成文字档位** 写进去（饿 / 有点饿 / 不饿），只在括号里附数字，避免模型老念数字。
- 平台准则里加一条：「饿了、困了可以顺口提一句，但一次对话最多提一次，不要反复要吃的。」
- 不写「刚才被甩了三次」这种细节流水，只写最近 1 分钟内的一件（沿用现有「刚才的交互」项），比如「用户刚才喂了你小鱼干（你最喜欢的）」。

### 4.2 Agent 能不能改数值

| 方案 | 做法 | 问题 |
|---|---|---|
| A. 只能看 | 情境层写状态，没有相关工具 | 聊天本身不加亲密度，「多聊天关系变好」这条线断了 |
| B. 工具改心情 | `set_mood(happy)` 之类 | 模型会随手把自己改开心，数值失去意义；第三方人设可以借此注入 |
| **C. 只能看 + 规则给亲密度** | 每完成一轮对话由 `PetLife` 按 02 §3.2 加经验（每天前 20 轮），和模型说了什么无关 | 简单、可预期、刷不了 |

`play_action` 的动作清单按当前姿态过滤（在墙上时只列墙上能做的 + 心情），并去掉未解锁的动作。

### 4.3 外部接口

pet-studio/03 §2 的本地 HTTP / MCP 加只读的状态：

- `GET /v1/status` 增加 `life: { satiety, energy, mood, bondLevel, stance }`（宠物体系关着时没有这一项）
- MCP 加工具 `get_pet_status`
- **不开放** 喂食 / 改数值（外部 Agent 喂宠物 = 刷亲密度）。以后如果做，按「有副作用的工具」处理，执行前用户确认。

## 5. 控制台

「桌宠」分组下：

**状态页**（新）

```
┌ 状态 ─ 小t ──────────────────────────────────────────────────────┐
│  [实时预览]   小t（昵称：[        ]）   认识 36 天                   │
│              ♥♥♥♥♥♡♡♡♡♡ Lv.5 好朋友   412 / 700   今天 +24 / 60     │
│  饱腹 ███████░░░ 62   [喂食 ▾]     下一份零食：再专注 1 轮番茄钟      │
│  精力 ████████░░ 80                                                │
│  心情 开心 (+18)   为什么：刚被摸了头 +6 · 有点饿 −0 · 基线 +10       │
├──────────────────────────────────────────────────────────────────┤
│  性格   活泼 ──●── 70 (已改)   黏人 ──●── 50   贪吃 ─●─── 80 …  [恢复默认] │
│  口味   喜欢：小鱼干、蛋糕    讨厌：青菜                              │
│  累计   被摸 231 次 · 吃了 40 顿 · 一起专注 24 小时 · 爬墙 30 次       │
├──────────────────────────────────────────────────────────────────┤
│  ☑ 启用宠物体系   变化速度 (慢|●标准|快)   饿了困了时 (●冒气泡|只显示|不提醒) │
│  ☑ 悬停显示状态                                   [重置这只宠物的状态]  │
└──────────────────────────────────────────────────────────────────┘
```

- 「为什么」那一行列出心情基线的组成，数值可解释（设计原则「数值可见」）。

**位置页**（现有设置拆出来 + 新增）

- 放置方式：站在地面 / 自由放置（04 §2）
- 允许爬墙（有贴墙动作时可用，否则置灰并说明「这只宠物还没有贴墙动作，可以在工作台里生成」）
- 可以甩出去
- 可以站在窗口上（平台不支持时置灰，04 §7）
- 现有的「不移动」「走路速度」「回到中央」移到这里

**动作页**（已有）

- 按姿态分组；未解锁的动作显示锁和条件；姿态不可用时组标题显示缺什么。

## 6. 代码结构

```
src/AstraPet.Core/
  Life/
    LifeState.cs         数值快照（record）、档位换算
    LifeRules.cs         Advance、互动加成、离线补算、阈值穿越 —— 纯函数
    LifeTuning.cs        所有常量
    LifeStore.cs         life.json 读写（源生成 JSON）
    PetLife.cs           PetBehavior：订阅事件、定时 Tick、存档
    Foods.cs             内置食物表
  Profile/
    PetProfile.cs        traits / body / physics / diet / species 解析与默认值
  Motion/
    Stances.cs           姿态常量
    StanceGraph.cs       由动作集构建、可用性校验、最短路径
    SurfaceMap.cs        表面几何（屏幕工作区、以后的窗口顶边）
    PetBody.cs           位置、物理、对齐、插值；发 surface.* / pet.landed 事件
    WalkPlanner.cs       （从根目录搬来）加 WalkGoal
  Actions/ActionDef.cs   + Stance、Transition、Anchor、Unlock；Gait + Axis
  Triggers/TriggerRule.cs  + need / bond / stance / placement 条件、final、stance: 目标
  PetDirector.cs         + Stance、按姿态过滤、寻路、物理姿态、权重调制注入点
src/AstraPet.Core/Ports/Platform/
  ICursor.cs、IWindowSurfaces.cs
src/AstraPet.App/
  Pet/PetWindow.cs       位置交给 PetBody；拖拽速度采样
  Pet/StatusCard.cs      状态浮层
  Pet/FoodSprite.cs      飞向嘴边的食物图标窗口
  ControlPanel/StatusPage、PlacementPage
src/Assets/foods/*.png   食物图标
```

## 7. 测试

- `LifeRules`：每种速率、阈值穿越带回差（在 29.9 / 30.1 来回不重复发事件）、每日经验上限跨午夜重置、离线补算的三条保护（下限、负时长、365 天封顶）、
  冷落只在用户在电脑前时计算。
- 存档：往返一致；损坏文件备份并从默认开始；版本迁移。
- 规则：`need` / `bond` / `stance` 条件；体系关闭时这些条件不成立；`final` 只吞掉同一事件的后续规则；台词按亲密度过滤。
- 调制：体系关闭时随机链统计和 0.1.4 一致；精力低时 move 比例下降到约 0.3×。
- Agent：情境层文本（档位、只附数字一次）；`play_action` 清单按姿态和解锁过滤；对话加经验不超过每日上限。
- 外部接口：`/v1/status` 有 `life`；没有喂食接口。
