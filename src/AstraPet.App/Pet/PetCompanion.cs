using AstraPet.Adapters;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Core.External;
using AstraPet.External;
using AstraPet.Platform;

namespace AstraPet.Pet;

/// <summary>
/// 宠物行为层和界面之间的适配：把宠物窗口的输入转发给 Core 的 <see cref="PetHost"/>（点击、摸头、菜单、提醒、
/// 省电、听歌、更新提醒都在那里），并替它说话（气泡 + 朗读）、开窗口。
/// 对话也在这里：智能体（Core 的 PetAgent）回复后要冒气泡、朗读、播动作。
/// </summary>
public sealed class PetCompanion : IPetVoice, IPetCommands, IExternalPet, IDisposable
{
    private readonly PetWindow _pet;
    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly IPlatform _platform;
    private readonly PetHost _host;

    public PetCompanion(PetWindow pet, PetDirector director, PetSettings settings, IPlatform platform,
        ILlmClient llm, IUpdateFeed updateFeed)
    {
        _pet = pet;
        _director = director;
        _settings = settings;
        _platform = platform;
        Agent = new PetAgent(settings, llm)
        {
            Persona = () => _director.Library.Persona,
            Actions = () => _director.Library.Actions,
            Situation = Situation,
            Usable = a => (_director.Usable?.Invoke(a) ?? true) && _director.CanReach(a),
        };
        Bubble = new BubbleWindow(pet, settings, platform);
        Updates = new UpdateChecker(settings, updateFeed, platform.Identity.Key, platform.Installer.PreferredKinds);

        var input = new PetInput();
        Events = new PetEventBus();
        var context = new PetContext
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
            Events = Events,
            Cursor = () => _pet.CursorOnCanvas(),
            Hotkey = platform.Hotkey,
            Body = pet.Body,
        };
        _host = new PetHost(context);
        Life = _host.Life;
        StatusCard = new StatusCard(pet, Life, platform);
        _food = new FoodSprite(platform);

        _pet.BodyEvent += e =>
        {
            PetLog.Write($"身体：{e.Type}" + (e.Data is { } d ? " " + string.Join(" ", d.Select(p => $"{p.Key}={p.Value}")) : ""));
            Events.Publish(e);
        };
        _pet.HoverStill += () => { if (Life.Enabled) StatusCard.Present(_director.Library.Name); };
        _pet.HoverEnded += StatusCard.Dismiss;
        _pet.DragStarted += StatusCard.Dismiss;
        _pet.FoodOptions = FoodOptions;
        _pet.MenuAvailable = id => id switch
        {
            MenuEntry.Feed or MenuEntry.Status => Life.Enabled,
            MenuEntry.Sleep => _director.Library.Actions.All.Any(a => a.Enabled && a.HasRole(Roles.Sleep)),
            _ => true,
        };

        _pet.Clicked += input.RaiseClicked;
        _pet.DoubleClicked += input.RaiseDoubleClicked;
        _pet.Petted += input.RaisePetted;
        _pet.DragStarted += input.RaiseDragStarted;
        _pet.DragEnded += input.RaiseDragEnded;
        _pet.MenuCommand += input.RaiseMenuCommand;
        _pet.MenuLabel = _host.Menu.LabelOf;
        _pet.MenuChecked = _host.Menu.IsChecked;
        _pet.Opened += (_, _) => _host.Start();
        _pet.Closed += (_, _) => Dispose();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetSettings.LifeEnabled) && !settings.LifeEnabled) StatusCard.Dismiss();
        };

        Agent.Busy += busy => { if (busy) Bubble.Say("…", 0); };
        // 03 §1.6：收到 play_action 就开始播，不等文字；规则 agent-action 把它变成 Agent 优先级的请求。
        Agent.ActionRequested += id => Events.Publish(PetEvents.ChatAction, ("action", id));
        Events.Published += e =>
        {
            if (e.Type is PetEvents.Clicked or PetEvents.Petted or PetEvents.DragEnded)
                _lastInteraction = (e.Type, DateTimeOffset.Now);
            var text = e.Type switch
            {
                PetEvents.Fed => $"用户刚才喂了你{e["name"]}" + (e["liked"] == "liked" ? "（你最喜欢的）" : e["liked"] == "disliked" ? "（你不太喜欢）" : ""),
                PetEvents.Thrown => "用户刚才把你甩了出去",
                PetEvents.PlayEnded => "用户刚才陪你玩了一会儿",
                PetEvents.Woken => "用户刚才把你叫醒了",
                _ => null,
            };
            if (text is not null) _lastLifeEvent = (text, DateTimeOffset.Now);
        };
        _external = new LocalHttpServer(new ExternalApi(this, AvaloniaUiDispatcher.Instance).HandleAsync);
        _pet.Opened += (_, _) => UpdateExternal();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PetSettings.ExternalApi) or nameof(PetSettings.ExternalPort)) UpdateExternal();
        };

        Agent.Remembered += added => Say("我记住啦：" + (added[0].StartsWith("用户") ? "你" + added[0][2..] : added[0]), speak: false);
    }

    public PetAgent Agent { get; }

    /// <summary>宠物体系：数值、喂食、亲密度（design/pet-life）。</summary>
    public PetLife Life { get; }
    public StatusCard StatusCard { get; }
    public PlayBehavior Play => _host.Play;
    public SummonBehavior Summon => _host.Summon;

    private readonly FoodSprite _food;

    /// <summary>右键菜单「喂食」子菜单：每个食物一项，标出喜欢的、冷却和零食数量。</summary>
    private IReadOnlyList<(string Id, string Label, bool Enabled)> FoodOptions()
    {
        var diet = Life.Profile.Diet;
        return Foods.All.Select(f =>
        {
            var (available, note) = Life.FoodStatus(f);
            var star = diet.Likes.Contains(f.Id) ? " ★" : "";
            return (f.Id, $"{f.Icon} {f.Name}{star}" + (note.Length > 0 ? $"（{note}）" : ""), available);
        }).ToList();
    }

    /// <summary>
    /// 喂食（design/pet-life/03 §2）：食物图标先拿在光标上，移到宠物身上停一下就喂（方案 B）；
    /// 6 秒没喂到、或者读不到光标，就从光标处飞到嘴边（方案 A）。吃不下时半路掉下去。
    /// </summary>
    public void Feed(string foodId)
    {
        if (Foods.Find(foodId) is not { } food || !Life.Enabled) return;
        var body = _pet.Body;
        (double X, double Y)? Cursor() => _pet.CursorOnCanvas() is var (cx, cy) ? body.CanvasToDesktop(cx, cy) : null;
        bool OverPet()
        {
            if (_pet.CursorOnCanvas() is not var (cx, cy)) return false;
            var b = Life.Profile.Body;
            var (left, right) = _director.Mirrored ? (PetClip.Width - b.X1, PetClip.Width - b.X0) : (b.X0, b.X1);
            return cx >= left && cx <= right && cy >= b.Y0 && cy <= b.Y1;
        }
        void FlyFrom((double X, double Y)? start)
        {
            var b = Life.Profile.Body;
            var mouthX = _director.Mirrored ? PetClip.Width - b.MouthX : b.MouthX;
            var to = body.CanvasToDesktop(mouthX, b.MouthY);
            var from = start ?? (to.X, to.Y - 120 * _pet.DesktopScaling);
            _food.Fly(food, from, to, Life.Data.Satiety >= LifeTuning.FullAt, () => Life.Feed(foodId));
        }
        if (Cursor() is null)
        {
            FlyFrom(null);
            return;
        }
        Bubble.Say($"把{food.Name}拿过来喂我～", 3);
        _food.Hold(food, Cursor, OverPet, TimeSpan.FromSeconds(6), () => Life.Feed(foodId), () => FlyFrom(Cursor()));
    }

    void IPetCommands.Feed(PetLife life, string foodId) => Feed(foodId);

    // ───── 外部控制（03 §2） ─────

    private readonly LocalHttpServer _external;

    /// <summary>本地接口的状态，控制台显示用：null 表示没开；否则是「正在监听 …」或出错原因。</summary>
    public string? ExternalStatus { get; private set; }
    public event Action? ExternalStatusChanged;

    PetDirector IExternalPet.Director => _director;
    PetSettings IExternalPet.Settings => _settings;
    PetEventBus IExternalPet.Events => Events;
    PetLife? IExternalPet.Life => Life;

    /// <summary>按设置开关本地 HTTP 接口。打开时没有令牌就生成一个。</summary>
    private void UpdateExternal()
    {
        _external.Stop();
        ExternalStatus = null;
        if (_settings.ExternalApi)
        {
            if (_settings.ExternalToken.Length == 0) _settings.ExternalToken = ExternalApi.NewToken();
            try
            {
                _external.Start(_settings.ExternalPort);
                ExternalStatus = $"正在监听 http://127.0.0.1:{_external.Port}";
                PetLog.Write($"外部控制接口已打开: 127.0.0.1:{_external.Port}");
            }
            catch (Exception e)
            {
                ExternalStatus = $"打不开端口 {_settings.ExternalPort}：{e.Message}（换一个端口试试）";
                PetLog.Write($"外部控制接口打开失败: {e.Message}");
            }
        }
        ExternalStatusChanged?.Invoke();
    }

    private (string Type, DateTimeOffset At)? _lastInteraction;

    /// <summary>系统提示词【现在】里时间之后的几行（03 §3.5）。不写应用名、窗口标题这类隐私信息。</summary>
    private IReadOnlyList<string> Situation()
    {
        var lines = new List<string>();
        var pomodoro = _host.Pomodoro;
        if (pomodoro.EndsAt is { } end)
        {
            var left = Math.Max(1, (int)Math.Ceiling((end - DateTimeOffset.Now).TotalMinutes));
            lines.Add(pomodoro.Resting
                ? $"番茄钟：休息中，还剩 {left} 分钟"
                : $"番茄钟：专注中，还剩 {left} 分钟（专注时少打扰，回复更短一点）");
        }
        if (SystemPrompt.ContinuousUse(_host.Reminders.ContinuousUse) is { } usage) lines.Add(usage);
        if (_settings.MusicPlaying) lines.Add("正在放音乐");
        lines.AddRange(LifeSituation());
        if (_lastInteraction is { } last && DateTimeOffset.Now - last.At < TimeSpan.FromMinutes(1))
            lines.Add(last.Type switch
            {
                PetEvents.Petted => "用户刚才摸了摸你的头",
                PetEvents.DragEnded => "用户刚才把你拖到了别的地方",
                _ => "用户刚才点了点你",
            });
        return lines;
    }

    /// <summary>宠物体系写进情境层的几行（design/pet-life/06 §4.1）：数值写成档位，括号里附一次数字。</summary>
    private IEnumerable<string> LifeSituation()
    {
        if (!Life.Enabled) yield break;
        var d = Life.Data;
        yield return $"你现在：{LifeRules.SatietyText(d.Satiety)}（饱腹 {d.Satiety:0}/100），{LifeRules.EnergyText(d.Energy)}，心情{LifeRules.MoodText(d.Mood)}";
        var level = Life.Level;
        var relation = $"你和用户：认识 {Life.DaysTogether} 天，是{LifeRules.LevelName(level)}（亲密度 {level}）";
        if (level >= 5 && _settings.UserCallName.Length > 0) relation += $"；用户希望你叫 TA「{_settings.UserCallName}」";
        yield return relation;
        if (Life.Traits.Describe() is { Length: > 0 } traits) yield return $"你的性格：{traits}";
        if (d.Nickname is { } nick) yield return $"用户给你起的昵称：{nick}";
        if (_director.Stance == Stances.Wall) yield return "你正扒在屏幕边上";
        else if (_director.Sleeping) yield return "你刚才在睡觉";
        if (_lastLifeEvent is { } last && DateTimeOffset.Now - last.At < TimeSpan.FromMinutes(1)) yield return last.Text;
    }

    private (string Text, DateTimeOffset At)? _lastLifeEvent;

    /// <summary>事件总线：触发规则、控制台、以后的外部 Agent 都从这里收发事件。</summary>
    public PetEventBus Events { get; }
    public BubbleWindow Bubble { get; }
    public UpdateChecker Updates { get; }
    public PomodoroBehavior Pomodoro => _host.Pomodoro;

    /// <summary>检查更新。手动检查（控制台）传 false，结果在控制台里看，不冒气泡。</summary>
    public Task<UpdateInfo?> CheckForUpdateAsync(bool automatic) => _host.UpdateNotifier.CheckAsync(automatic);

    /// <summary>需要打开对话框（双击、菜单）。</summary>
    public event Action? ChatRequested;

    /// <summary>需要打开控制台（菜单）。</summary>
    public event Action<string?>? ConsoleRequested;

    public void RequestChat() => ChatRequested?.Invoke();

    // ───── IPetCommands ─────

    void IPetCommands.OpenChat() => ChatRequested?.Invoke();
    void IPetCommands.OpenConsole(string? page) => ConsoleRequested?.Invoke(page);
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
            Events.Publish(PetEvents.ChatThinking);
            var reply = await Agent.AskAsync(text);
            Bubble.Say(reply.Text);
            if (_settings.SpeakReplies) Speak(reply.Text);
            // Agent 调过 play_action 时 acted 为 true，规则 reply 就不再补一个随机反应。
            Events.Publish(PetEvents.ChatReplied, ("acted", reply.Acted ? "true" : "false"));
            return reply.Text;
        }
        catch (Exception e)
        {
            Events.Publish(PetEvents.ChatFailed);
            PetLog.Write($"对话失败: {e.Message}");
            Bubble.Say("呜…我好像连不上大脑了，去控制台「智能」页看看吧");
            throw;
        }
    }

    public void Dispose()
    {
        _external.Dispose();
        _host.Dispose();
        StatusCard.Close();
        _food.Close();
        _platform.Speech.Stop();
        Bubble.Close();
    }
}
