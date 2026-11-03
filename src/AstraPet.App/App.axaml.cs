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
    private ChatWindow? _chat;
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

            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsStore.Save(settings); };
            settings.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };

            _pet = new PetWindow(director, settings, c.Platform);
            _companion = new PetCompanion(_pet, director, settings, c.Platform, c.Llm, c.UpdateFeed);
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
        _console ??= new ConsoleWindow(new ConsoleViewModel(_pet!, _companion!, _settings!, _director!,
            Composition!.Platform, Composition.Decoder, Composition.BuiltInVideosDir, Composition.Ffmpeg));

    private void ShowConsole() => EnsureConsole().Present();

    private void ShowChat()
    {
        _chat ??= new ChatWindow(new ChatViewModel(_companion!, _settings!));
        _chat.Show();
        _chat.Activate();
    }
}
