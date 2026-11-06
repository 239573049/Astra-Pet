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
        // MCP 服务（03 §2.2）：stdio 上说 JSON-RPC，转发给正在运行的宠物。不起界面，stdout 只能写协议消息。
        if (args.Length > 0 && args[0] == "mcp") return Mcp();
        // 编程智能体的 hook（design/agent-hooks §3）：转发一次事件就退出，必须快、不能往 stdout 写东西。
        if (args.Length > 0 && args[0] == "hook") return Hook(args);
        if (args.Contains("--smoke")) return Smoke();
        if (args.Contains("--platform-report")) return PlatformReport();

#if DEBUG
        // 正常运行路径不应该抛异常（见 FfmpegFrameSource.ReadFrames 的说明），出现了就打日志方便排查。
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            Console.Error.WriteLine($"[首次异常] {e.Exception.GetType().Name}: {e.Exception.Message}");
#endif
        // 崩溃前把原因写进日志文件：Windows 发布版没有控制台，这是唯一能看到的地方。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => PetLog.Write($"未处理的异常: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => PetLog.Write($"未观察到的任务异常: {e.Exception}");

        App.Composition = Composition.Create();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int Mcp()
    {
        // PetLog 会写 Console.Out；协议消息直接写标准输出流，其余输出改到 stderr，免得混进协议里。
        Console.SetOut(Console.Error);
        var paths = PlatformFactory.Create().Paths;
        PetLog.UseDirectory(paths.LogDirectory);
        SettingsStore.UseDirectory(paths.DataDirectory);
        try
        {
            return AstraPet.External.McpCommand.RunAsync(SettingsStore.FilePath).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            PetLog.Write($"MCP 服务出错: {e}");
            return 1;
        }
    }

    private static int Hook(string[] args)
    {
        // Claude Code 会把一部分 hook 的 stdout 交给模型，日志一律不进 stdout。
        Console.SetOut(TextWriter.Null);
        try
        {
            var paths = PlatformFactory.Create().Paths;
            PetLog.UseDirectory(paths.LogDirectory);
            SettingsStore.UseDirectory(paths.DataDirectory);
            return AstraPet.External.AgentHookCommand.Run(SettingsStore.FilePath, args);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// NativeAOT 冒烟自测（CI 里发布后跑一次）：走一遍组合根、起 Avalonia 和 Arc 主题，把控制台的 XAML 完整加载一遍但不显示。
    /// 直接 publish 时 Avalonia 的 XAML 编译被跳过、或者有东西被裁掉，都会在这里抛异常，而不是用户双击后才发现。
    /// </summary>
    private static int Smoke()
    {
        try
        {
            // 走一遍组合根（选平台、定目录、找素材），组装出错在这里就暴露。不加载素材库：CI 上没有 ffmpeg。
            var composition = Composition.Create();
            Console.WriteLine($"platform: {composition.Platform.Identity}（详细结果用 --platform-report）");
            BuildAvaloniaApp().SetupWithoutStarting();
            _ = new ControlPanel.ConsoleWindow();
            _ = new Pet.ChatInputWindow();
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
                .UseArcBlocks()
                .UseArcSpecial()
                // 启动时先跟随系统、用宠物的绿；设置加载后 App.ApplyTheme 再按用户的选择套一次。
                .WithTheme(t => t.Appearance(ArcAppearance.System).Accent(Color.Parse(PetSettings.DefaultAccent))))
            .LogToTrace();
}
