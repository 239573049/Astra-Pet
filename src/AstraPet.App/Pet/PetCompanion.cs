using AstraPet.Adapters;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Core.External;
using AstraPet.External;
using AstraPet.Platform;
using Avalonia.Threading;

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
            PackPrompt = () => _director.Library.Prompt,
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
        _pet.Clicked += input.RaiseClicked;
        _pet.DoubleClicked += input.RaiseDoubleClicked;
        _pet.Petted += input.RaisePetted;
        _pet.DragStarted += input.RaiseDragStarted;
        _pet.DragEnded += input.RaiseDragEnded;
        _pet.MenuCommand += input.RaiseMenuCommand;
        _pet.MenuRows = _host.Menu.Rows;
        _pet.Opened += (_, _) => _host.Start();
        _pet.Closed += (_, _) => Dispose();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetSettings.LifeEnabled) && !settings.LifeEnabled) StatusCard.Dismiss();
        };

        Agent.Busy += busy => { if (busy) Bubble.Say("…", 0, agent: true); };
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
        Agents = new AgentSessions(Events, settings, Notify);
        _agentSweep.Tick += (_, _) => Agents.Sweep();
        _agentSweep.Start();
        // 更新、移动过程序后，已绑定的 hook 还指着旧路径：启动时悄悄改过来（读写别人的配置文件，不占 UI 线程）。
        if (Environment.ProcessPath is { } exe) _ = Task.Run(() => AgentIntegrations.RepairStale(exe));
        _external = new LocalHttpServer(new ExternalApi(this, AvaloniaUiDispatcher.Instance).HandleAsync);
        _pet.Opened += (_, _) => UpdateExternal();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PetSettings.ExternalApi) or nameof(PetSettings.ExternalPort)) UpdateExternal();
        };

        // 记忆是在回复之后提取的，正在聊的时候冒出来会盖掉回复，所以等聊完再说。
        Agent.Remembered += added => ((IPetVoice)this).SayLater("我记住啦：" + (added[0].StartsWith("用户") ? "你" + added[0][2..] : added[0]), speak: false);
        _pendingTimer.Tick += (_, _) => FlushPending();

        // 聊天记录：启动时接上这只宠物上次的对话；每次变化存盘；换宠物换成那只的记录。
        LoadConversation();
        Agent.HistoryChanged += SaveConversation;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetSettings.PetId)) LoadConversation();
            if (e.PropertyName == nameof(PetSettings.PersistChat))
            {
                if (settings.PersistChat) SaveConversation();
                else ConversationStore.DeleteAll(SettingsStore.Directory);
            }
        };
    }

    public PetAgent Agent { get; }

    // ───── 聊天记录持久化 ─────

    private string ConversationPath => ConversationStore.PathFor(SettingsStore.Directory, _settings.PetId);

    private void LoadConversation()
    {
        var snapshot = _settings.PersistChat ? ConversationStore.Load(ConversationPath) : null;
        Agent.Restore(snapshot);
        if (snapshot is { IsEmpty: false })
            PetLog.Write($"接上之前的对话：{snapshot.Turns.Count} 轮" + (snapshot.Summary is null ? "" : "，有摘要"));
    }

    private void SaveConversation()
    {
        if (_settings.PersistChat) ConversationStore.Save(ConversationPath, Agent.Snapshot());
    }
    public IPlatform Platform => _platform;

    /// <summary>宠物体系：数值、喂食、亲密度（design/pet-life）。</summary>
    public PetLife Life { get; }
    public StatusCard StatusCard { get; }
    public PlayBehavior Play => _host.Play;
    public SummonBehavior Summon => _host.Summon;
    /// <summary>右键菜单（控制台预览也用它，和真实菜单一致）。</summary>
    public MenuBehavior Menu => _host.Menu;

    private readonly FoodSprite _food;

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
    AgentSessions? IExternalPet.Agents => Agents;

    /// <summary>编程智能体的会话聚合（design/agent-hooks §4）。</summary>
    public AgentSessions Agents { get; }
    private readonly DispatcherTimer _agentSweep = new() { Interval = TimeSpan.FromMinutes(1) };
    /// <summary>外部程序让宠物说话：和提醒一样，智能体占着气泡或正在聊天时先攒着，空了再说。</summary>
    void IExternalPet.Say(string text, bool speak) => ((IPetVoice)this).SayLater(text, speak);

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

    // ───── 聊天时不打断 ─────

    /// <summary>最后一次聊天（发出或收到回复、关掉输入框）之后这么久内还算在聊，给用户看完回复、想下一句的时间。</summary>
    public static readonly TimeSpan ConversationLinger = TimeSpan.FromSeconds(90);
    /// <summary>攒着的话超过这么久就不说了（过时的报时、提醒没意义）。</summary>
    public static readonly TimeSpan PendingExpiry = TimeSpan.FromMinutes(10);
    /// <summary>用户托付的定时提醒攒得更久：聊得再久，聊完也要说到。</summary>
    public static readonly TimeSpan ImportantExpiry = TimeSpan.FromHours(3);
    private const int MaxPending = 3;

    private bool _chatOpen;
    private int _userTalking;
    private DateTimeOffset _lastChatAt = DateTimeOffset.MinValue;

    /// <summary>
    /// 攒着等聊完再说的话：一句固定台词，或一个交给智能体的起因（Cue）加兜底台词。
    /// <see cref="Important"/>：定时提醒，攒得更久，攒多了先挤掉别的。
    /// </summary>
    /// <see cref="Urgent"/>：编程智能体的通知，不等「刚聊完」的 90 秒，只等输入框关上、回复收起。
    private sealed record Pending(string Text, bool Speak, string? Cue, DateTimeOffset At, bool Important = false, bool Urgent = false)
    {
        public bool Expired => DateTimeOffset.Now - At > (Important ? ImportantExpiry : PendingExpiry);
    }
    private readonly List<Pending> _pending = new();
    /// <summary>聊完后一句一句说：每次都等上一句气泡收起来再说下一句，免得互相盖掉。</summary>
    private readonly DispatcherTimer _pendingTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>输入框开着（由输入框设置）。关掉时也算刚聊过，再等一会儿才说攒着的话。</summary>
    public bool ChatOpen
    {
        get => _chatOpen;
        set
        {
            if (_chatOpen == value) return;
            _chatOpen = value;
            _lastChatAt = DateTimeOffset.Now;
        }
    }

    /// <summary>用户正在和宠物聊天：输入框开着、在等回复、或刚聊完不久。这时别的气泡不打断。</summary>
    public bool InConversation =>
        _chatOpen || _userTalking > 0 || DateTimeOffset.Now - _lastChatAt < ConversationLinger;

    /// <summary>
    /// 智能体占着气泡：正在请求（聊天或主动开口，可能正流式冒字），或它说的话还没收起。
    /// 这时自发的气泡（触发规则的随口反应、提醒、攒着的话）不能盖上去，否则用户还没看完就被冲掉了。
    /// </summary>
    private bool AgentHolding => _talking > 0 || Bubble.ShowingAgent;

    void IPetVoice.SayLater(string text, bool speak)
    {
        if (InConversation || AgentHolding) Defer(new Pending(text, speak, null, DateTimeOffset.Now));
        else Say(text, speak);
    }

    /// <summary>
    /// 编程智能体的通知（design/agent-hooks §4.4）：「在等你确认」这类话要及时，不等刚聊完的那 90 秒；
    /// 只在用户正在打字、回复还在路上、智能体的话还挂着时先攒着。
    /// </summary>
    private bool NotifyBlocked => _chatOpen || _userTalking > 0 || AgentHolding;

    public void Notify(string text, bool speak)
    {
        if (NotifyBlocked) Defer(new Pending(text, speak, null, DateTimeOffset.Now, Urgent: true));
        else Say(text, speak);
    }

    void IPetVoice.React(string text, bool speak)
    {
        if (!InConversation && !AgentHolding) Say(text, speak);
    }

    private void Defer(Pending item)
    {
        _pending.RemoveAll(p => p.Text == item.Text && p.Cue == item.Cue);
        _pending.Add(item);
        while (_pending.Count > MaxPending)
        {
            var drop = _pending.FindIndex(p => !p.Important);
            _pending.RemoveAt(drop >= 0 ? drop : 0);
        }
        PetLog.Write($"气泡被占着，先不说：{item.Cue ?? item.Text}");
        _pendingTimer.Start();
    }

    private void FlushPending()
    {
        _pending.RemoveAll(p => p.Expired);
        if (_pending.Count == 0)
        {
            _pendingTimer.Stop();
            return;
        }
        // 任何气泡还挂着都等它收起（上一句攒着的话最长显示 15 秒，固定间隔会把它盖掉）。
        if (Bubble.Showing) return;
        // 智能体的通知先说，它们只等最基本的条件；其他的还要等聊完。
        var index = _pending.FindIndex(p => p.Urgent);
        if (index >= 0 ? NotifyBlocked : InConversation || AgentHolding)
            return;
        if (index < 0) index = 0;
        var next = _pending[index];
        _pending.RemoveAt(index);
        if (next.Cue is null || !SpeakUp(next.Cue, next.Text, next.Important)) Say(next.Text, next.Speak);
    }

    /// <summary>正在请求智能体（用户发的或主动开口的），这时不再主动开口。</summary>
    private int _talking;

    /// <summary>
    /// 主动开口（搭话、问候、提醒，见 <see cref="IPetCommands.SpeakUp"/>）：没配模型返回 false。
    /// 说的话和用户的聊天进同一个上下文。<paramref name="fallback"/> 不为 null 时这句一定要说到：
    /// 模型没说或出错就说 fallback。出错只写日志，不在气泡里抱怨。
    /// 用户正在聊天时：一定要说的先攒着，聊完再说；随口搭话直接不说。
    /// </summary>
    bool IPetCommands.SpeakUp(string cue, string? fallback) => SpeakUp(cue, fallback, important: false);

    /// <summary>定时提醒到点：同样请智能体开口，只是用户正在聊天时攒得更久（<see cref="ImportantExpiry"/>）。</summary>
    bool IPetCommands.Remind(string cue, string fallback) => SpeakUp(cue, fallback, important: true);

    private bool SpeakUp(string cue, string? fallback, bool important)
    {
        if (!Agent.IsConfigured) return false;
        if (InConversation || AgentHolding)
        {
            if (fallback is not null) Defer(new Pending(fallback, true, cue, DateTimeOffset.Now, important));
            return true;
        }
        _ = SpeakUpAsync(cue, fallback, important);
        return true;
    }

    private async Task SpeakUpAsync(string cue, string? fallback, bool important)
    {
        _talking++;
        try
        {
            var shown = false;
            var reply = await Agent.SpeakUpAsync(cue, mustSpeak: fallback is not null,
                onPartial: partial =>
                {
                    // 用户在它想的时候开始聊了：别把半截话冒出来盖掉聊天。
                    if (PetAgent.MaybeSilent(partial) || InConversation) return;
                    shown = true;
                    Bubble.Say(partial, 0, agent: true);
                });
            var text = reply?.Text is { Length: > 0 } t ? t : fallback;
            if (InConversation)
            {
                // 回来时用户已经在聊了：一定要说的攒着（用已经生成好的话），随口搭话就算了。
                if (fallback is not null && text is not null) Defer(new Pending(text, true, null, DateTimeOffset.Now, important));
                return;
            }
            if (text is null && shown) Bubble.Dismiss();   // 说了半截又决定不说
            if (text is not null)
            {
                Bubble.Say(text, agent: true);
                if (_settings.SpeakReplies || fallback is not null && _settings.SpeakReminders) Speak(text);
            }
            if (reply is not null) Events.Publish(PetEvents.ChatReplied, ("acted", reply.Acted ? "true" : "false"));
        }
        catch (Exception e)
        {
            PetLog.Write($"主动开口失败: {e.Message}");
            if (fallback is not null) Say(fallback);
        }
        finally
        {
            _talking--;
        }
    }

    /// <summary>发给智能体，回复显示在气泡里并按设置朗读。出错时气泡里说明原因，并把异常抛给调用方。</summary>
    public async Task<string> ChatAsync(string text)
    {
        _talking++;
        _userTalking++;
        _lastChatAt = DateTimeOffset.Now;
        try
        {
            Events.Publish(PetEvents.ChatThinking);
            // 流式：边收边更新气泡（一直显示），收完再按字数定收起的时间。
            var reply = await Agent.AskAsync(text, partial => Bubble.Say(partial, 0, agent: true));
            Bubble.Say(reply.Text, agent: true);
            if (_settings.SpeakReplies) Speak(reply.Text);
            // Agent 调过 play_action 时 acted 为 true，规则 reply 就不再补一个随机反应。
            Events.Publish(PetEvents.ChatReplied, ("acted", reply.Acted ? "true" : "false"));
            return reply.Text;
        }
        catch (Exception e)
        {
            Events.Publish(PetEvents.ChatFailed);
            PetLog.Write($"对话失败: {e.Message}");
            Bubble.Say("呜…我好像连不上大脑了，去控制台「智能」页看看吧", agent: true);
            throw;
        }
        finally
        {
            _talking--;
            _userTalking--;
            _lastChatAt = DateTimeOffset.Now;
        }
    }

    public void Dispose()
    {
        _pendingTimer.Stop();
        _agentSweep.Stop();
        _external.Dispose();
        _host.Dispose();
        StatusCard.Close();
        _food.Close();
        _platform.Speech.Stop();
        Bubble.Close();
    }
}
