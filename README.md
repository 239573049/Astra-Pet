# Astra Pet

一只住在桌面上的宠物：会走动、会回应点击和摸头，可以接入大模型陪你聊天，也会提醒你喝水、起身活动。用 [Avalonia](https://avaloniaui.net/) 写成，支持 macOS 和 Windows，可以编成 Native AOT。

下载：<https://astra-gate.si/pet>　源码：<https://github.com/239573049/Astra-Pet>

## 许可 / License

> **源码开源（MIT），宠物形象的版权归 RoutinAI。 / Source code is open source (MIT); the pet artwork is copyrighted by RoutinAI.**

| 内容 / What | 许可 / License | 文件 / File |
|---|---|---|
| 程序源码 / Source code | **MIT** © 2026 RoutinAI | [LICENSE](LICENSE) |
| 宠物形象、动画、角色设定等素材 / Pet characters, animations, artwork（`src/Assets/videos/`、`characters/小t/`、`astra/`） | **© 2026 RoutinAI，保留所有权利 / All rights reserved**，只能按素材许可使用 | [LICENSE-ASSETS.md](LICENSE-ASSETS.md) |
| 第三方库、数据、随包分发的 FFmpeg / Third-party libraries, data, bundled FFmpeg | 各自的许可，其中 AtomUI 是 LGPL-3.0 / their own licenses (AtomUI is LGPL-3.0) | [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) |

要点：你可以自由使用、修改、发布这份**代码**；但**不能**把宠物素材拿去单独使用、修改、商用或再分发，也不能把它们打进你自己的发布包。想发布自己的版本，请换成你自己的角色（见 [LICENSE-ASSETS.md](LICENSE-ASSETS.md) 第 5 节）。

In short: you may freely use, modify and publish the **code**; you may **not** use the pet assets on their own, modify, commercialise or redistribute them, or ship them inside your own builds. To publish your own version, swap in your own character (see section 5 of [LICENSE-ASSETS.md](LICENSE-ASSETS.md)).

## 从源码构建

需要 .NET 10 SDK 和 ffmpeg（要带 `libvpx-vp9` 解码器：宠物动画是带透明通道的 VP9 WebM，ffmpeg 自带的 VP9 解码器会丢掉透明通道）。**官方发布包已经自带**一个精简的 LGPL 版 ffmpeg，直接用就行；从源码运行时用系统里的（`brew install ffmpeg`）或者自己构建一份（`tools/ffmpeg/build.sh`）。

```bash
dotnet run --project src        # 开发运行
```

素材目录和 ffmpeg 的查找顺序：

- 素材：环境变量 `ASTRA_PET_VIDEOS`，其次是程序目录下的 `Assets/videos`。
- ffmpeg：环境变量 `FFMPEG`，其次是程序旁边的 `ffmpeg`（Windows 是 `ffmpeg.exe`，官方发布包里就是这个），再其次是系统里的 ffmpeg。

### Native AOT

```bash
dotnet build   src/AstraPet.csproj -c Release -r osx-arm64 -p:PublishAot=true
dotnet publish src/AstraPet.csproj -c Release -r osx-arm64 -p:PublishAot=true --no-build -o publish
./publish/AstraPet --smoke      # 冒烟自测，输出 "smoke ok" 才算正常
```

必须**先 build 再 `publish --no-build`**：开着 AOT 时直接 `dotnet publish` 会让 Avalonia 跳过 XAML 编译，程序一启动就报 “No precompiled XAML found”。`-r` 换成 `osx-x64`、`win-x64` 就是别的平台。

### 发布

给仓库打 `v*` 标签（例如 `v0.2.0`、`v0.3.0-preview.1`），GitHub 工作流 `.github/workflows/release.yml` 会构建三个平台的包并上传到 <https://astra-gate.si>。上传需要在仓库 Secrets 里配置 `PET_UPLOAD_TOKEN`。

## 目录

```
src/            程序源码（Avalonia + AtomUI）
characters/     宠物角色（素材，见上面的许可表）
tools/          素材生成（petgen）、ffmpeg 构建、第三方声明生成等脚本
licenses/       随包分发的第三方许可证全文
```
