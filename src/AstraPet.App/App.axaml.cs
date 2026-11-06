using AstraPet.ControlPanel;
using AstraPet.Core;
using AstraPet.Infrastructure.Studio;
using AstraPet.Pet;
using AstraPet.Studio;
using AstraPet.Core.Studio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TKCode.Arc;

namespace AstraPet;

/// <summary>
/// 界面的组装：用 <see cref="Composition"/> 准备好的东西建宠物窗口（主窗口）和行为层，
/// 控制台和对话框按需创建。设置变化后 0.5 秒存盘一次。
/// </summary>
public partial class App : Application
{
    /// <summary>Program 在启动 Avalonia 之前建好，放这里。冒烟测试和设计器里为 null。</summary>
    public static Composition? Composition { get; set; }

    private PetSettings? _settings;
    private PetDirector? _director;
    private PetWindow? _pet;
    private PetCompanion? _companion;
    private ConsoleWindow? _console;

    /// <summary>控制台收起这么久之后释放（见 <see cref="ConsoleWindow.Release"/>）。</summary>
    private static readonly TimeSpan ConsoleReleaseDelay = TimeSpan.FromMinutes(2);
    private readonly DispatcherTimer _consoleReleaseTimer = new() { Interval = ConsoleReleaseDelay };
    private ChatInputWindow? _chat;
    private StudioWindow? _studio;
    /// <summary>工作台收起后也同样过一阵释放（见 <see cref="StudioWindow.Release"/>）。</summary>
    private readonly DispatcherTimer _studioReleaseTimer = new() { Interval = ConsoleReleaseDelay };
    private VideoTaskQueue? _videoQueue;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Composition is { } c)
        {
            var (settings, director) = c.LoadPet();
            _settings = settings;
            _director = director;

            // 明暗和主色：启动时按设置套一次，之后控制台里改了立即生效。
            ApplyTheme(settings);
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PetSettings.Appearance) or nameof(PetSettings.Accent)) ApplyTheme(settings);
            };

            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsStore.Save(settings); };
            _consoleReleaseTimer.Tick += (_, _) => TryReleaseConsole();
            _studioReleaseTimer.Tick += (_, _) => TryReleaseStudio();
            settings.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };

            _pet = new PetWindow(director, settings, c.Platform);
            _companion = new PetCompanion(_pet, director, settings, c.Platform, c.Llm, c.UpdateFeed);
            _companion.ConsoleRequested += ShowConsole;
            _companion.ChatRequested += ShowChat;

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = _pet;
            desktop.ShutdownRequested += (_, _) =>
            {
                _saveTimer.Stop();
                _studio?.Shutdown();
                SettingsStore.Save(settings);
                _videoQueue?.Dispose();   // 只停轮询；任务记录都在盘上，下次启动接着查
            };

            // 工作台的视频任务：上次没查完的（包括强退的）接着查，绝不重新提交。
            _videoQueue = c.CreateVideoQueue(settings);
            var studioRoot = c.StudioRoot;
            var queue = _videoQueue;
            _ = Task.Run(() =>
            {
                try
                {
                    var resumed = StudioProject.List(studioRoot).Sum(queue.Resume);
                    if (resumed > 0) PetLog.Write($"工作台：接着查询 {resumed} 个没完成的视频任务");
                }
                catch (Exception e)
                {
                    PetLog.Write($"工作台：恢复视频任务失败: {e.Message}");
                }
            });

#if DEBUG
            if (Environment.GetEnvironmentVariable("ASTRA_PET_STUDIO") == "1")
                _pet.Opened += (_, _) => DispatcherTimer.RunOnce(OpenStudioForDebug, TimeSpan.FromSeconds(1));
#endif
            if (Environment.GetEnvironmentVariable("ASTRA_PET_CONSOLE") == "1")
                _pet.Opened += (_, _) => ShowConsole();
            else
                // 宠物出来后过一会儿，在后台把控制台预热好；用户第一次打开就不用等。
                _pet.Opened += (_, _) => DispatcherTimer.RunOnce(
                    () => EnsureConsole().Warmup(), TimeSpan.FromSeconds(2), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private ConsoleWindow EnsureConsole()
    {
        if (_console is not null) return _console;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var vm = new ConsoleViewModel(_pet!, _companion!, _settings!, _director!,
            Composition!.Platform, Composition.Decoder, Composition.Catalog, Composition.Ffmpeg);
        vm.StudioRequested += ShowStudio;
        var console = new ConsoleWindow(vm);
        PetLog.Write($"创建控制台 {watch.ElapsedMilliseconds}ms");

        // 收起后过一阵释放；重新打开就停表。预热完直接释放。
        console.WarmedUp += TryReleaseConsole;
        console.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (console.IsVisible) _consoleReleaseTimer.Stop();
            else _consoleReleaseTimer.Start();
        };
        console.Closed += (_, _) =>
        {
            if (_console != console) return;
            _console = null;
            _consoleReleaseTimer.Stop();
        };
        return _console = console;
    }

    /// <summary>
    /// 释放收起着的控制台。工作台开过就不释放：工作台拿控制台的 ViewModel 当宿主。
    /// 正在导入动作、安装更新时过一会儿再试。
    /// </summary>
    private void TryReleaseConsole()
    {
        _consoleReleaseTimer.Stop();
        if (_console is not { } console || _studio is not null || console.IsVisible) return;
        if (!console.CanRelease)
        {
            _consoleReleaseTimer.Start();
            return;
        }
        console.Release();
        PetLog.Write("控制台已释放");
        CompactSoon();
    }

    /// <summary>
    /// 释放收起着的工作台。本机上的活（出图、抠像、导出、试用、安装）没做完就过一会儿再试；
    /// 服务端的视频任务不拦着：它们在 App 级的 <see cref="VideoTaskQueue"/> 里接着查询、下载。
    /// 工作台释放后控制台也不再被它拖住，按控制台自己的规则接着释放。
    /// </summary>
    private void TryReleaseStudio()
    {
        _studioReleaseTimer.Stop();
        if (_studio is not { } studio || studio.IsVisible) return;
        if (!studio.CanRelease)
        {
            _studioReleaseTimer.Start();
            return;
        }
        studio.Release();
        PetLog.Write("工作台已释放");
        // 工作台一直开着时控制台释不掉；现在控制台要是也收着，就接着走它的释放（它自己会再等收起的时长）。
        if (_console is { IsVisible: false }) _consoleReleaseTimer.Start();
        CompactSoon();
    }

    /// <summary>
    /// 宠物平时几乎不分配内存，不主动收一次的话，释放掉的界面树要很久以后才被回收、还给系统。
    /// 窗口的原生资源在关闭后的几帧里才放完，稍等一下再收。低频事件，一次几十毫秒的阻塞 GC 可以接受。
    /// </summary>
    private static void CompactSoon() => DispatcherTimer.RunOnce(() =>
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }, TimeSpan.FromSeconds(1), DispatcherPriority.Background);

    /// <summary>角色工作台：要用时建，关掉先隐藏，收起一阵后释放（<see cref="TryReleaseStudio"/>）。试用和安装通过控制台的 ViewModel 换角色。</summary>
    private void ShowStudio()
    {
        if (_studio is null)
        {
            var c = Composition!;
            var console = EnsureConsole();
            StudioViewModel? vm = null;
            var gate = new DeferredGate(() => vm!);
            var engine = new StudioEngine(AvaloniaImageCodec.Instance, gate, _videoQueue!, c.Ffmpeg);
            vm = new StudioViewModel(_settings!, engine, c.StudioRoot, c.Decoder, c.Platform.Paths.CacheDirectory,
                c.Platform.Shell, (IStudioHost)console.DataContext!);
            var studio = _studio = new StudioWindow(vm);
            studio.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty) return;
                if (studio.IsVisible) _studioReleaseTimer.Stop();
                else _studioReleaseTimer.Start();
            };
            studio.Closed += (_, _) =>
            {
                if (_studio != studio) return;
                _studio = null;
                _studioReleaseTimer.Stop();
            };
        }
        _studio.Present();
    }

#if DEBUG
    /// <summary>
    /// 开发用：ASTRA_PET_STUDIO=1 启动后打开工作台。再设 ASTRA_PET_STUDIO_DEMO=&lt;角色目录（如 characters/小t）&gt;
    /// 会用它的参考图和原始视频搭一个演示工程（⑤ 用现成视频代替，不花钱）并打开；
    /// ASTRA_PET_STUDIO_SNAPSHOT=&lt;目录&gt; 等处理完把每一步截图存下来。配合 ASTRA_PET_DATA 指到临时目录，别动真数据。
    /// </summary>
    private bool _studioDebugStarted;

    private async void OpenStudioForDebug()
    {
        if (_studioDebugStarted) return;   // 宠物窗口的 Opened 可能来不止一次
        _studioDebugStarted = true;
        ShowStudio();
        if (Environment.GetEnvironmentVariable("ASTRA_PET_STUDIO_DEMO") is not { Length: > 0 } character) return;
        var vm = (StudioViewModel)_studio!.DataContext!;
        var project = StudioProject.List(vm.Root).FirstOrDefault(p => p.Data.Name == "小t（演示）")
                      ?? await Task.Run(() => StudioDemo.Seed(vm.Engine, vm.Root, character));
        vm.Open(project);
        if (Environment.GetEnvironmentVariable("ASTRA_PET_STUDIO_SNAPSHOT") is { Length: > 0 } dir)
            await _studio.SnapshotAsync(dir);
    }
#endif

    /// <summary>引擎要先有确认框才能建，确认框（ViewModel）又要引擎：中间垫一层。</summary>
    private sealed class DeferredGate(Func<ICostGate> gate) : ICostGate
    {
        public Task<bool> ConfirmAsync(CostEstimate estimate, CancellationToken ct) => gate().ConfirmAsync(estimate, ct);
    }

    private void ShowConsole() => ShowConsole(null);

    private void ShowConsole(string? page)
    {
        var console = EnsureConsole();
        if (page is not null) ((ConsoleViewModel)console.DataContext!).Navigate(page);
        console.Present();
    }

    /// <summary>把设置里的明暗（跟随系统 / 浅色 / 深色）和主色套到 Arc 主题上。</summary>
    private void ApplyTheme(PetSettings settings)
    {
        var appearance = settings.Appearance switch
        {
            "light" => ArcAppearance.Light,
            "dark" => ArcAppearance.Dark,
            _ => ArcAppearance.System,
        };
        if (!Color.TryParse(settings.Accent, out var accent)) accent = Color.Parse(PetSettings.DefaultAccent);
        RequestedThemeVariant = appearance switch
        {
            ArcAppearance.Light => ThemeVariant.Light,
            ArcAppearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        try
        {
            ArcTheme.Apply(t => t.Appearance(appearance).Accent(accent));
        }
        catch (Exception e)
        {
            PetLog.Write($"应用主题失败: {e.Message}");
        }
    }

    private void ShowChat()
    {
        _chat ??= new ChatInputWindow(_pet!, _companion!, _settings!, _companion!.Platform);
        _chat.Present();
    }
}
