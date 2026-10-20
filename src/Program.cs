using AstraPet.Core;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using AtomUI.Theme.DesignTokens;
using Avalonia;

namespace AstraPet;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke")) return Smoke();

#if DEBUG
        // 正常运行路径不应该抛异常（见 ClipStream.ReadFrames 的说明），出现了就打日志方便排查。
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            Console.Error.WriteLine($"[首次异常] {e.Exception.GetType().Name}: {e.Exception.Message}");
#endif
        var videosDir = ResolveVideosDir();
        var ffmpeg = ResolveFfmpeg();
        PetLog.Write($"内置素材目录: {videosDir}");
        PetLog.Write($"ffmpeg: {ffmpeg}");

        // 素材库在 App 里按设置选的角色加载（要先读设置），每段只解一帧，确认解码器和 alpha 没问题。
        App.BuiltInVideosDir = videosDir;
        App.Ffmpeg = ffmpeg;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// NativeAOT 冒烟自测（CI 里发布后跑一次）：起 Avalonia 和 AtomUI 主题，把控制台的 XAML 完整加载一遍但不显示。
    /// 直接 publish 时 Avalonia 的 XAML 编译被跳过、或者有东西被裁掉，都会在这里抛异常，而不是用户双击后才发现。
    /// </summary>
    private static int Smoke()
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            _ = new ControlPanel.ConsoleWindow();
            Console.WriteLine("smoke ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"smoke failed: {e}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseAtomUIPlatformDetect()
            .WithAtomUIDefaultOptions()
            .UseAtomUI(builder =>
            {
                // AtomUI derives control resources from its own theme configuration.
                // RequestedThemeVariant alone leaves controls using the default light palette.
                builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID,
                    new ThemeConfigBuilder()
                        .WithAlgorithms(ThemeAlgorithm.Dark, ThemeAlgorithm.Compact)
                        .WithToken(nameof(DesignToken.ColorPrimary), "#32AA7D")
                        .WithToken(nameof(DesignToken.FontSize), "12")
                        .WithToken(nameof(DesignToken.ControlHeightSM), "26")
                        .Build());
                builder.UseDesktopControls();
            })
            .LogToTrace();

    /// <summary>内置角色的素材目录：优先用环境变量，其次找输出目录里的素材，最后找源码树里的素材。</summary>
    private static string ResolveVideosDir()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ASTRA_PET_VIDEOS"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "videos"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "videos")),
        };
        var found = candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
        if (found is null)
            throw new DirectoryNotFoundException(
                "找不到素材目录。把 webm 放到 Assets/videos 下，或设置 ASTRA_PET_VIDEOS。");
        return found;
    }

    private static string ResolveFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FFMPEG");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        // 官方发布包把精简的 ffmpeg 放在程序旁边（见 tools/ffmpeg/build.sh），优先用它：版本固定，一定带 libvpx-vp9。
        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (File.Exists(bundled)) return bundled;

        foreach (var candidate in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "ffmpeg" })
            if (candidate == "ffmpeg" || File.Exists(candidate))
                return candidate;
        throw new FileNotFoundException("找不到 ffmpeg，请安装或设置 FFMPEG 环境变量。");
    }
}
