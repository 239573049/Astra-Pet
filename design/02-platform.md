# 02 平台抽象层

本章是这次重构的重点。目标有两个：

1. **收拢**：所有与操作系统有关的代码只出现在 `AstraPet.Platform` 里，其余各层看不到 `OperatingSystem.IsXxx()`。
2. **对齐**：macOS、Windows、Linux 三个平台能实现的能力都实现；实在做不到的，用能力描述明确告诉界面，而不是悄悄失效。

## 1. 现状盘点

| 能力 | 现在的实现位置 | macOS | Windows | Linux |
|---|---|---|---|---|
| 窗口透明 | `MacWindow.MakeTransparent` | ✅ objc | Avalonia 自带 | Avalonia 自带 |
| **逐像素点击穿透** | `PetWindow.UpdatePassthrough` + `MacWindow.IgnoresMouse` | ✅ | ❌ `_mac is null` 直接返回 | ❌ |
| 全屏应用上方显示 / 隐藏 | `MacWindow.SetSpaces` | ✅ | ❌ | ❌ |
| 录屏时隐藏 | `MacWindow.SetCapturable` / `WinWindow.SetCapturable` | ✅ | ✅ Win10 2004+ | ❌ |
| 毛玻璃背景（控制台） | `MacWindow.InstallGlassBackdrop` | ✅ | — | — |
| 开机自启 | `Platform/AutoStart` | ✅ LaunchAgent | ✅ HKCU\Run | ❌ |
| 朗读 | `Platform/Speech` | ✅ `say` | ✅ PowerShell + System.Speech | ❌ |
| 空闲时长 | `SystemMonitor.IdleSeconds` | ✅ CoreGraphics | ✅ GetLastInputInfo | ❌ 恒为 0 |
| 是否在放声音 | `SystemMonitor.IsAudioPlaying` | ✅ `pmset -g assertions` | ❌ | ❌ |
| 打开链接 / 文件夹 | 3 个 ViewModel 各写一遍 | ✅ | ✅ | ✅ xdg-open |
| 数据目录 | `SettingsStore`（SpecialFolder.ApplicationData） | ✅ | ✅ | ✅ `~/.config/AstraPet` |
| 日志文件 | 无，只写 stdout | ⚠️ | ⚠️ WinExe 没有控制台，日志等于丢了 | ⚠️ |
| 应用内更新 | `Core/UpdateInstaller` | ✅ dmg | ✅ Inno 静默安装 | ✅ deb（pkexec） |
| API Key 存储 | 明文写在 settings.json | ⚠️ | ⚠️ | ⚠️ |
| 平台标识 | `Core/AppVersion.Platform` | ✅ | ✅ | ✅ |

## 2. 接口

所有接口定义在 `AstraPet.Core/Ports/Platform/`，实现在 `AstraPet.Platform/`。

```csharp
public interface IPlatform
{
    PlatformIdentity Identity { get; }          // os=darwin|win32|linux, arch=arm64|x64，拼成更新源要的 "darwin-arm64"
    PlatformCapabilities Capabilities { get; }
    IAppPaths Paths { get; }
    IShell Shell { get; }
    IAutoStart AutoStart { get; }
    ISpeech Speech { get; }
    ISystemMonitor Monitor { get; }
    ISecretStore Secrets { get; }
    IUpdateInstaller Installer { get; }

    /// <summary>窗口打开后调用；handle 是 NSWindow* / HWND / X11 Window。拿不到时返回空实现，不返回 null。</summary>
    INativeWindow AttachWindow(nint handle, NativeWindowRole role);
}

public enum NativeWindowRole { Pet, Bubble, Panel }
```

### 2.1 能力描述

```csharp
public sealed record PlatformCapabilities
{
    public bool ClickThrough { get; init; }       // 逐像素点击穿透
    public bool OverFullscreen { get; init; }     // 能控制「全屏应用上方是否显示」
    public bool CaptureExclusion { get; init; }   // 录屏时隐藏
    public bool AutoStart { get; init; }
    public bool Speech { get; init; }
    public bool IdleDetection { get; init; }
    public bool AudioDetection { get; init; }
    public bool SecureSecrets { get; init; }      // false 时 API Key 退回明文，界面要提示
    public bool InAppUpdate { get; init; }
    public bool GlassBackdrop { get; init; }

    /// <summary>不支持某项时给用户看的原因，键是上面的属性名。</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();
}
```

有些能力要到运行时才知道，比如 Linux 上有没有装 `spd-say`、当前是不是 Wayland 会话、Windows 版本够不够 2004。
所以 `Capabilities` 在 `PlatformFactory.Create()` 时探测一次，结果缓存起来。

界面怎么用：

```csharp
// 之前
public string PlatformNote => OperatingSystem.IsMacOS() ? "…" : "全屏隐藏目前只在 macOS 上生效…";
// 之后
public bool CanHideInFullscreen => _caps.OverFullscreen;
public string? HideInFullscreenNote => _caps.Notes.GetValueOrDefault(nameof(PlatformCapabilities.OverFullscreen));
```

开关项绑定 `IsEnabled="{Binding CanXxx}"`，旁边显示 Note。不支持的功能显示为灰色并说明原因，而不是开了没反应。

### 2.2 窗口

```csharp
public interface INativeWindow
{
    bool IgnoresMouse { get; set; }                // 点击穿透：true 时鼠标落到下面的窗口
    PixelPoint? CursorInWindow();                  // 光标相对窗口左上角的位置（物理像素），拿不到返回 null
    void SetCapturable(bool capturable);
    void SetOverFullscreen(bool over);
    void MakeTransparent();                        // 不需要的平台是空操作
    void SetAlpha(double alpha);
    bool TryInstallGlassBackdrop();
}
```

**逐像素点击穿透的通用算法留在 App 层**，平台只提供两个原语：「让窗口忽略鼠标」和「光标在哪」。

```
每 15ms：
  if 正在按住:                 IgnoresMouse = false   // 拖拽不能断
  else if 未开点击穿透:         IgnoresMouse = false
  else if CursorInWindow() is p && 当前帧 p 处 alpha >= 32:  IgnoresMouse = false
  else:                         IgnoresMouse = true
```

这和现在 macOS 上的做法一样，只是把 `MacWindow` 换成接口，Windows 和 Linux 填上各自的实现，`PetWindow` 里就不用再判断平台了。

### 2.3 其余服务

```csharp
public interface IAppPaths
{
    string DataDirectory { get; }        // 设置、角色目录
    string LogDirectory { get; }
    string CacheDirectory { get; }       // 更新下载、模型库刷新缓存
    string AppDirectory { get; }         // 程序所在目录（.app 里是 Contents/MacOS）
    string ResourceDirectory { get; }    // 素材、licenses（.app 里是 Contents/Resources，其他平台同 AppDirectory）
    string ExecutablePath { get; }       // 自启、重启用；.app 时指向 .app 包而不是里面的二进制
}

public interface IShell
{
    bool OpenUrl(string url);
    bool OpenFolder(string path);        // 不存在先创建
    bool RevealFile(string path);        // 在文件管理器里选中
}

public interface IAutoStart
{
    bool IsEnabled();
    bool Apply(bool enabled);            // 参数从 IAppPaths 取，不再从 ViewModel 传 videosDir / ffmpeg 进来
}

public interface ISpeech
{
    Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(CancellationToken ct = default);
    void Speak(string text, SpeechOptions options);   // 打断上一句
    void Stop();
}
public sealed record SpeechOptions(string Voice, int WordsPerMinute, double Volume);  // 统一单位，各平台自行换算

public interface ISystemMonitor
{
    TimeSpan? IdleTime();                // null 表示拿不到；调用方不能把它当成 0
    Task<bool?> IsAudioPlayingAsync(CancellationToken ct = default);   // 可能起子进程，一律异步
}

public interface ISecretStore
{
    bool IsSecure { get; }
    string? Get(string key);
    void Set(string key, string? value); // null 表示删除
}

public interface IUpdateInstaller
{
    IReadOnlyList<string> PreferredKinds { get; }    // macOS: dmg,zip；Windows: exe,zip；Linux: deb,appimage,zip
    bool CanInstall(string kind);
    Task<InstallResult> InstallAsync(string file, CancellationToken ct);  // 成功后调用方退出程序
}
```

> `IdleTime()` 返回 `TimeSpan?` 而不是现在的「不支持就返回 0」。0 的意思是「用户一直在用」，
> 在不支持的平台上会让省电模式永远不启动，而且看不出是没实现。返回 null 能让调用方明确处理。

## 3. 各平台实现方案

### 3.1 macOS

| 服务 | 方案 | 说明 |
|---|---|---|
| 窗口 | 继续用 objc_msgSend（`MacWindow` 现有代码平移） | `DllImport` 改成 `[LibraryImport]` |
| 光标位置 | `mouseLocationOutsideOfEventStream`（现有） | 返回值要做 Y 轴翻转和 point→像素换算，这一步挪进实现里 |
| 空闲 | `CGEventSourceSecondsSinceLastEventType`（现有） | |
| 声音检测 | **改成** CoreAudio：在默认输出设备上读 `kAudioDevicePropertyDeviceIsRunningSomewhere` | 不用每 5 秒起一个 `pmset` 子进程，开销从约 20ms 降到微秒级；`pmset` 方案留作后备 |
| 朗读 | `say`（现有） | |
| 自启 | `~/Library/LaunchAgents/*.plist`（现有） | `ProgramArguments` 用 `IAppPaths.ExecutablePath` |
| 密钥 | Keychain：`SecKeychainAddGenericPassword` / `FindGenericPassword` | **不用 `security` 命令行**，因为密钥会出现在命令行参数里，其他进程用 `ps` 就能看到 |
| 安装器 | 挂载 dmg → 复制 .app → 退出后由脚本替换并重新打开（现有） | |
| 路径 | Data：`~/Library/Application Support/AstraPet`；Log：`~/Library/Logs/AstraPet`；Cache：`~/Library/Caches/AstraPet` | 数据目录保持不变，兼容老用户 |

### 3.2 Windows

| 服务 | 方案 | 说明 |
|---|---|---|
| **点击穿透** | `SetWindowLongPtr(GWL_EXSTYLE)` 切换 `WS_EX_TRANSPARENT \| WS_EX_LAYERED` | 光标位置用 `GetCursorPos` + `ScreenToClient`。不能靠 `WM_NCHITTEST` 返回 `HTTRANSPARENT`，因为它只会把点击传给同一线程的其他窗口，传不到别的程序 |
| 全屏应用 | `SHQueryUserNotificationState`：结果是 `QUNS_BUSY`、`QUNS_RUNNING_D3D_FULL_SCREEN` 或 `QUNS_PRESENTATION_MODE` 时，说明前台是全屏应用，按设置隐藏宠物 | 和 macOS 的 Spaces 语义不同：Windows 上做法是「检测到全屏就隐藏」。由 Presence 行为每 5 秒查一次 |
| 录屏时隐藏 | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`（现有） | 低于 Win10 2004 时探测失败，`CaptureExclusion = false` |
| 空闲 | `GetLastInputInfo`（现有） | 注意 `TickCount` 的 32 位回绕 |
| **声音检测** | WASAPI：默认渲染设备上的 `IAudioMeterInformation.GetPeakValue() > 0.001` | COM 用 `[GeneratedComInterface]` 源生成，能在 AOT 下用。届时去掉 csproj 里的 `BuiltInComInteropSupport`，它与 AOT 不兼容 |
| 朗读 | 第一阶段保留 PowerShell + System.Speech；第二阶段改成 SAPI `ISpVoice` COM | PowerShell 每次启动约 1 秒，第一句话有明显延迟 |
| 自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（现有） | |
| 密钥 | DPAPI（`CryptProtectData`，CurrentUser 范围），加密后的结果存在 settings.json 里 | 不引入 Credential Manager，免得多一处要清理 |
| 安装器 | Inno Setup `/SILENT /relaunch=1`（现有） | |
| 路径 | Data：`%APPDATA%\AstraPet`；Log 和 Cache：`%LOCALAPPDATA%\AstraPet\{logs,cache}` | |

### 3.3 Linux

先说清楚范围：**Avalonia 在 Linux 上走 X11 后端，Wayland 会话下通过 XWayland 运行**（升级 Avalonia 时要复核这一点）。
所以下面的窗口能力都基于 X11 实现。启动时检测 `XDG_SESSION_TYPE=wayland`，在 Notes 里注明「Wayland 会话下部分能力受限」。

| 服务 | 方案 | 说明 |
|---|---|---|
| 点击穿透 | XShape：`XShapeCombineRectangles(…, ShapeInput, …)` 把输入区域设为空或整窗 | 光标用 `XQueryPointer`。在 XWayland 下也有效 |
| 全屏应用 | 读 `_NET_ACTIVE_WINDOW`，再看它的 `_NET_WM_STATE` 里有没有 `_NET_WM_STATE_FULLSCREEN` | 和 Windows 一样，检测到全屏就隐藏；纯 Wayland 窗口读不到，结果当作「不是全屏」 |
| 录屏时隐藏 | 不支持 | `CaptureExclusion = false` |
| 空闲 | 优先用 libXss 的 `XScreenSaverQueryInfo`；没有时用 `gdbus` 调 `org.gnome.Mutter.IdleMonitor.GetIdletime`；再没有就用 `org.freedesktop.ScreenSaver.GetSessionIdleTime`（KDE） | 三种都失败时 `IdleDetection = false` |
| 声音检测 | `pactl list short sinks`，看有没有状态是 `RUNNING` 的设备 | PulseAudio 和 PipeWire（pipewire-pulse）都兼容；没有 `pactl` 时 `AudioDetection = false` |
| 朗读 | 优先 `spd-say`（speech-dispatcher），没有就用 `espeak-ng` | 语速、音量从 `SpeechOptions` 换算成 -100~100；声音列表用 `spd-say -L` |
| 自启 | 写 `$XDG_CONFIG_HOME/autostart/astra-pet.desktop`（默认 `~/.config/autostart`） | `Exec` 写 `/usr/bin/astra-pet`；不是 deb 安装的就写 `ExecutablePath` |
| 密钥 | 用 `secret-tool store/lookup`（libsecret），密钥**从标准输入传** | 没有 `secret-tool` 或没有 keyring 服务时退回明文，`SecureSecrets = false`，界面上提示 |
| 安装器 | `pkexec dpkg -i`（现有） | 不是 deb 安装的（比如手动解压）时 `InAppUpdate = false`，改为打开下载页 |
| 路径 | Data：`$XDG_CONFIG_HOME/AstraPet`（就是现在的位置，保持不变）；Log：`$XDG_STATE_HOME/AstraPet`；Cache：`$XDG_CACHE_HOME/AstraPet` | |
| 打开链接 | `xdg-open` | |

### 3.4 不支持的平台

`PlatformFactory` 识别不了的系统（FreeBSD 等）返回 `UnsupportedPlatform`：所有能力都是 false，
`Shell` 尝试 `xdg-open`，路径按 XDG 规范。程序能跑起来、宠物能动，只是没有附加能力。

## 4. AOT 与实现约束

- **P/Invoke**：统一用 `[LibraryImport]` + `partial`（源生成），不再用 `DllImport`。macOS 的 objc_msgSend 按签名声明多个入口（现有做法）。
- **COM**：只用 `[GeneratedComInterface]` / `StrategyBasedComWrappers`，不依赖内置 COM 互操作。
- **平台分支**：`PlatformFactory.Create()` 里用 `OperatingSystem.IsMacOS()` 这类判断。按 RID 做 NativeAOT 发布时，
  ILC 会把它们当常量处理，其他平台的实现类整个被裁掉，不会增加产物体积。
- **标注**：每个平台实现类加 `[SupportedOSPlatform("macos")]` 等标注，平台兼容性分析器会检查有没有在错误的平台上被调用。
- **子进程**：统一走 `Common/ProcessRunner`，负责超时、不抛异常、读 stderr 写日志。现在 `Speech`、`SystemMonitor`、`UpdateInstaller` 里各写了一套，到时合并成这一个。
- **不在 UI 线程上起子进程**：`IsAudioPlayingAsync`、`ListVoicesAsync` 都是异步接口。

## 5. 测试策略

- Core 和 App 里的逻辑通过 `FakePlatform`（所有能力可配置）测试。比如「`IdleTime()` 返回 null 时，省电模式不启动」。
- Platform 的实现很难做单元测试，做两类检查：
  - CI 的三平台 `--smoke` 里加一步 `--platform-report`，把探测到的 `Capabilities` 打出来，确认各平台至少能跑通探测；
  - 维护一张手动测试清单（见 [04-migration.md](04-migration.md#手动测试清单每个平台都要过)），发版前在三个平台各过一遍。
