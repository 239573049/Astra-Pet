using AstraPet.Adapters;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Platform;

namespace AstraPet.Pet;

/// <summary>
/// 宠物行为层和界面之间的适配：把宠物窗口的输入转发给 Core 的 <see cref="PetHost"/>（点击、摸头、菜单、提醒、
/// 省电、听歌、更新提醒都在那里），并替它说话（气泡 + 朗读）、开窗口。
/// 对话还留在这里，因为智能体在 Infrastructure（见 design/04-migration.md 第 3 步）。
/// </summary>
public sealed class PetCompanion : IPetVoice, IPetCommands, IDisposable
{
    private readonly PetWindow _pet;
    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly IPlatform _platform;
    private readonly PetHost _host;

    public PetCompanion(PetWindow pet, PetDirector director, PetSettings settings, IPlatform platform)
    {
        _pet = pet;
        _director = director;
        _settings = settings;
        _platform = platform;
        Agent = new PetAgent(settings);
        Bubble = new BubbleWindow(pet, settings, platform);
        Updates = new UpdateChecker(settings, platform.Identity.Key, platform.Installer.PreferredKinds);

        var input = new PetInput();
        _host = new PetHost(new PetContext
        {
            Settings = settings,
            Director = director,
            Input = input,
            Voice = this,
            Commands = this,
            Monitor = platform.Monitor,
            Capabilities = platform.Capabilities,
            Updates = Updates,
            Time = TimeProvider.System,
            Ui = AvaloniaUiDispatcher.Instance,
        });

        _pet.Clicked += input.RaiseClicked;
        _pet.DoubleClicked += input.RaiseDoubleClicked;
        _pet.Petted += input.RaisePetted;
        _pet.MenuCommand += input.RaiseMenuCommand;
        _pet.MenuLabel = _host.Menu.LabelOf;
        _pet.Opened += (_, _) => _host.Start();
        _pet.Closed += (_, _) => Dispose();

        Agent.Busy += busy => { if (busy) Bubble.Say("…", 0); };
        Agent.Remembered += added => Say("我记住啦：" + (added[0].StartsWith("用户") ? "你" + added[0][2..] : added[0]), speak: false);
    }

    public PetAgent Agent { get; }
    public BubbleWindow Bubble { get; }
    public UpdateChecker Updates { get; }
    public PomodoroBehavior Pomodoro => _host.Pomodoro;

    /// <summary>检查更新。手动检查（控制台）传 false，结果在控制台里看，不冒气泡。</summary>
    public Task<UpdateInfo?> CheckForUpdateAsync(bool automatic) => _host.UpdateNotifier.CheckAsync(automatic);

    /// <summary>需要打开对话框（双击、菜单）。</summary>
    public event Action? ChatRequested;

    /// <summary>需要打开控制台（菜单）。</summary>
    public event Action? ConsoleRequested;

    public void RequestChat() => ChatRequested?.Invoke();

    // ───── IPetCommands ─────

    void IPetCommands.OpenChat() => ChatRequested?.Invoke();
    void IPetCommands.OpenConsole() => ConsoleRequested?.Invoke();
    void IPetCommands.ResetPosition() => _pet.ResetPosition();

    // ───── 说话 / 对话 ─────

    /// <summary>头顶冒一句话；<paramref name="speak"/> 为真且开了朗读提醒时同时念出来。</summary>
    public void Say(string text, bool speak = true)
    {
        Bubble.Say(text);
        if (speak && _settings.SpeakReminders) Speak(text);
    }

    public void Speak(string text) =>
        _platform.Speech.Speak(text, new SpeechOptions(_settings.Voice, _settings.SpeechRate, _settings.SpeechVolume));

    public void StopSpeaking() => _platform.Speech.Stop();

    /// <summary>发给智能体，回复显示在气泡里并按设置朗读。出错时气泡里说明原因，并把异常抛给调用方。</summary>
    public async Task<string> ChatAsync(string text)
    {
        try
        {
            var reply = await Agent.AskAsync(text);
            Bubble.Say(reply);
            if (_settings.SpeakReplies) Speak(reply);
            _director.Play(PetLibrary.Click, fromUser: true);
            return reply;
        }
        catch (Exception e)
        {
            PetLog.Write($"对话失败: {e.Message}");
            Bubble.Say("呜…我好像连不上大脑了，去控制台「智能」页看看吧");
            throw;
        }
    }

    public void Dispose()
    {
        _host.Dispose();
        _platform.Speech.Stop();
        Bubble.Close();
    }
}
