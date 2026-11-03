using AstraPet.Core;
using AstraPet.Platform;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using Avalonia;
using Avalonia.Media;

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
        var platform = PlatformFactory.Create();
        SettingsStore.UseDirectory(platform.Paths.DataDirectory);
        var videosDir = ResolveVideosDir(platform.Paths);
        var ffmpeg = ResolveFfmpeg(platform.Paths);
        PetLog.Write($"平台: {platform.Identity}");
        PetLog.Write($"内置素材目录: {videosDir}");
        PetLog.Write($"ffmpeg: {ffmpeg}");

        // 素材库在 App 里按设置选的角色加载（要先读设置），每段只解一帧，确认解码器和 alpha 没问题。
        App.Platform = platform;
        App.BuiltInVideosDir = videosDir;
        App.Ffmpeg = ffmpeg;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// NativeAOT 冒烟自测（CI 里发布后跑一次）：选平台实现、起 Avalonia 和 Arc 主题，把控制台的 XAML 完整加载一遍但不显示。
    /// 直接 publish 时 Avalonia 的 XAML 编译被跳过、或者有东西被裁掉，都会在这里抛异常，而不是用户双击后才发现。
    /// </summary>
    private static int Smoke()
    {
        try
        {
            var platform = PlatformFactory.Create();
            Console.WriteLine($"platform: {platform.Identity} {platform.Capabilities with { Notes = new Dictionary<string, string>() }}");
            BuildAvaloniaApp().SetupWithoutStarting();
            _ = new ControlPanel.ConsoleWindow();
            _ = new ControlPanel.ChatWindow();
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
            .UsePlatformDetect()
            .WithInterFont()
            .WithArcFonts()
            .UseArc(arc => arc
                .UseArcControls()
                // 固定深色，主色沿用宠物的绿。
                .WithTheme(t => t.Appearance(ArcAppearance.Dark).Accent(Color.Parse("#32AA7D"))))
            .LogToTrace();

    /// <summary>内置角色的素材目录：优先用环境变量，其次找程序的资源目录，最后找源码树里的素材（开发运行）。</summary>
    private static string ResolveVideosDir(IAppPaths paths)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ASTRA_PET_VIDEOS"),
            Path.Combine(paths.ResourceDirectory, "Assets", "videos"),
            Path.GetFullPath(Path.Combine(paths.AppDirectory, "..", "..", "..", "Assets", "videos")),
        };
        var found = candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
        if (found is null)
            throw new DirectoryNotFoundException(
                "找不到素材目录。把 webm 放到 Assets/videos 下，或设置 ASTRA_PET_VIDEOS。");
        return found;
    }

    /// <summary>
    /// 环境变量 FFMPEG 优先；其次是官方发布包放在程序旁边的精简版（见 tools/ffmpeg/build.sh，版本固定，一定带 libvpx-vp9）；
    /// 再其次是系统里的。
    /// </summary>
    private static string ResolveFfmpeg(IAppPaths paths) =>
        Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } fromEnv ? fromEnv : paths.FindTool("ffmpeg");
}
