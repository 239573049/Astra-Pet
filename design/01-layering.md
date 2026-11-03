# 01 分层与项目划分

## 1. 分层

```
┌──────────────────────────────────────────────────────────────────┐
│ AstraPet.App          Avalonia，可执行程序，组合根                    │
│   Composition         唯一 new 具体实现、把各层接起来的地方               │
│   Views               PetWindow · BubbleWindow · ConsoleWindow · Chat │
│   ViewModels          按页拆分；只依赖 Core 的服务和 PlatformCapabilities │
└───────┬──────────────────────┬─────────────────────────┬─────────┘
        │                      │                         │
┌───────▼─────────┐  ┌─────────▼──────────┐  ┌───────────▼────────────┐
│ AstraPet.Infra   │  │ AstraPet.Platform   │  │ （UI 适配，留在 App 内）   │
│ ffmpeg 帧源       │  │ Abstractions 由 Core │  │ AvaloniaUiDispatcher    │
│ LLM HTTP 客户端   │  │ 定义，这里实现：       │  │ AvaloniaNativeWindowHost│
│ JSON 设置存储     │  │  MacOS / Windows /  │  │                         │
│ 导入器 / 更新源    │  │  Linux / Unsupported│  │                         │
└───────┬─────────┘  └─────────┬──────────┘  └───────────┬────────────┘
        │                      │                         │
        └──────────────┬───────┴─────────────────────────┘
               ┌───────▼──────────────────────────────────────────┐
               │ AstraPet.Core       只依赖 BCL                      │
               │   Domain    PetDirector · Gait · PetClip 元数据 ·   │
               │             SemVer · 设置模型 · 记忆 · 提供商预设      │
               │   Services  宠物行为 · Agent · 更新 · 角色目录        │
               │   Ports     IFrameSource · ILlmClient ·             │
               │             ISettingsStore · IPlatform · IUiDispatcher│
               └──────────────────────────────────────────────────┘
```

### 依赖规则

| 项目 | 可以引用 | 禁止 |
|---|---|---|
| `AstraPet.Core` | BCL | Avalonia、任何平台 API、`System.Diagnostics.Process`、`HttpClient` |
| `AstraPet.Infrastructure` | Core、Sqlite、Tomlyn、YamlDotNet | Avalonia、平台 API |
| `AstraPet.Platform` | Core | Avalonia（窗口句柄以 `nint` 传入） |
| `AstraPet.App` | 以上全部、Avalonia、TKCode.Arc | — |
| `AstraPet.Tests` | Core、Infrastructure | Avalonia |

规则由 csproj 的 `ProjectReference` 保证；Core 的 csproj 里不引用 Avalonia 包，写错了编译不过。

> **Platform 为什么不依赖 Avalonia：** 窗口相关的平台调用只需要原生句柄（`NSWindow*`、`HWND`、X11 `Window`）。
> App 里的一个小适配器负责从 Avalonia 的 `TryGetPlatformHandle()` 取出句柄传给 Platform。
> 这样 Platform 可以单独编译、单独测试，也不会被 Avalonia 版本升级牵连。

## 2. 「Port」放在 Core

接口（Port）由使用方 Core 定义，实现方（Infrastructure、Platform、App）去实现。
这样依赖箭头总是指向 Core，Core 的服务可以用假实现做单元测试。

| Port | 作用 | 实现在 |
|---|---|---|
| `IFrameSource` / `IFrameSourceFactory` | 打开一段动画，按帧取 RGBA | Infrastructure（ffmpeg 子进程） |
| `ILlmClient` | 发一次对话请求，拿到文本 | Infrastructure |
| `IModelCatalog` | 查模型参数 | Infrastructure（内嵌 models.dev） |
| `ISettingsStore` | 读写设置文件，含版本迁移 | Infrastructure |
| `IUpdateFeed` | 查服务器上的最新版本 | Infrastructure |
| `IImportSource` | 从其他应用导入提供商 | Infrastructure |
| `IPlatform` 及其子接口 | 一切与操作系统有关的事 | Platform（见 02） |
| `IUiDispatcher` | 回到 UI 线程 | App（Avalonia Dispatcher） |
| `TimeProvider`（BCL） | 定时器、当前时间 | 生产用 `TimeProvider.System`，测试用假时钟 |

## 3. 目标目录结构

```
src/
├─ AstraPet.Core/
│  ├─ Domain/
│  │  ├─ Animation/      PetClip（元数据）· Gait · ClipCategory · PetDirector
│  │  ├─ Settings/       AppSettings 各分节 · PetRuntimeState · MemoryItem · MenuEntry
│  │  ├─ Providers/      LlmProvider · LlmProtocol · ProviderPresets · ProviderText
│  │  └─ Versioning/     SemVer · AppVersion（不含平台判断，平台标识由 IPlatform 给）
│  ├─ Services/
│  │  ├─ Behaviors/      IPetBehavior · Interaction · Reminders · Presence · Greeting · UpdateNotifier
│  │  ├─ PetHost.cs      行为的宿主：持有 Director、Bubble 端口，启动 / 停止所有行为
│  │  ├─ AgentService.cs 现在的 PetAgent
│  │  ├─ UpdateService.cs 检测 + 下载 + 调平台安装器
│  │  └─ CharacterService.cs 角色目录扫描、加载、回退到内置
│  ├─ Ports/             见上表
│  └─ Observable.cs
├─ AstraPet.Infrastructure/
│  ├─ Media/             FfmpegFrameSource · FfmpegProbe · FramePool · FfmpegLocator
│  ├─ Ai/                LlmClient（三种协议）· ModelCatalog
│  ├─ Storage/           JsonSettingsStore · SettingsMigrations · SettingsJsonContext
│  ├─ Import/            CcSwitch · Codex · Dsh · ImportKit
│  └─ Update/            HttpUpdateFeed · UpdateJsonContext · Downloader
├─ AstraPet.Platform/
│  ├─ PlatformFactory.cs
│  ├─ Common/            UnsupportedXxx · ProcessRunner · DesktopEntry 工具
│  ├─ MacOS/             MacPlatform · MacNativeWindow · MacAutoStart · MacSpeech · MacMonitor · MacInstaller · MacKeychain
│  ├─ Windows/           WinPlatform · WinNativeWindow · WinAutoStart · WinSpeech · WinMonitor · WinInstaller · WinDpapi
│  └─ Linux/             LinuxPlatform · X11NativeWindow · XdgAutoStart · SpdSpeech · LinuxMonitor · DebInstaller · LibSecret
├─ AstraPet.App/
│  ├─ Program.cs · App.axaml · Composition.cs
│  ├─ Adapters/          AvaloniaUiDispatcher · NativeWindowAttach · BubblePresenter
│  ├─ Views/Pet/         PetWindow · BubbleWindow
│  ├─ Views/Console/     ConsoleWindow + 各页 UserControl
│  ├─ Views/Chat/        ChatWindow
│  ├─ ViewModels/        Console/{General,Appearance,Behavior,Reminders,Ai,Memory,Speech,Update,About}ViewModel …
│  └─ Assets/
└─ AstraPet.Tests/
```

现有文件到新位置的对照见 [04-migration.md](04-migration.md#文件对照表)。

## 4. 组合根

不引入 DI 容器（`Microsoft.Extensions.DependencyInjection` 在 AOT 下可用，但这个规模手写更直观、零反射风险）。
`App/Composition.cs` 是唯一知道具体类型的地方：

```csharp
sealed class Composition : IDisposable
{
    public Composition(string[] args)
    {
        Platform   = PlatformFactory.Create();                     // 按系统选实现
        Paths      = Platform.Paths;
        Settings   = new JsonSettingsStore(Paths.DataDirectory, Platform.Secrets).Load();
        Frames     = new FfmpegFrameSourceFactory(FfmpegLocator.Find(Paths));
        Characters = new CharacterService(Paths, Frames);
        Director   = new PetDirector(Characters.LoadSelected(Settings), Settings, Runtime);
        Agent      = new AgentService(new LlmClient(), Settings);
        Updates    = new UpdateService(new HttpUpdateFeed(), Platform.Installer, Settings, Platform.Identity);
        // 视图和行为在 App.OnFrameworkInitializationCompleted 里创建，因为它们需要 Dispatcher
    }
}
```

`--smoke` 也走同一个 Composition，只是不显示窗口，这样冒烟测试能覆盖到组装过程本身。

## 5. 为什么不是单项目 + 文件夹

单项目也能分层，但依赖规则只能靠自觉。拆项目带来的好处：

- Core 引用了 Avalonia 或 `Process` 会**编译失败**，不用 code review 去盯；
- 测试项目只引用 Core + Infrastructure，测试启动不需要 UI；
- Platform 的 `[SupportedOSPlatform]` 分析器只在它自己的项目里起作用，噪音小。

代价是多几个 csproj，NativeAOT 发布时它们会被合并成一个可执行文件，对产物没有影响。
