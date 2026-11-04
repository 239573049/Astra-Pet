# 版本日志

## 0.1.4 — 2026-11-04

### 新增

- **角色包（`.astrapet`）**：内置角色改为角色包，默认角色仍是小t。新增内置角色「大肥鱼」（CC BY-NC-SA 4.0，非 RoutinAI 出品，禁止商用，署名见控制台）。
- **控制台显示当前角色的作者、许可和署名**，可以直接打开许可文本。
- **应用内更新**：下载安装包并就地安装、重启。macOS 替换 `.app`，Windows 静默运行安装程序，Linux 安装 `.deb`；其他 Linux 安装方式打开下载页。更新检测会记录安装包类型，控制台里可以一键下载安装。
- **Linux**：开机自启（XDG）、朗读（speech-dispatcher / espeak-ng）、空闲与声音检测；新增 `--platform-report` 用于排查。
- **Windows**：点击穿透按像素生效，控制台的「点击穿透」开关可用（尚未在 Windows 真机上验证）。

### 变化

- 界面控件库从 AtomUI 换成 TKCode.Arc（MIT，自带流式 Markdown 和代码高亮）。
- 运行时升级到 **.NET 11**。
- 日志按天写入文件，保留 7 天；未处理的异常也会写入日志（Windows 发布版之前没有控制台，日志等于丢失）。
- macOS 听歌检测改为直接查询 CoreAudio，不再每 5 秒启动一次 `pmset`。
- 久坐计时：「离开 5 分钟算活动过」的判断移到每 15 秒一次的提醒检查里。

### 修复

- Windows：空闲时长在开机满 49.7 天后回绕。
- Linux：`~/.config` 不存在时数据目录会变成相对路径。
- Linux：子进程的 stderr 不再漏到终端。
- 日志目录还不存在时清理旧日志会抛出首次异常。

### 内部

- 拆成 Core / Infrastructure / Platform / App 四个项目；Core 不依赖 Avalonia 和平台层，具体实现只在组合根 `Composition` 里创建。
- 新增单元测试，共 106 个。
- 许可清理：移除 AtomUI 相关的 LGPL / GPL 许可文件，重新生成 `THIRD_PARTY_NOTICES.md`（45 个 NuGet 包，均为 MIT / Apache-2.0 / BSD）。

## 更早的版本

0.1.0 – 0.1.3 没有单独整理版本日志，可以用 `git log v0.1.2..v0.1.3` 查看各版本的提交。
