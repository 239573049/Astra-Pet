# 03 核心层（AstraPet.Core）

Core 只依赖 BCL，不引用 Avalonia，也不直接起进程、发 HTTP、读平台 API。
本章说明哪些东西放进 Core、它们之间怎么交互，以及要做的几处结构调整。

## 1. 动画：Director 与帧源

### 1.1 现状

`PetDirector` 已经与窗口无关，但它通过 `PetLibrary.Open()` 直接 `new ClipStream(clip, ffmpeg)`。
`ClipStream` 会起 ffmpeg 子进程，所以 Director 没法脱离 ffmpeg 测试，也换不了解码方式。

### 1.2 目标

```csharp
// Core/Ports
public interface IFrameSource : IDisposable
{
    ClipInfo Clip { get; }
    byte[]? TryTake();          // 还没解出来或已播完时返回 null，用 Completed 区分
    bool Completed { get; }
}

public interface IFrameSourceFactory
{
    IFrameSource Open(ClipInfo clip);
    ClipInfo Probe(string path, string category);   // 启动时探测元数据
    void Recycle(byte[] frame);                      // 帧缓冲池
}
```

- `PetClip` 改名为 `ClipInfo`，只保留数据（Name、Category、Path、Fps、EstimatedFrames）。`Probe` 的逻辑移到 Infrastructure。
- `PetLibrary` 只管素材元数据：分类、步态、能不能镜像。它不再持有 ffmpeg 路径，`Open` 改由 `IFrameSourceFactory` 负责。
- `PetDirector(PetLibrary, IFrameSourceFactory, PetBehaviorConfig, PetRuntimeState, Random)`：
  `Random` 从外面注入，测试时用固定种子，就能断言「播完 turn 之后朝向翻转」「用户触发的片段播完回到 idle」。
- Infrastructure 里的 `FfmpegFrameSource` 就是现在的 `ClipStream`，逻辑原样搬过去，包括有界队列、背压、缓冲池和不抛异常的停止路径。

这样以后可以换解码方式，Director 不用改：

| 实现 | 场景 |
|---|---|
| `FfmpegFrameSource` | 现在的方案，三个平台通用 |
| `InProcessVpxFrameSource`（可选） | 用 P/Invoke 直接调 libvpx 和 libwebm，不再起子进程。能省掉每段动画启动 ffmpeg 的开销，也省掉随包分发 ffmpeg |
| `MemoryFrameSource` | 单元测试用：按给定帧数产出纯色帧 |

### 1.3 走路规划

现在 `PetWindow.PlanWalk` / `StepWalk` 把屏幕几何和走路规则写在一起。拆成两部分：

- **Core 的 `WalkPlanner`**：给它当前 X、可走范围、步幅、随机数，返回 `Walk(StartX, Direction, Distance)`，
  再按帧号算出当前位置。整个过程只有纯计算，可以单元测试，覆盖「两边都够」「只有一边够」「都不够」三种情况。
- **App 的 `PetWindow`**：只负责从 Avalonia 读工作区和 `DesktopScaling`，算出可走范围，然后把 `Position` 设成规划器给的值。

## 2. 宠物行为：拆分 PetCompanion

### 2.1 现状

`PetCompanion`（281 行）一个类做了七件事：点击和摸头的回应、右键菜单、番茄钟、喝水 / 久坐 / 整点报时、
省电和听歌检测、开机打招呼、更新提醒。它直接用 `DispatcherTimer`、`Dispatcher.UIThread`、静态的 `Speech` 和 `SystemMonitor`。

### 2.2 目标结构

```csharp
public interface IPetBehavior : IDisposable
{
    void Start(PetContext context);
}

/// <summary>行为能用到的一切，都由宿主注入。</summary>
public sealed class PetContext
{
    public required AppSettings Settings { get; init; }
    public required PetRuntimeState Runtime { get; init; }
    public required PetDirector Director { get; init; }
    public required IPetVoice Voice { get; init; }        // 说话：气泡 + 按设置朗读
    public required IPetCommands Commands { get; init; }  // 打开对话、打开控制台、回到中央…（由 App 实现）
    public required IPlatform Platform { get; init; }
    public required TimeProvider Time { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public required Random Random { get; init; }
}
```

| 行为 | 负责 | 从 PetCompanion 迁出的部分 |
|---|---|---|
| `InteractionBehavior` | 单击台词、摸头、双击打开对话 | `OnClicked`、`OnPetted`、双击 |
| `MenuBehavior` | 右键菜单的文字和命令 | `MenuLabelOf`、`OnMenuCommand` |
| `ReminderBehavior` | 整点报时、喝水、久坐 | `Clock()` 前半段 |
| `PomodoroBehavior` | 番茄钟状态机 | `StartPomodoro` / `StopPomodoro` 和 `Clock()` 后半段 |
| `PresenceBehavior` | 省电、听歌、全屏隐藏（Windows / Linux） | `Monitor()` |
| `GreetingBehavior` | 启动问候 | `Greet()` |
| `UpdateNotifyBehavior` | 定时检查更新、同一个版本只提醒一次 | `AutoCheckUpdate` / `CheckForUpdateAsync` |

`PetHost` 负责创建、启动、停止这些行为，相当于现在 `PetCompanion` 的构造函数和 `Dispose`。

**行为之间不直接互相引用，通过 `PetRuntimeState` 交流。** 比如久坐提醒要知道「用户离开过电脑」，
现在是 `Monitor()` 直接改 `_sittingSince`。改成 Presence 把 `Runtime.IdleTime` 写进运行时状态，
Reminder 读它来判断要不要重置久坐计时。

### 2.3 时间和线程

- **定时器**：用 `TimeProvider.CreateTimer`，不再用 `DispatcherTimer`。生产环境传 `TimeProvider.System`；
  测试时用 `Microsoft.Extensions.TimeProvider.Testing` 的 `FakeTimeProvider` 把时间往前拨，
  就能测「喝水间隔 60 分钟，拨 59 分钟不提醒，拨 61 分钟提醒一次」。
- **线程**：`TimeProvider` 的回调跑在线程池上，所以行为在回调里先 `Ui.Post(...)` 切回 UI 线程，再碰设置和 Director。
  约定和现在一样：**设置、运行时状态、Director 只在 UI 线程读写。**
- `IUiDispatcher` 在 App 里用 `Dispatcher.UIThread` 实现，测试时用同步执行的假实现。

```csharp
public interface IUiDispatcher
{
    void Post(Action action);
    bool CheckAccess();
}
```

### 2.4 对话

现在的 `PetAgent` 改名为 `AgentService`，依赖从 `PetSettings` 改成 `AiSettings` 分节和 `ILlmClient`。
`LlmClient` 现在是静态类，里面有一个静态的 `HttpClient`；改成实例类实现 `ILlmClient`，放在 Infrastructure。
这样就能用假的 `ILlmClient` 测试「上下文只带最近 N 轮」「历史记录会截断」「自动记忆的去重和上限」。
三种协议的 JSON 拼装和解析在 Infrastructure 里另外测，测试数据是录下来的真实响应。

## 3. 设置模型

### 3.1 拆分

| 新类型 | 内容 | 存盘 |
|---|---|---|
| `AppSettings` | 根对象：`SchemaVersion` + 下面的分节 | ✅ |
| ├ `GeneralSettings` | AutoStart、Topmost、HideInFullscreen、AllowCapture、ClickThrough | ✅ |
| ├ `AppearanceSettings` | PetId、Scale、Opacity、BubbleFontSize、Speed | ✅ |
| ├ `BehaviorSettings` | ActionInterval、PowerSaving(+Minutes)、StayStill、MusicDetect、点击 / 拖拽 / 摸头开关、ClickLines、MenuItems | ✅ |
| ├ `ReminderSettings` | HourlyChime、Drink、Sit、Pomodoro、DailyGreeting | ✅ |
| ├ `AiSettings` | Providers、ActiveProviderId、Temperature、MaxTokens、ContextTurns、Timeout、SystemPrompt、Memory* | ✅（ApiKey 另存，见 3.3） |
| ├ `SpeechSettings` | SpeakReplies、SpeakReminders、Voice、Rate、Volume | ✅ |
| └ `UpdateSettings` | AutoCheck、Channel、Server、NotifiedVersion | ✅ |
| `PetRuntimeState` | Paused、Visible、MusicPlaying、PowerSavingActive、IdleTime、全屏状态 | ❌ |

派生出来的值放到用到它们的地方，不留在设置对象上：

- `Chain`（随机链权重）放进 `PetDirector`，由 `BehaviorSettings` 和 `PetRuntimeState` 算出来；
- `EffectiveSpeed` 放到 `PetRuntimeState` 的扩展方法里；
- `ActiveProvider` 放到 `AiSettings`。

每个分节都继承 `Observable`。根对象订阅各分节的变化，统一触发 `Changed` 事件，这样存盘的防抖逻辑不变（0.5 秒）。
ViewModel 只拿自己那一节，比如「语音」页只依赖 `SpeechSettings` 和 `ISpeech`。

### 3.2 文件格式与迁移

新的格式：

```json
{
  "schemaVersion": 2,
  "general":    { "autoStart": false, "topmost": true, … },
  "appearance": { "petId": "", "scale": 0.5, … },
  …
}
```

迁移由 Infrastructure 的 `SettingsMigrations` 负责：

1. 读成 `JsonNode`，看 `schemaVersion`，没有这个字段就是 1，也就是现在的扁平格式。
2. 按版本依次执行 `V1ToV2(JsonNode)`、`V2ToV3`……。每一步只搬字段，**都是纯函数**，测试数据用真实的 v1 文件。
3. 迁移前把原文件备份成 `settings.v1.bak.json`，只备份一次，已经有了就不覆盖。
4. 迁移完再按强类型反序列化（源生成的 `SettingsJsonContext`，AOT 可用）。

降级不考虑：新版本写出的文件，旧版本读不了时会用默认值启动，并保留备份。这和现在「文件坏了就备份、用默认值启动」的行为一样。

### 3.3 API Key

- 设置文件里的 `LlmProvider.ApiKey` 换成 `ApiKeyRef`（形如 `secret:provider/<id>`），密钥本体通过 `ISecretStore` 存到系统的密钥存储里。
- `ISecretStore.IsSecure == false` 的平台（比如没有 keyring 的 Linux）退回现在的做法，明文写在设置里，「智能」页给出提示。
- 迁移 v1 到 v2 时，把明文密钥写进 `ISecretStore`，成功了才从文件里删掉。
- 导出和备份设置时不带密钥。

## 4. 其他 Core 成员

| 现在 | 去向 | 说明 |
|---|---|---|
| `Observable` | Core | 不变 |
| `SemVer` | Core/Domain/Versioning | 不变，补测试 |
| `AppVersion` | Core/Domain/Versioning | 去掉平台判断，平台标识改由 `IPlatform.Identity` 提供 |
| `PetLog` | Core（接口）+ Infrastructure（文件输出） | 保留「最近 500 条在内存里，给控制台看」；另外按天滚动写到 `IAppPaths.LogDirectory`，保留 7 天。Windows 的 WinExe 没有控制台，现在日志实际上看不到 |
| `ProviderPresets`、`ProviderText` | Core/Domain/Providers | 不变 |
| `ModelCatalog` | Infrastructure/Ai | 要读内嵌资源和联网刷新，不是纯领域逻辑 |
| `PetCatalog` | Core/Services/CharacterService | 扫目录用 `IAppPaths`；加载失败回退到内置角色的逻辑不变 |
| `UpdateChecker` | Core/Services/UpdateService + Infrastructure/Update/HttpUpdateFeed | 判断逻辑（比较版本、选渠道、同一版本只提醒一次）留在 Core，HTTP 和 JSON 解析放 Infrastructure |
| `UpdateInstaller` | Platform（各平台的 `IUpdateInstaller`） | 下载部分放 Infrastructure 的 `Downloader` |
| `Import/*` | Infrastructure/Import | 不变；`ImportCandidate` 里给界面用的属性（KeyMask、Host 这类）挪到 ViewModel |
