using AstraPet.ControlPanel;
using AstraPet.Core;
using AstraPet.Pet;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AstraPet;

/// <summary>
/// 组装各层：设置（从磁盘读）→ 状态机 → 宠物窗口（主窗口）→ 行为层（气泡、智能体、提醒）；
/// 控制台和对话框按需创建。设置变化后 0.5 秒存盘一次。
/// </summary>
public partial class App : Application
{
    /// <summary>Program 在启动 Avalonia 之前定好内置角色的素材目录和 ffmpeg，放这里。</summary>
    public static string BuiltInVideosDir { get; set; } = "";
    public static string Ffmpeg { get; set; } = "";

    private PetSettings? _settings;
    private PetDirector? _director;
    private PetWindow? _pet;
    private PetCompanion? _companion;
    private ConsoleWindow? _console;
    private ChatWindow? _chat;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && BuiltInVideosDir.Length > 0)
        {
            var settings = _settings = SettingsStore.Load();
            PetLog.Write($"设置文件: {SettingsStore.FilePath}");

            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsStore.Save(settings); };
            settings.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };

            var library = PetCatalog.LoadSelected(settings, BuiltInVideosDir, Ffmpeg);
            PetLog.Write($"加载完成: {library.ClipCount} 段, 动作 {library[PetLibrary.Click].Count} 个, " +
                         $"移动 {library[PetLibrary.Move].Count} 个, 唱歌 {library[PetLibrary.Sing].Count} 个");

            var director = _director = new PetDirector(library, settings);
            _pet = new PetWindow(director, settings);
            _companion = new PetCompanion(_pet, director, settings);
            _companion.ConsoleRequested += ShowConsole;
            _companion.ChatRequested += ShowChat;

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = _pet;
            desktop.ShutdownRequested += (_, _) => { _saveTimer.Stop(); SettingsStore.Save(settings); };

            if (Environment.GetEnvironmentVariable("ASTRA_PET_CONSOLE") == "1")
                _pet.Opened += (_, _) => ShowConsole();
            else
                // 宠物出来后过一会儿，在后台把控制台预热好；用户第一次打开就不用等。
                _pet.Opened += (_, _) => DispatcherTimer.RunOnce(
                    () => EnsureConsole().Warmup(), TimeSpan.FromSeconds(2), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private ConsoleWindow EnsureConsole() =>
        _console ??= new ConsoleWindow(new ConsoleViewModel(_pet!, _companion!, _settings!, _director!, BuiltInVideosDir, Ffmpeg));

    private void ShowConsole() => EnsureConsole().Present();

    private void ShowChat()
    {
        _chat ??= new ChatWindow(new ChatViewModel(_companion!, _settings!));
        _chat.Show();
        _chat.Activate();
    }
}
