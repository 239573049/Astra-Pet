# Astra Pet 架构重设计

这一组文档描述 Astra Pet 下一阶段的代码分层，重点是**把平台差异收拢到一处、让三个桌面平台的能力对齐**。
文档只描述目标和迁移路径，不代表已经实现；每完成一步就回来更新对应章节的状态。

| 文档 | 内容 |
|---|---|
| [01-layering.md](01-layering.md) | 分层、项目划分、依赖规则、目录结构 |
| [02-platform.md](02-platform.md) | 平台抽象层：接口、能力描述、macOS / Windows / Linux 各自的实现方案 |
| [03-core.md](03-core.md) | 不依赖 UI 的核心：动画、帧源、行为拆分、时间和线程、设置模型与迁移 |
| [04-migration.md](04-migration.md) | 分步迁移计划、每步的验收标准、CI 改动、风险 |

## 为什么要重新分层

现状（v0.1.3，单个 `src/AstraPet.csproj`，约 7k 行 C#）的主要问题：

1. **平台判断散落各层。** `OperatingSystem.IsXxx()` 出现在 `Core/`（AppVersion、UpdateChecker、UpdateInstaller）、
   `ControlPanel/`（ConsoleViewModel 拼平台说明、自己选 explorer / open / xdg-open）和 `Pet/`（直接持有 `MacWindow?`、调 `WinWindow`）。
   「用浏览器打开链接」在三个 ViewModel 里各写了一遍。
2. **跨平台能力不对齐，而且看不出来。** 点击穿透只在 macOS 上生效（Windows 上 `UpdatePassthrough` 直接 return）；
   听歌检测只有 macOS；Linux 上自启、朗读、空闲检测、点击穿透都没有。哪些能用、哪些不能用，界面靠手写的字符串告诉用户。
3. **`Core` 名不副实。** 领域逻辑（`PetDirector`、`Gait`）、基础设施（ffmpeg 子进程、HTTP、JSON 落盘）、平台安装逻辑混在一起；
   行为层 `PetCompanion` 直接依赖 `DispatcherTimer` 和静态的 `Speech` / `SystemMonitor`，无法脱离 Avalonia 测试。
4. **`PetSettings` 是上帝对象。** 388 行，持久化配置和运行时状态（`Paused`、`MusicPlaying`、`PowerSavingActive`）混在一起，
   所有层都能直接读写。
5. **没有测试项目。** 质量只靠 `--smoke` 和 CI 的 AOT 构建兜底。

## 设计原则

- **Core 不认识平台，也不认识 Avalonia。** Core 只依赖 BCL，可以在任何平台上跑单元测试。
- **界面按能力分支，不按操作系统分支。** ViewModel 里不出现 `OperatingSystem.IsXxx()`，只读 `PlatformCapabilities`。
- **每个平台服务都有「不支持」实现。** 调用处不写 `?.`、不判空；不支持的平台返回明确的 `Capabilities = false`。
- **NativeAOT 优先。** 不引入反射式 DI 容器、不用反射式序列化；P/Invoke 用 `[LibraryImport]` 源生成；
  平台分支用 `OperatingSystem.IsXxx()`，让 ILC 在按 RID 发布时把其他平台的分支裁掉。
- **小步迁移，每步都能发版。** 每一步结束时 `dotnet build`、`--smoke`、三个平台的 release 构建都要通过。
- **保持用户数据兼容。** 设置文件格式变化必须带版本号和自动迁移，老用户升级后配置不丢。
