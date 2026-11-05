# 04 空间移动：地面、重力、爬墙

## 1. 目标

- 宠物 **站在地面上**（工作区底边，也就是任务栏 / Dock 上方），松手会掉下去，被甩会飞出去再落地。
- 走到屏幕边上，**会爬墙**：贴在墙上待一会儿、往上爬、松手掉下来、或者爬回地面。
- 以后：倒挂在天花板、站在其他应用窗口的顶上。
- 现在的「随便放在哪都行」保留为一种放置模式。

## 2. 放置模式【待定 L6，推荐 B】

| 模式 | 行为 |
|---|---|
| **自由放置**（0.1.x 的行为） | 没有重力；拖到哪停在哪；走路只改 X；不爬墙、不能甩 |
| **站在地面** | 有重力；松手下落到最近的表面；能甩；走到屏幕边按概率爬墙（「允许爬墙」开关） |

- 推荐 B：新安装默认「站在地面」，老用户升级后保持「自由放置」，控制台「桌宠 → 位置」页里切换，并在升级后的首次启动冒一句「我学会爬墙啦，在控制台里打开试试」。
- 「不移动」（`StayStill`）在两种模式下都有效：不随机走、不爬，但重力照常（被放在半空还是会掉下来）。

## 3. 姿态（stance）【待定 L8，推荐 A：姿态图】

### 3.1 概念

**姿态** 是「宠物现在以什么方式待在哪」。每个动作只能在某个姿态下播；换姿态靠 **过渡动作**。

| 姿态 | 含义 | 窗口怎么对齐 | 谁进入 |
|---|---|---|---|
| `ground` | 站在一个水平表面上（默认，所有现有动作都属于它） | `body.feetY` 贴表面 | 落地、从墙上下来 |
| `wall` | 贴在竖直表面上（屏幕左 / 右边） | `body.wall.contactX` 贴墙 | 过渡动作 `ground→wall`、空中抓墙 |
| `sit` / `lie` | 坐下 / 趴下睡觉（还在地面上，换个姿势） | 同 `ground` | 过渡动作 |
| `ceiling` | 倒挂在顶边（以后） | `body.ceiling.contactY` 贴顶 | 从墙上爬上去 |
| `air` | 下落 / 被甩飞，物理控制位置 | 不对齐，物理积分 | 松手、抓不住墙、走出窗口顶边 |
| `held` | 被拖着 | 跟手 | 拖拽（现在的 `Dragging`） |

`air` 和 `held` 是 **物理姿态**：不由 Director 按随机链进入，只由输入和物理进入；其他是 **动作姿态**，由过渡动作连接成一张图：

```
              wall-enter                    climb-top（以后）
   ground ─────────────────▶ wall ─────────────────────────▶ ceiling
     ▲ │  ◀───────────────── │ │  ◀──────────────────────────
     │ │      wall-exit      │ │
     │ │ sit-enter / exit    │ └─ let-go ──▶ air ──(落地)──▶ ground
     │ ▼                     │                 └─(抓墙)───▶ wall
     sit ── lie-enter ──▶ lie(sleep)
     
   任何姿态 ──(拖拽)──▶ held ──(松手)──▶ air（站在地面模式）/ 原地 ground（自由放置模式）
```

### 3.2 动作字段的增量

```jsonc
{
  "id": "climb", "roles": ["climb"], "kind": "loop",
  "stance": "wall",                         // 只能在这个姿态下播；默认 ground
  "clips": ["clips/climb/a.webm"],
  "gait": { "axis": "y", "stride": 180, "curve": [ … ] },   // 走路的 gait 加一个 axis，默认 x
  "anchor": { "contactX": 232 }             // 覆盖档案里的默认接触点（01 §3）
}
{
  "id": "wall-enter", "roles": ["wall-enter"],
  "transition": { "from": "ground", "to": "wall" },         // 过渡动作：播完姿态变成 to
  "clips": ["clips/wall-enter/a.webm"]
}
```

- 原来的 `kind: phased`（`enter / loop / exit`）变成语法糖：加载时展开成两个过渡动作和一个循环动作，姿态名用动作 id（`nap` → 姿态 `nap`）。
  这样原路线第五期的分段动作直接落在姿态图上，不需要单独的状态机。
- 一个姿态 **可用** 的条件：至少有一个这个姿态下的 `idle` 角色动作，并且有一条从 `ground` 进来、一条回到 `ground`（或经 `air` 回到 `ground`）的路。
  不可用的姿态在加载时整组忽略（记日志、控制台提示缺什么），所以「只做了爬墙片段没做上墙过渡」的包不会把宠物卡在墙上。

### 3.3 Director 怎么用姿态

- `Director.Stance` 是当前姿态。随机链只从 `stance == 当前姿态` 的动作里抽（`PickAmbient` 多一个过滤）。
- 播完过渡动作，`Stance = transition.to`。
- **请求的动作不在当前姿态** 时（比如在墙上被喂食，`eat` 只在 `ground`），Director 在姿态图上找最短路径，依次播过渡动作再播目标；
  路径上的过渡动作继承请求的优先级。找不到路径：用当前姿态的 `idle` 代替，台词照说。
- 用户操作（单击）的反应也按姿态找：每个姿态可以有自己的 `click` 动作（墙上被点 → 吓一跳抓紧），没有就走回退链；
  回退链跨姿态时 **不自动切姿态**（不然点一下就从墙上下来很突兀），直接用当前姿态的 `idle` + 台词。
- 「用户触发的动作播完回 idle」改为「回当前姿态的 idle」。

## 4. 重力、抛掷、落地

### 4.1 表面

`SurfaceMap`（Core，纯几何）由窗口层在启动、屏幕变化（`Screens.Changed`）、DPI 变化时用工作区重建：

- 每个屏幕的工作区给出四条边：底边 = 地面，左右 = 墙，顶边 = 天花板。
- **多屏**：两块屏幕相邻的那条边不是墙。如果两边地面高度差 ≤ 4 像素，走路可以直接走过去；否则那条边当墙（可以爬，爬到另一块屏的地面高度时能翻过去——以后）。
  P2 先做简单版：相邻边都当「开口」，走过去时如果地面不一样高就掉下去 / 走不过去（挡住）。
- 以后加上窗口顶边（§7）。

### 4.2 物理（`PetBody`）

`air` 姿态时，`PetBody` 用自己的 60Hz 定时器积分，不跟片段帧率走：

```
v.y += g × dt                       g = 2600 DIP/s²（按缩放换算成桌面像素）
v   *= (1 − 0.6 × dt)               空气阻力，让甩的距离可控
p   += v × dt
撞左右墙：  允许爬墙且有 wall 姿态 → 按概率抓住（bold 越高越容易）→ 进 wall，发 pet.grabbedWall
           否则 v.x = −v.x × bounce
撞天花板：  v.y = −v.y × bounce
落到地面：  |v.y| < 900 → impact = soft；否则 hard
           进 ground，发 pet.landed {impact}；落地后水平速度直接归零（不滑行）
```

- 松手时的初速度来自拖拽采样（03 §4.2），除以 `sqrt(weight)`；上限 6000 DIP/s，防止一甩飞出屏幕外。
- 空中播 `fall` 角色（loop，回退 `drag`），落地播 `land`（回退 `idle`）；`hard` 且 `fragile` 或 `bold` 低时播 `dizzy`（回退 `surprised` → `click`）。
- 位置始终夹在所有工作区的并集内（找不到就回主屏地面中央），防止掉到屏幕外。
- 在半空中被再次抓住：直接进 `held`。

### 4.3 「放下」的手感

拖拽松手时离地面很近（< 20 DIP）直接吸附落地，不播下落；在地面上拖动时（只横向拖）也不进 `air`。避免每次挪一下位置都「掉」一次。

## 5. 爬墙【待定 L7，推荐 A】

### 5.1 素材方案

| 方案 | 做法 | 评价 |
|---|---|---|
| **A. 专门的片段** | 工作台生成「贴墙」关键帧（侧身、手脚抓着画面左侧的竖直墙面），再以它为首尾帧生成：`wall-idle`（贴墙待机）、`climb`（原地攀爬循环）、`wall-enter`（站姿 → 贴墙，首尾帧不同）、`wall-exit`（贴墙 → 站姿）、`let-go`（可选，松手）| 效果好；要多生成 3～5 段，约多 30 秒视频 |
| B. 旋转走路片段 | 把 `move` 旋转 90°，窗口换成竖的 360×640 | 零成本；人形角色像壁虎在墙上「走」，脸朝外、身体横着，观感差；窗口要能改尺寸 |
| C. A 为主，没有用 B | | 两套都要做；B 的观感问题不会消失 |

推荐 A；没有贴墙片段的宠物 **不爬墙**（走到边上照旧转身往回走），和现在一样。B 可以作为四足 / 软体类角色的一个 `species` 级选项以后再看。

### 5.2 对齐

所有片段约定角色 **朝左**。贴墙片段里，角色侧身贴着画面左侧的一道「看不见的墙」，`contactX` 是手脚接触墙的那条竖线（工作台自动测，05 §4）。

```
左墙：窗口 X = 墙.x − contactX × 缩放          （不镜像）
右墙：窗口 X = 墙.x − (画布宽 − contactX) × 缩放（镜像，Direction = +1）
```

- 在墙上时 Direction 由墙决定（左墙朝左、右墙朝右），`turn` 不可用（`wall` 姿态下不抽 `turn`）。
- **过渡动作的对齐**：`wall-enter` 首帧按地面对齐（`feetY` 贴地），尾帧按墙对齐（`contactX` 贴墙）；中间每帧窗口位置在两个对齐点之间按 `gait` 曲线（默认线性）插值。
  这和走路「窗口位置由帧号决定」是同一个思路（`Walk.XAt`），只是变成二维。片段里角色要「原地」完成这个动作，位移靠窗口——和走路一致，工作台模板会写清楚。
- 走到墙边时，`WalkPlanner` 让这一圈停在「正好能接上 `wall-enter` 起点」的位置（墙.x 往里留出 `wall-enter` 首帧和尾帧的水平差）。

### 5.3 什么时候上墙、在墙上做什么

```
地面走路这一圈结束时贴着屏幕边（离墙 < 半个身宽）→ PetBody 发 surface.wallReached {side}
  → 规则 climb-up：do = 进入 wall 姿态（do: "stance:wall"），chance = 0.15 × curious 倍率 × energetic 倍率，冷却 5 分钟，
    when: placement = ground、AllowClimb、精力 ≥ 30、亲密度不限
在墙上（随机链只抽 wall 姿态的动作）：
  wall-idle      权重 40
  climb（向上）   权重 30 × energetic 倍率；gait.axis = y，窗口 Y 往上；到顶（离天花板 < 半个身宽）就不再抽 climb
  向下           没有专门的下爬片段 → 播 wall-idle 并让窗口慢速下滑（「滑下来」）；有 climb-down 角色就用它
  let-go         权重 10；进 air 掉下来
  wall-exit      只有离地面 < 1 个身高时可抽：直接下到地面
在墙上待的总时长到 3 分钟（× clingy 倍率无关）或精力 < 20 → 优先下来（wall-exit 或 let-go）
```

- `do: "stance:wall"` 是规则的新写法：意思是「走到这个姿态」，Director 用姿态图找过渡。
- 墙上被单击：播墙上的 `click`（没有就 `wall-idle` + 台词「哇，别吓我」）；被拖：照常进 `held`。

## 6. 走路规划的扩展

`WalkPlanner.Plan` 现在只知道「左右范围」。改成接收一个 `WalkGoal`：

| 目标 | 用在哪 |
|---|---|
| `Random`（现有） | 随机走 |
| `Toward(x)` | 主动靠近光标（03 §4.4）、逗它玩（03 §4.5） |
| `ToEdge(side)` | 好奇的宠物去屏幕边（为爬墙做准备，概率按 `curious`） |
| `Along(axis = y, …)` | 墙上的 climb |

纯计算，继续放在 Core，和现在的测试一起扩展。

## 7. 站在其他窗口顶上【待定 L9，推荐 B，最后一期】

- 平台层加 `IWindowSurfaces`（能力 `WindowSurfaces`）：每 500 毫秒（只在 `ground` 且在走路 / 空中时）枚举可见的普通窗口矩形和 Z 序，**只要矩形，不要标题和进程名**。
  - macOS：`CGWindowListCopyWindowInfo`（只读 `kCGWindowBounds` / `kCGWindowLayer`，不需要屏幕录制权限）
  - Windows：`EnumWindows` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`，跳过隐藏 / cloaked / 工具窗口
  - Linux X11：`_NET_CLIENT_LIST_STACKING` + `XGetGeometry`；Wayland 拿不到，能力为 false
- 窗口顶边中没被更上层窗口挡住的那几段，作为额外的「地面」加进 `SurfaceMap`；走到段的尽头 → 掉下去（`air`）。
- 站着的窗口移动了：跟着移；窗口关了 / 最小化 / 被挡住：掉下去。
- 默认关（控制台开关「可以站在窗口上」），因为：要额外的平台代码；全屏 / 最大化窗口的顶边就是屏幕顶，宠物爬到「天花板」会很怪，需要过滤；用户可能不想宠物站在正在用的窗口标题栏上挡按钮。

## 8. 平台层的增量

| 需要 | 接口 | 说明 |
|---|---|---|
| 全局光标位置 | ~~`ICursor.Position`~~ 实现时改用现有的 `INativeWindow.CursorInWindow()`（窗口外也读得到） | 视线跟随、靠近光标、逗玩要知道光标在哪 |
| 窗口枚举 | `IWindowSurfaces`（能力 `WindowSurfaces`），已实现三个平台 | §7 |
| 全局快捷键 | `IGlobalHotkey`（能力 `GlobalHotkey`），已实现三个平台 | 召唤（03 §4.6） |
| 屏幕变化 | Avalonia `Screens.Changed`，已有 | 重建 `SurfaceMap`；宠物在消失的屏幕上 → 回主屏地面 |

Linux 补充：原生 Wayland 下窗口不能自己设位置（走路、下落都做不到），现在靠 XWayland 运行；这一点不变，`SurfaceMap` 不需要特殊处理。

## 9. `PetBody` 和其他部分怎么配合

```
               ┌──────────── PetDirector ────────────┐
 事件 / 规则 ──▶│ 选动作（按 Stance 过滤、姿态图寻路）   │──ActionStarted──┐
               │ Stance（播完过渡动作时更新）           │                 │
               └─────────────────────────────────────┘                 ▼
                                                        ┌──────── PetBody（Core）────────┐
 PetWindow：拖拽采样、松手速度 ─────────────────────────▶│ 位置、速度、当前表面             │
 PetWindow：每帧 Tick(帧号) ◀── 窗口位置 ───────────────│ 走路 / 攀爬 / 过渡的位置插值      │
 SurfaceMap（屏幕 / 窗口几何）─────────────────────────▶│ air 时的物理积分（自己的定时器）  │
                                                        │ 发事件：wallReached、landed、    │
                                                        │        grabbedWall、ledgeEnd     │
                                                        └─────────────────────────────────┘
```

- `PetWindow` 退化成「把 `PetBody.WindowPosition` 设到窗口上」，走路的 `_walk` 和 `PlanWalk` 搬进 `PetBody`。
- `PetBody` 用桌面像素，缩放（`Scale × DesktopScaling`）由窗口传进来；不认识 Avalonia。
- 进入 `air`、`held` 时 `PetBody` 通知 Director（`Director.EnterPhysical(stance)`），Director 播 `fall` / `drag`；落地后 Director 回到 `ground` 的随机链。

## 10. 测试

- 姿态图：只做了 `wall-idle` 没做 `wall-enter` 的包，`wall` 姿态不可用；`phased` 展开后路径正确。
- 寻路：在 `wall` 上请求 `eat`（只在 `ground`）→ 依次播 `wall-exit`、`eat`；没有 `wall-exit` 时走 `let-go` → `air` → 落地 → `eat`。
- 物理：固定初速度的轨迹、撞墙反弹、落地冲击分档、速度上限、不出屏（假屏幕几何）。
- 对齐：左墙 / 右墙的窗口 X 计算（含镜像和缩放 1.5、DPI 2.0）；过渡动作的二维插值首尾两帧对齐。
- 多屏：主屏右边接一块地面高 40 像素的副屏，走到交界处的行为。
- 放置模式：自由放置下松手不下落、不发 `surface.*` 事件；升级迁移后老用户是自由放置。
