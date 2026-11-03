using System.Diagnostics;
using AstraPet.Core;

namespace AstraPet.Platform;

/// <summary>默认的路径规则。数据目录在各平台都是「应用数据目录/AstraPet」，和以前的版本保持一致。</summary>
internal class AppPaths : IAppPaths
{
    private static readonly string? CustomData =
        Environment.GetEnvironmentVariable("ASTRA_PET_DATA") is { Length: > 0 } custom ? custom : null;

    // DoNotVerify：默认选项下目录不存在时返回空串（新装的 Linux 用户可能还没有 ~/.config），
    // 拼出来就成了相对路径，设置会写到当前工作目录里。目录由用到的地方自己创建。
    public string DataDirectory { get; } =
        CustomData ?? Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "AstraPet");

    protected static string Folder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    public string LogDirectory => CustomData is not null ? Path.Combine(CustomData, "logs") : PlatformLogDirectory;

    /// <summary>默认按 XDG 规范：$XDG_STATE_HOME（默认 ~/.local/state）/AstraPet。macOS、Windows 各自覆盖。</summary>
    protected virtual string PlatformLogDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Folder(Environment.SpecialFolder.UserProfile), ".local", "state"), "AstraPet");

    public string AppDirectory { get; } = AppContext.BaseDirectory;

    public virtual string ResourceDirectory => AppDirectory;

    /// <summary>可执行文件的扩展名，Windows 上是 .exe。</summary>
    protected virtual string ExecutableSuffix => "";

    /// <summary>PATH 之外还要找的系统目录（比如 macOS 上从 Finder 启动时 PATH 里没有 Homebrew）。</summary>
    protected virtual IEnumerable<string> SystemToolDirectories => [];

    public string FindTool(string name)
    {
        var file = name + ExecutableSuffix;
        // 官方发布包把工具放在程序旁边，优先用它：版本固定。
        var bundled = Path.Combine(AppDirectory, file);
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in SystemToolDirectories)
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        return file;
    }
}

/// <summary>交给系统默认程序打开。.NET 的 UseShellExecute 在三个平台上分别走 ShellExecute、open、xdg-open。</summary>
internal sealed class SystemShell : IShell
{
    public bool OpenUrl(string url) => Start(url, "打开网页");

    public bool OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception e)
        {
            PetLog.Write($"创建目录失败: {e.Message}");
            return false;
        }
        return Start(path, "打开目录");
    }

    private static bool Start(string target, string what)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            PetLog.Write($"{what}失败: {e.Message}");
            return false;
        }
    }
}
