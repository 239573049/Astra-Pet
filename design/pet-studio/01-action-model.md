# 01 动作模型与自定义

## 1. 概念

| 概念 | 是什么 | 谁引用它 |
|---|---|---|
| **片段 Clip** | 一个 640×360、VP9 带 alpha 的 webm 文件，对应现在的 `PetClip` | 只有动作引用片段 |
| **动作 Action** | 有稳定 id 的一个动作：一个或多个片段（播放时随机抽一个）+ 名字、描述、语义角色、播放属性 | 触发规则、Agent、控制台、随机链 |
| **语义角色 Role** | 系统认识的语义槽，比如 `idle`、`drag`、`happy`。一个动作可以有多个角色 | 程序按角色找动作（「拖拽时播哪个」「开心时播哪个」） |

和现在的对应关系：现在的「分类」就是角色，「片段」不变，中间多了「动作」这一层。
`click` 分类下的「开心挥手」「害羞捂脸」「抱水豚」变成三个动作，各自带描述，都有 `click` 角色，另外分别有 `happy`、`shy`、`affection` 角色。

**【待定 D1】** 也可以不引入「动作」这层，只在分类文件夹旁边放一个 actions.json 给片段补描述。改动更小，
但「同一动作的多个变体」「一个动作属于多个角色」「用户给某个动作单独调权重 / 绑触发」都表达不了，Agent 也只能按文件名点名。推荐 B。

## 2. 内置角色

| 角色 | 必需 | 用途 / 默认触发 | 没有时 |
|---|---|---|---|
| `idle` | ✅ | 待机；随机链的底 | 不能加载 |
| `drag` | ✅ | 按住拖拽时循环 | 不能加载 |
| `click` | ✅（至少一个动作） | 单击、摸头、对话回复的兜底反应 | 不能加载 |
| `move` | ✅ | 随机走动（带步态） | 不能加载 |
| `turn` | ✅ | 转身；播完朝向翻转 | 不能加载 |
| `sing` | | 检测到在放音乐 | 用 `happy` → `click` |
| `greet` | | 启动打招呼、Agent 打招呼 | `happy` → `click` |
| `sleep` | | 省电模式（分段动作，见 §3.3） | 只放慢 `idle` |
| `think` | | Agent 思考中（循环直到回复到达） | 保持当前片段 |
| `talk` | | 说话 / 朗读时 | 不额外播 |
| `celebrate` | | 番茄钟一轮结束、Agent 庆祝 | `happy` → `click` |
| `drink` / `stretch` | | 喝水 / 久坐提醒 | `click` |
| 情绪：`happy` `shy` `sad` `angry` `surprised` `confused` `affection` | | 主要给 Agent 用 | 按回退链找，最后到 `click` |

- 回退链写在代码里（`RoleFallbacks`），比如 `celebrate → happy → click`、`sad → idle`。
- **【已定 D14：维持五类必需】** `idle`、`drag`、`click`、`move`、`turn` 每个角色至少要有一个启用的动作，和现在 `PetLibrary.Required` 一致，
  所有宠物的基础体验一致。由此带来的约束：
  - 控制台「动作」页里，某个必需角色只剩最后一个启用的动作时，不能禁用它，也不能把它的这个角色去掉（按钮置灰并说明原因）；
  - 用户覆盖层导致必需角色缺失（比如包升级后删了动作）时，忽略这些覆盖项让角色恢复可用，并在控制台提示；
  - 工作台的基础套装里 `move`、`turn` 是必需项，没生成好就不能导出（04 §3 ④、⑧）。
  - 想让宠物不走动，用现有的「不移动」开关（`StayStill`），不是删掉 move 动作。
- 用户自定义的角色名允许，但只有内置角色会被系统事件自动用到；自定义角色可以在触发规则里引用（`role:dance`）。

## 3. 动作定义

### 3.1 字段

```jsonc
{
  "id": "wave",                     // 包内唯一；[a-z0-9-]，1~32 字符。触发规则、Agent、覆盖层都靠它关联
  "name": "开心挥手",                // 显示名
  "description": "举起一只手朝用户挥手，头顶冒出粉色小爱心", // 给 Agent 看的说明，一句话，≤60 字
  "roles": ["click", "greet", "happy"],
  "clips": ["clips/wave.webm", "clips/wave-2.webm"],          // 多个时随机抽一个
  "kind": "oneshot",                // oneshot | loop | phased，见 3.3
  "mirror": true,                   // 朝右时能不能镜像（带文字的片段填 false）；替代 text_clips.json
  "ambient": 40,                    // 随机链权重，0 = 不在空闲时自己播
  "cooldown": 0,                    // 秒；同一动作两次自动触发的最小间隔（用户直接操作不受限）
  "lines": ["嗨～", "你来啦！"],      // 可选：播放时随机冒一句气泡
  "gait": { "stride": 240, "curve": [0, 0, 0.01] }            // 只有 move 角色的动作有；替代 move_strides.json
}
```

`ambient` 的默认值按第一个角色取，和现在的链权重等价：`idle` 30、`turn` 10、`click` 40（分给该角色的所有动作）、`move` 20，其他 0。
「分给所有动作」的意思是：现在 `click` 分类整体 40，里面三段等概率；新模型下默认每个 click 动作的权重是 `40 / 动作数`，
保证老角色包升级后随机行为不变。

### 3.2 播放属性

| 属性 | 含义 |
|---|---|
| `kind: oneshot` | 播一遍就结束（现在所有片段都是这样） |
| `kind: loop` | 循环到被打断或到 `maxSeconds`；`drag` 和 `think` 用 |
| `kind: phased` | 分段：进入 → 循环 → 退出，见下 |
| `interrupt` | `any`（默认）/ `user`（只有用户操作能打断）/ `never`（播完为止，拖拽除外） |

### 3.3 分段动作（第二期）

睡觉、趴下、坐下这类「换一个姿势待一会儿」的动作，用一段视频做不好：首尾都要回到站姿，中间待不住。分成三段：

```jsonc
{
  "id": "nap", "roles": ["sleep"], "kind": "phased",
  "phases": { "enter": "clips/nap-enter.webm", "loop": "clips/nap-loop.webm", "exit": "clips/nap-exit.webm" }
}
```

- `enter` 用 Seedance 首尾帧模式，首帧是站姿、尾帧是睡姿；`loop` 首尾都是睡姿；`exit` 反过来（D7 的方案 C）。
  所以工作台里要多生成一张「睡姿」关键帧（从站姿图 + 提示词改出来）。
- 播放器状态：进入 → 循环直到退出条件（省电结束 / 被点击 / 超时）→ 退出 → 回待机。被拖拽时直接切 `drag`，不播退出段。

### 3.4 运行时对象

```csharp
// Core/Actions/ActionDef.cs —— 不可变，来自包或覆盖层合并后的结果
public sealed record ActionDef(
    string Id, string Name, string Description,
    IReadOnlyList<string> Roles, IReadOnlyList<PetClip> Clips,
    ActionKind Kind, bool Mirror, int Ambient, TimeSpan Cooldown,
    InterruptPolicy Interrupt, IReadOnlyList<string> Lines,
    Gait? Gait, ActionPhases? Phases, ActionOrigin Origin, bool Enabled);

public enum ActionOrigin { Pack, User }   // 用户新增的动作可以删，包里的只能禁用
```

`PetLibrary` 改为持有 `ActionSet`：

```csharp
public sealed class ActionSet
{
    public IReadOnlyList<ActionDef> All { get; }
    public ActionDef? Find(string id);
    public IReadOnlyList<ActionDef> WithRole(string role);          // 只含 Enabled
    public ActionDef? PickByRole(string role, Random rng);           // 走回退链，保证 click 兜底
    public ActionDef? PickAmbient(Random rng, Func<ActionDef, bool> allowed); // 按 ambient 权重
}
```

## 4. 用户自定义

### 4.1 层叠

```
最终动作集 = 角色包里的定义（只读）
          ⊕ 用户覆盖：改名、改描述、改角色、调权重 / 冷却、改台词、禁用、绑触发
          ⊕ 用户新增的动作（导入或在工作台生成的片段）
```

**【待定 D2】**

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **A. 覆盖层** | 包不动；改动和新增片段存在 `<数据目录>/pets/<包 id>/` | 包升级后改动还在；内置角色也能改；可以「恢复默认」 | 包里删掉了某个动作时，覆盖项成了孤儿（控制台标出来，让用户删） |
| B. 复制为可编辑角色 | 先把包解成用户角色文件夹，再随便改 | 实现最简单，所见即所得 | 内置角色升级后拿不到新动作；占双份空间 |
| **C. A + 工作台「以此为基础新建」** | 日常调整走覆盖层；想彻底改造时在工作台里以它为底新建工程 | 两种需求都覆盖 | 两条路径都要做 |

推荐 C。覆盖层按 **包 id**（`pet.json` 的 `id`）关联，不按文件哈希，包升级后改动仍然生效。用户文件夹角色的 id 是文件夹名。

### 4.2 覆盖层存储

```
<数据目录>/pets/<petKey>/
  actions.user.json      覆盖和新增
  clips/<动作 id>/*.webm  用户新增的片段（导入时已处理成 640×360 带 alpha）
```

`petKey`：角色包用 `pet.json` 里的 `id`（包升级、改文件名都不影响）；用户自己放的角色文件夹用 `dir-<文件夹名>`；
开发素材（`ASTRA_PET_VIDEOS`）没有覆盖层，控制台里不能自定义。

```jsonc
{
  "version": 1,
  "overrides": {
    "wave":  { "ambient": 10, "lines": ["哈喽！"], "description": "朝你挥手打招呼" },
    "hug-capy": { "enabled": false }
  },
  "added": [
    { "id": "dance", "name": "跳舞", "description": "原地转圈跳舞", "roles": ["happy", "dance"],
      "clips": ["clips/dance/dance.webm"], "ambient": 5 }
  ],
  "triggers": [ /* 只对这只宠物生效的触发规则，见 02 */ ]
}
```

合并规则：`overrides` 里只允许出现可覆盖的字段（不能改包里动作的 `clips` 和 `gait`），未知字段忽略；
`added` 的 id 和包里重复时，用户的那个改名为 `<id>-user`，并记日志。

### 4.3 新增动作的三种途径

1. **导入现成的透明视频**：选一个 webm（VP9 + alpha）。尺寸不是 640×360 或人物大小不一致时，走 06 的「统一尺寸」步骤。
2. **导入绿幕 / 纯色背景视频**（mp4 / mov）：走 06 的完整处理管线（抠像 → 统一尺寸 → 编码 → 质检），在导入对话框里预览结果。
3. **在工作台里为这只宠物生成**：需要这只宠物的站姿首尾帧。格式 2 的包可以带 `source/first_frame.png`（见 07）；
   没带的（格式 1 的包、第三方包）只能让用户上传一张站姿图，或从 idle 片段第一帧取（640×360，质量不够，界面上提示）。

### 4.4 控制台「动作」页

在控制台「桌宠」下新增「动作」页，列出当前角色的动作：

```
┌ 动作 ─ 小t ───────────────────────────────────────────── [+ 添加动作 ▾] ┐
│ ▶ [缩略图] 待机呼吸   idle            随机 ███████░ 30   ✓ 启用        │
│ ▶ [缩略图] 开心挥手   click happy greet 随机 ███░░░░ 13   ✓ 启用   ⋯   │
│ ▶ [缩略图] 害羞捂脸   click shy         随机 ███░░░░ 13   ✓ 启用   ⋯   │
│ ▶ [缩略图] 跳舞 (我的) happy dance      随机 █░░░░░░  5   ✓ 启用   ⋯   │
├──────────────────────────────────────────────────────────────────────┤
│ 选中：开心挥手                                                         │
│  名字 [开心挥手        ]   角色 [click ×][happy ×][greet ×][+]          │
│  给 Agent 的描述 [举起一只手朝用户挥手，头顶冒出粉色小爱心        ]      │
│  随机权重 ──●──── 13   冷却 [0] 秒   ☑ 朝右时镜像                        │
│  台词（一行一句）[嗨～ / 你来啦！]                                       │
│  触发  · 单击（来自角色 click）   · Agent 可用                            │
│        [+ 绑定触发…]                                    [恢复默认]      │
└──────────────────────────────────────────────────────────────────────┘
```

- ▶ 在宠物窗口上立即播一次（走 Director，优先级 user）。
- 「添加动作 ▾」：导入透明视频 / 导入绿幕视频 / 在工作台中生成。
- 现在「行为」页里的单击台词、摸头台词迁移到动作的 `lines` 和触发规则的 `say` 上，旧设置项保留读取、写入时迁移。

## 5. 格式 1 的兼容

格式 1 的包和用户文件夹没有动作定义，加载时按文件合成：

| 字段 | 取值 |
|---|---|
| `id` | `<分类>-<序号>`（按文件名排序，从 1 开始）；序号稳定，所以覆盖层能关联 |
| `name` | 文件名去掉扩展名（「开心挥手」） |
| `description` | 空。Agent 看到的是名字；控制台提示「补一句描述，Agent 才知道这个动作是做什么的」 |
| `roles` | `[分类]` |
| `mirror` | 不在 `text_clips.json` 的 `no_mirror` 里 |
| `gait` | `move_strides.json` 里同名的条目 |
| `ambient` | 按 §3.1 的默认规则 |
