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
        if (args.Contains("--platform-report")) return PlatformReport();

#if DEBUG
        // 正常运行路径不应该抛异常（见 FfmpegFrameSource.ReadFrames 的说明），出现了就打日志方便排查。
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            Console.Error.WriteLine($"[首次异常] {e.Exception.GetType().Name}: {e.Exception.Message}");
#endif
        var platform = PlatformFactory.Create();
        PetLog.UseDirectory(platform.Paths.LogDirectory);
        // 崩溃前把原因写进日志文件：Windows 发布版没有控制台，这是唯一能看到的地方。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => PetLog.Write($"未处理的异常: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => PetLog.Write($"未观察到的任务异常: {e.Exception}");
        SettingsStore.UseDirectory(platform.Paths.DataDirectory);
        var videosDir = ResolveVideosDir(platform.Paths);
        var ffmpeg = ResolveFfmpeg(platform.Paths);
        PetLog.Write($"Astra Pet {AppVersion.Current} · {platform.Identity}");
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
            Console.WriteLine($"platform: {platform.Identity}（详细结果用 --platform-report）");
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

    /// <summary>
    /// 打印平台探测结果，不起界面：能力、说明、路径，以及实际读一次空闲时长、声音状态、语音列表。
    /// CI 里三个平台各跑一次，排查用户问题时也可以让对方跑。
    /// </summary>
    private static int PlatformReport()
    {
        var p = PlatformFactory.Create();
        var caps = p.Capabilities;
        Console.WriteLine($"platform     {p.Identity}");
        Console.WriteLine($"data         {p.Paths.DataDirectory}");
        Console.WriteLine($"logs         {p.Paths.LogDirectory}");
        Console.WriteLine($"resources    {p.Paths.ResourceDirectory}");
        Console.WriteLine($"ffmpeg       {p.Paths.FindTool("ffmpeg")}");
        foreach (var (name, supported) in new (string, bool)[]
                 {
                     (nameof(caps.ClickThrough), caps.ClickThrough), (nameof(caps.OverFullscreen), caps.OverFullscreen),
                     (nameof(caps.CaptureExclusion), caps.CaptureExclusion), (nameof(caps.AutoStart), caps.AutoStart),
                     (nameof(caps.Speech), caps.Speech), (nameof(caps.IdleDetection), caps.IdleDetection),
                     (nameof(caps.AudioDetection), caps.AudioDetection), (nameof(caps.InAppUpdate), caps.InAppUpdate),
                 })
            Console.WriteLine($"{(supported ? "[x]" : "[ ]")} {name,-17} {caps.NoteFor(name)}");
        Console.WriteLine($"idle         {p.Monitor.IdleTime()?.ToString() ?? "n/a"}");
        Console.WriteLine($"audio        {p.Monitor.IsAudioPlayingAsync().GetAwaiter().GetResult()}");
        var voices = p.Speech.ListVoicesAsync().GetAwaiter().GetResult();
        Console.WriteLine($"voices       {voices.Count - 1}（前几个：{string.Join("、", voices.Skip(1).Take(3))}）");
        Console.WriteLine($"autostart    {p.AutoStart.IsEnabled()}");
        return 0;
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
            // 开发运行：src/AstraPet.App/bin/<配置>/<框架>/ 往上四层是 src/，共享素材在 src/Assets。
            Path.GetFullPath(Path.Combine(paths.AppDirectory, "..", "..", "..", "..", "Assets", "videos")),
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
