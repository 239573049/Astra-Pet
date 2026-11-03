# 04 迁移计划

原则：**每一步都是一个可以单独合并、单独发版的 PR**。每步结束时都要满足：

- `dotnet build` 零警告（包括裁剪和 AOT 分析器）
- 三个平台的 NativeAOT 发布都能通过 `--smoke`
- 手动检查：宠物能动，能点、能拖，控制台能打开，对话能用

## 第 0 步：准备（已完成）

- [x] 提交未提交的改动（Arc 界面库、应用内更新），提交号 `8f692da`
- [x] 写出本设计文档

顺手要清理的：`THIRD_PARTY_NOTICES.md`、`tools/third_party_head.md`、`tools/gen_third_party.py`
里还提到 AtomUI，`licenses/LGPL-3.0-AtomUI.txt` 也已经不需要了。这个可以单独提一个小 PR。

## 第 1 步：平台抽象落地（不拆项目）✅

> 已完成，在 `refactor/platform-abstraction` 分支上。验收结果：Platform 目录之外没有 `OperatingSystem.Is`；
> `DllImport` 全部换成 `LibraryImport`；macOS 上 Debug 和 NativeAOT 版本的 `--smoke` 都通过，实际运行时窗口透明、
> 点击穿透、控制台预热都正常。Windows 和 Linux 只验证了能编译，还没有在真机上跑过。
>
> 和计划相比多做的：界面上全屏隐藏、录屏、点击穿透三个开关按平台能力置灰（之前在 Windows 上能打开但不起作用）；
> Windows 空闲时长的 32 位回绕修正；Linux 上不是通过 deb 安装时，更新改为打开下载页。

**目标**：界面层和 Core 里不再出现 `OperatingSystem.IsXxx()`，行为和现在保持一致。

1. 在现有项目里新建 `src/Platform/Abstractions/`，按 [02-platform.md](02-platform.md#2-接口) 定义接口和 `PlatformCapabilities`。
2. 把现有代码平移进实现类：
   - `MacWindow` → `MacOS/MacNativeWindow`，`WinWindow` → `Windows/WinNativeWindow`
   - `AutoStart`、`Speech`、`SystemMonitor` → 按平台拆开
   - `UpdateInstaller` 的三个平台分支 → 三个 `IUpdateInstaller`
   - `AppVersion.Platform` → `PlatformIdentity`
   - `SettingsStore.Directory`、`Program.ResolveVideosDir`、`ResolveFfmpeg`、`OpenLicensesFolder` 里的路径判断 → `IAppPaths`
   - 三个 ViewModel 里的 `Process.Start(url)` 和 `OpenFolder` → `IShell`
3. 加上 `Unsupported*` 空实现和 `PlatformFactory`。
4. `PetWindow` 的点击穿透改为依赖 `INativeWindow`：算法不变，平台判断去掉。
5. `ConsoleViewModel` 的 `PlatformNote`、`MusicNote`、`AutoStartSupported`、`SpeechSupported` 改为读 `Capabilities`。
6. `DllImport` 改成 `LibraryImport`。

**验收**：

- `grep -rn "OperatingSystem.Is" src/ | grep -v src/Platform/` 没有结果。
- 各平台的行为和第 0 步一致。

## 第 2 步：补齐跨平台能力

**目标**：[02-platform.md](02-platform.md#3-各平台实现方案) 表里的「新增」项全部完成。

按价值排序，可以拆成几个 PR：

| PR | 内容 | 平台 | 状态 |
|---|---|---|---|
| 2a | 逐像素点击穿透（`WS_EX_TRANSPARENT`） | Windows | ✅ 已实现，**待 Windows 真机验证**（见下） |
| 2b | 文件日志（`LogDirectory`，按天滚动） | 全部 | ✅ macOS 已验证 |
| 2c | 全屏检测后隐藏（`SHQueryUserNotificationState` / `_NET_WM_STATE_FULLSCREEN`） | Windows、Linux |
| 2d | 声音检测：WASAPI 峰值、`pactl`；macOS 改用 CoreAudio | 全部 |
| 2e | Linux：XDG 自启、`spd-say` / `espeak-ng`、空闲检测、XShape 点击穿透 | Linux |
| 2f | 去掉 `BuiltInComInteropSupport`，COM 改为 `GeneratedComInterface` | Windows |

**验收**：每个平台跑一遍手动测试清单（见下文），界面上灰掉的选项都给出原因。

**2a 在 Windows 上要重点看的：**

1. 宠物、气泡、控制台三个窗口加了 `WS_EX_LAYERED` 之后还能正常显示（不黑、不消失、透明区域仍然透明）。
   如果显示不正常，说明 Avalonia 的合成方式和分层窗口冲突，要改成只切 `WS_EX_TRANSPARENT`，或者走 `WM_NCHITTEST`。
2. 透明区域能点到下面的窗口，角色身上能点、能拖，拖的过程中不会断。
3. 两台不同 DPI 的显示器之间拖动后，命中位置仍然准确（`GetDpiForWindow` 换算）。
4. 控制台预热期间（启动后约 2 秒）不挡鼠标，预热完打开控制台能正常操作。

## 第 3 步：Core 去掉对 Avalonia 的依赖

**目标**：Core 目录下的代码不引用 `Avalonia.*`，也不引用 `System.Diagnostics.Process`。

1. 引入 `IFrameSource` / `IFrameSourceFactory`，`ClipStream` 改名为 `FfmpegFrameSource`（[03-core.md §1](03-core.md#1-动画director-与帧源)）。
2. 走路规划提取成 `WalkPlanner`。
3. 拆 `PetCompanion`：建 `PetHost` 和七个 `IPetBehavior`，接入 `TimeProvider` 和 `IUiDispatcher`（[03-core.md §2](03-core.md#2-宠物行为拆分-petcompanion)）。
4. `LlmClient` 改为实例类并实现 `ILlmClient`；`PetAgent` 改名为 `AgentService`。
5. `UpdateChecker` 拆成 `UpdateService`（Core）和 `HttpUpdateFeed`（Infrastructure）。
6. 新增 `App/Composition.cs`，`App.axaml.cs` 只负责调用它。

**验收**：`grep -rn "using Avalonia" src/Core` 没有结果；`--smoke` 走的是 Composition。

## 第 4 步：拆成多个项目并接入测试

**目标**：用项目引用把依赖规则固定下来，CI 跑单元测试。

1. 按 [01-layering.md §3](01-layering.md#3-目标目录结构) 建 `AstraPet.Core`、`AstraPet.Infrastructure`、`AstraPet.Platform`、`AstraPet.App`、`AstraPet.Tests`，再加一个 `AstraPet.sln`（或 `.slnx`）。
2. 把公共的 MSBuild 属性（TargetFramework、Nullable、AOT 分析器、Version）提到 `Directory.Build.props`。
3. `GenerateBuildInfo` 目标挪到 Core，因为 `AppVersion` 在 Core 里。
4. 更新 `release.yml`、`ci.yml`、`tools/package/*`、`installer/windows/AstraPet.iss` 里的项目路径。
5. 写第一批测试：
   - `PetDirector`：转向后翻转朝向、用户触发的片段播完回 idle、动作间隔、拖拽时循环、唱歌
   - `WalkPlanner`：三种空间情况、帧号和位置的对应
   - `ReminderBehavior` / `PomodoroBehavior`：用 `FakeTimeProvider`
   - `PresenceBehavior`：`IdleTime() == null` 时不进入省电
   - `LlmClient`：三种协议的请求拼装，以及正常、错误、空响应的解析
   - `SemVer`、`Import/*`（用样例配置文件）

**CI 改动**（`ci.yml`）：

```yaml
strategy:
  matrix:
    os: [macos-15, windows-latest, ubuntu-24.04]
steps:
  - dotnet test tests/AstraPet.Tests
  - dotnet build  src/AstraPet.App -c Release -r <rid> -p:PublishAot=true
  - dotnet publish src/AstraPet.App -c Release -r <rid> -p:PublishAot=true --no-build -o publish
  - ./publish/AstraPet --smoke
  - ./publish/AstraPet --platform-report   # 打印探测到的 Capabilities，结果留在日志里
```

**验收**：三个平台的 CI 都是绿的；Core 的 csproj 不引用任何 Avalonia 包。

## 第 5 步：拆分设置、迁移格式、改存密钥

1. 按 [03-core.md §3](03-core.md#3-设置模型) 拆成各分节，加 `PetRuntimeState`。
2. `SettingsMigrations.V1ToV2`，并用真实的 v1 文件写测试。
3. `ISecretStore` 的三个平台实现，迁移时把明文密钥搬进系统密钥存储。
4. `ConsoleViewModel`（455 行）和 `ProvidersViewModel`（790 行）按页面拆开，每页只拿自己那一节设置。

**验收**：用 0.1.x 版本的设置文件启动，所有配置都还在；macOS 和 Windows 的 settings.json 里不再出现明文密钥。

## 文件对照表

| 现在 | 第 1–3 步后（同一个项目） | 第 4 步后（所在项目） |
|---|---|---|
| `Program.cs` | 不变，路径解析改用 `IAppPaths` | App |
| `App.axaml(.cs)` | 委托给 `Composition` | App |
| `Core/PetDirector.cs` | 依赖 `IFrameSourceFactory` | Core/Domain/Animation |
| `Core/PetClip.cs` | `ClipInfo`（只有数据）+ `FfmpegProbe` | Core + Infrastructure/Media |
| `Core/ClipStream.cs` | `FfmpegFrameSource` | Infrastructure/Media |
| `Core/PetLibrary.cs` | 不再持有 ffmpeg 路径 | Core/Domain/Animation |
| `Core/PetCatalog.cs` | `CharacterService` | Core/Services |
| `Core/PetSettings.cs` | 第 5 步拆成 `AppSettings` 的各分节和 `PetRuntimeState` | Core/Domain/Settings |
| `Core/SettingsStore.cs` | `JsonSettingsStore` + `SettingsMigrations` | Infrastructure/Storage |
| `Core/UpdateChecker.cs` | `UpdateService` + `HttpUpdateFeed` | Core/Services + Infrastructure/Update |
| `Core/UpdateInstaller.cs` | 三个平台的 `IUpdateInstaller` + `Downloader` | Platform + Infrastructure |
| `Core/AppVersion.cs` | 去掉平台判断 | Core/Domain/Versioning |
| `Core/ModelCatalog.cs` | 不变 | Infrastructure/Ai |
| `Core/PetLog.cs` | 加文件输出 | Core + Infrastructure |
| `Core/{Observable,SemVer,ProviderPresets,ProviderText}.cs` | 不变 | Core |
| `Ai/LlmClient.cs` | 实例类，实现 `ILlmClient` | Infrastructure/Ai |
| `Ai/PetAgent.cs` | `AgentService` | Core/Services |
| `Import/*` | 不变 | Infrastructure/Import |
| `Pet/PetWindow.cs` | 用 `INativeWindow` 和 `WalkPlanner` | App/Views/Pet |
| `Pet/BubbleWindow.cs` | 用 `INativeWindow`，实现 `IPetVoice` 里显示气泡的部分 | App/Views/Pet |
| `Pet/PetCompanion.cs` | `PetHost` + 七个 `IPetBehavior` | Core/Services/Behaviors |
| `Platform/MacWindow.cs` | `MacOS/MacNativeWindow` | Platform |
| `Platform/WinWindow.cs` | `Windows/WinNativeWindow` | Platform |
| `Platform/{AutoStart,Speech,SystemMonitor}.cs` | 按平台拆开 | Platform |
| `ControlPanel/*` | 第 5 步按页拆 ViewModel | App/Views + App/ViewModels |

## 手动测试清单（每个平台都要过）

| # | 项目 | macOS | Windows | Linux X11 | Linux Wayland |
|---|---|---|---|---|---|
| 1 | 宠物显示、透明、置顶 | | | | |
| 2 | 单击、双击、拖拽、摸头 | | | | |
| 3 | 透明区域点击穿透到下面的窗口 | | | | 预期不支持 |
| 4 | 走路不超出屏幕，多屏和高 DPI 下正常 | | | | |
| 5 | 全屏应用上隐藏 | | | | |
| 6 | 录屏时隐藏 | | | 预期不支持 | 预期不支持 |
| 7 | 开机自启开关，重新登录后生效 | | | | |
| 8 | 朗读、声音列表、试听 | | | | |
| 9 | 离开电脑后进入省电模式 | | | | |
| 10 | 放音乐时唱歌 | | | | |
| 11 | 对话、自动记忆 | | | | |
| 12 | 检查更新、应用内安装、重启 | | | | |
| 13 | API Key 不再明文出现在 settings.json 里（第 5 步后） | | | | |
| 14 | 日志文件生成在 LogDirectory | | | | |

## 风险

| 风险 | 应对 |
|---|---|
| 平台代码平移时引入回归（尤其 macOS 的 objc 调用） | 第 1 步只平移、不改逻辑；在 macOS 上把清单完整跑一遍再合并 |
| Windows 上切换 `WS_EX_TRANSPARENT` 和 Avalonia 的合成方式冲突（比如闪烁、拖拽中断） | 2a 先做原型；按住期间一律不切，和 macOS 一样 |
| Linux 发行版、桌面环境差异大 | 能力全部在运行时探测，失败就降级；只承诺 Ubuntu 24.04 + GNOME/KDE 的 X11 和 XWayland |
| 拆项目后 AOT 的 XAML 编译、裁剪出问题 | 第 4 步单独做，三个平台都过 `--smoke` 再合并；`IsAotCompatible=true` 加在所有库项目上 |
| 设置迁移丢数据 | 迁移前先备份；用真实的 v1 文件做测试；迁移失败用默认值启动，并保留原文件 |
| 引入密钥存储后，用户换机器或系统重装导致 Key 丢失 | 界面上提示「API Key 存在系统钥匙串里」；读不到 Key 时让用户重新输入，不让程序崩溃 |
