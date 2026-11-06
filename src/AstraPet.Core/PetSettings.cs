using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;
using AstraPet.Core.Studio;

namespace AstraPet.Core;

/// <summary>
/// 全部用户设置，按控制台的七个页面分组。宠物窗口、智能体、提醒服务读它、监听它变化，控制台改它，
/// 彼此不直接引用。标了 <see cref="JsonIgnoreAttribute"/> 的是运行时状态，不存盘。
/// 只在 UI 线程上读写；存盘见 <see cref="SettingsStore"/>。
/// </summary>
public sealed class PetSettings : Observable
{
    public const double MinScale = 0.25;
    public const double MaxScale = 1.0;

    public PetSettings()
    {
        Providers.CollectionChanged += OnCollectionChanged;
        Memories.CollectionChanged += OnCollectionChanged;
        Reminders.CollectionChanged += OnCollectionChanged;
        MenuItems.CollectionChanged += OnCollectionChanged;
        MediaProviders.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>任何设置（含列表里的条目）变了，存盘用。</summary>
    public event Action? Changed;

    // ───── 常规 ─────

    private bool _autoStart;
    private bool _topmost = true;
    private bool _hideInFullscreen = true;
    private bool _allowCapture = true;
    private bool _clickThrough = true;

    /// <summary>登录系统时自动启动。实际开关由平台的 IAutoStart 落到系统里。</summary>
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    /// <summary>其他应用全屏时不显示宠物。哪些平台支持见 PlatformCapabilities.OverFullscreen。</summary>
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }
    /// <summary>允许 OBS 等录屏软件捕获宠物窗口。关掉后录屏里看不到宠物。</summary>
    public bool AllowCapture { get => _allowCapture; set => Set(ref _allowCapture, value); }
    /// <summary>透明像素放行鼠标。哪些平台支持见 PlatformCapabilities.ClickThrough。</summary>
    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }

    // ───── 外观 ─────

    public const string DefaultAccent = "#32AA7D";

    private string _appearance = "system";
    private string _accent = DefaultAccent;

    /// <summary>控制台、对话框、右键菜单的明暗：system 跟随系统，light、dark 固定。</summary>
    public string Appearance
    {
        get => _appearance;
        set => Set(ref _appearance, value is "light" or "dark" ? value : "system");
    }

    /// <summary>主色，#RRGGBB。默认是宠物的绿。</summary>
    public string Accent
    {
        get => _accent;
        set => Set(ref _accent, value is { Length: 7 } && value[0] == '#' ? value.ToUpperInvariant() : DefaultAccent);
    }

    // ───── 桌宠 ─────

    private string _petId = "";
    private double _scale = 0.5;
    private double _bubbleFontSize = 13;
    private double _opacity = 1;
    private double _speed = 1;
    private int _actionInterval;
    private bool _powerSaving = true;
    private int _powerSavingMinutes = 5;
    private bool _stayStill;
    private bool _musicDetect;

    /// <summary>选的角色，见 <see cref="PetEntry.Id"/>：空串是默认角色（小t），builtin:&lt;id&gt; 是其他内置角色，其余是用户角色目录下的文件夹名或角色包文件名。</summary>
    public string PetId { get => _petId; set => Set(ref _petId, value ?? ""); }
    /// <summary>显示缩放，相对素材原寸 640x360。</summary>
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, MinScale, MaxScale)); }
    public double BubbleFontSize { get => _bubbleFontSize; set => Set(ref _bubbleFontSize, Math.Clamp(value, 10, 24)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.2, 1)); }
    /// <summary>动画播放速度倍率。</summary>
    public double Speed { get => _speed; set => Set(ref _speed, Math.Clamp(value, 0.5, 2)); }
    /// <summary>两次自动动作之间至少待机多少秒。0 表示不限制。</summary>
    public int ActionInterval { get => _actionInterval; set => Set(ref _actionInterval, Math.Clamp(value, 0, 600)); }
    /// <summary>一段时间没有键鼠操作时只待机、降低帧率。</summary>
    public bool PowerSaving { get => _powerSaving; set => Set(ref _powerSaving, value); }
    public int PowerSavingMinutes { get => _powerSavingMinutes; set => Set(ref _powerSavingMinutes, Math.Clamp(value, 1, 60)); }
    /// <summary>不移动：随机链里不再抽移动动画。</summary>
    public bool StayStill { get => _stayStill; set => Set(ref _stayStill, value); }
    /// <summary>检测到在放音乐时改播唱歌动画。</summary>
    public bool MusicDetect { get => _musicDetect; set => Set(ref _musicDetect, value); }

    // ───── 互动 ─────

    private bool _clickReact = true;
    private bool _clickBubble = true;
    private bool _doubleClickChat = true;
    private bool _dragEnabled = true;
    private bool _petting = true;
    private bool _proactiveChat = true;
    private int _proactiveMinutes = 30;
    private string _clickLines = "干嘛戳我啦～\n嘿嘿，被你发现了\n今天也要加油哦！\n我在这儿陪着你呢\n别闹，我在想事情";

    /// <summary>单击播放点击回应动作。</summary>
    public bool ClickReact { get => _clickReact; set => Set(ref _clickReact, value); }
    /// <summary>单击时随机说一句 <see cref="ClickLines"/>。</summary>
    public bool ClickBubble { get => _clickBubble; set => Set(ref _clickBubble, value); }
    public bool DoubleClickChat { get => _doubleClickChat; set => Set(ref _doubleClickChat, value); }
    public bool DragEnabled { get => _dragEnabled; set => Set(ref _dragEnabled, value); }
    /// <summary>摸头：鼠标在角色身上停留 2 秒触发一次回应。</summary>
    public bool Petting { get => _petting; set => Set(ref _petting, value); }
    /// <summary>
    /// 主动搭话：用户在电脑前、一阵子没理宠物时，宠物会按自己的性格（角色提示词）找用户说句话；用户离开一阵回来时也会打个招呼。
    /// 要先配好模型；宠物也可以选择不说（高冷的性格开口更少）。
    /// </summary>
    public bool ProactiveChat { get => _proactiveChat; set => Set(ref _proactiveChat, value); }
    /// <summary>主动搭话的平均间隔（分钟），每次在 0.7～1.5 倍之间随机。</summary>
    public int ProactiveMinutes { get => _proactiveMinutes; set => Set(ref _proactiveMinutes, Math.Clamp(value, 5, 180)); }
    /// <summary>点击台词，一行一句。</summary>
    public string ClickLines { get => _clickLines; set => Set(ref _clickLines, value); }

    private List<TriggerRule> _triggers = new();

    /// <summary>
    /// 用户的全局触发规则（和具体宠物无关，只引用 role:）。和内置规则同 id 时覆盖内置的。整体替换，不原地改。
    /// 只对某只宠物生效的规则在它的覆盖层里（<see cref="ActionOverlay"/>）。
    /// </summary>
    public List<TriggerRule> Triggers { get => _triggers; set => Set(ref _triggers, value ?? new()); }

    // ───── 宠物体系（design/pet-life） ─────

    public const string PlacementGround = "ground";
    public const string PlacementFree = "free";
    /// <summary>宠物体系的设置版本：0 是 0.1.x 升级上来的老设置（放置方式保持自由放置，L6）。</summary>
    public const int CurrentLifeVersion = 1;

    private bool _lifeEnabled = true;
    private double _lifeSpeed = 1;
    private string _needHints = "bubble";
    private bool _hoverStatus = true;
    private string _placement = PlacementGround;
    private bool _allowClimb = true;
    private bool _allowThrow = true;
    private string _userCallName = "";
    private bool _perchOnWindows;
    private bool _summonHotkey = true;

    /// <summary>启用宠物体系：饱腹、精力、心情、亲密度。关掉后数值冻结，回到纯观赏。</summary>
    public bool LifeEnabled { get => _lifeEnabled; set => Set(ref _lifeEnabled, value); }
    /// <summary>数值变化速度：0.5 慢 / 1 标准 / 2 快。</summary>
    public double LifeSpeed { get => _lifeSpeed; set => Set(ref _lifeSpeed, value <= 0.75 ? 0.5 : value >= 1.5 ? 2 : 1); }
    /// <summary>饿了困了时：bubble 冒气泡 / card 只在状态浮层显示 / off 不提醒。</summary>
    public string NeedHints { get => _needHints; set => Set(ref _needHints, value is "card" or "off" ? value : "bubble"); }
    /// <summary>鼠标在宠物身上停一秒显示状态浮层。</summary>
    public bool HoverStatus { get => _hoverStatus; set => Set(ref _hoverStatus, value); }
    /// <summary>放置方式：ground 站在地面（有重力、能甩、能爬墙），free 自由放置（0.1.x 的行为）。</summary>
    public string Placement { get => _placement; set => Set(ref _placement, value == PlacementFree ? PlacementFree : PlacementGround); }
    /// <summary>走到屏幕边上时按概率爬墙（要这只宠物有贴墙动作）。</summary>
    public bool AllowClimb { get => _allowClimb; set => Set(ref _allowClimb, value); }
    /// <summary>拖拽时快速松手会把它甩出去。</summary>
    public bool AllowThrow { get => _allowThrow; set => Set(ref _allowThrow, value); }
    /// <summary>希望宠物怎么称呼用户；亲密度 5 级以后会用（台词里的 {user}）。</summary>
    public string UserCallName { get => _userCallName; set => Set(ref _userCallName, (value ?? "").Trim()); }
    public int LifeVersion { get; set; }
    /// <summary>站在其他应用的窗口顶上（design/pet-life/04 §7，L9 默认关）。只读窗口位置，不读标题。</summary>
    public bool PerchOnWindows { get => _perchOnWindows; set => Set(ref _perchOnWindows, value); }
    /// <summary>全局快捷键召唤（Ctrl+Alt+P / ⌃⌥P）：宠物跑到光标下面。</summary>
    public bool SummonHotkey { get => _summonHotkey; set => Set(ref _summonHotkey, value); }

    [JsonIgnore] public bool GroundMode => Placement == PlacementGround;

    // ───── 菜单 ─────

    /// <summary>右键菜单的条目和顺序。加载时会补上新版本新增的条目。</summary>
    public ObservableCollection<MenuEntry> MenuItems { get; set; } = new();

    // ───── 桌面组件 ─────

    private bool _hourlyChime;
    private bool _drinkReminder;
    private int _drinkMinutes = 60;
    private bool _sitReminder;
    private int _sitMinutes = 45;
    private int _pomodoroWork = 25;
    private int _pomodoroRest = 5;
    private bool _dailyGreeting = true;

    public bool HourlyChime { get => _hourlyChime; set => Set(ref _hourlyChime, value); }
    public bool DrinkReminder { get => _drinkReminder; set => Set(ref _drinkReminder, value); }
    public int DrinkMinutes { get => _drinkMinutes; set => Set(ref _drinkMinutes, Math.Clamp(value, 10, 240)); }
    public bool SitReminder { get => _sitReminder; set => Set(ref _sitReminder, value); }
    public int SitMinutes { get => _sitMinutes; set => Set(ref _sitMinutes, Math.Clamp(value, 10, 240)); }
    public int PomodoroWork { get => _pomodoroWork; set => Set(ref _pomodoroWork, Math.Clamp(value, 5, 90)); }
    public int PomodoroRest { get => _pomodoroRest; set => Set(ref _pomodoroRest, Math.Clamp(value, 1, 30)); }
    /// <summary>启动时按时段打招呼。</summary>
    public bool DailyGreeting { get => _dailyGreeting; set => Set(ref _dailyGreeting, value); }

    // ───── 智能 ─────

    private string _activeProviderId = "";
    private int _maxTokens = FixedMaxTokens;
    private int _contextTurns = FixedContextTurns;
    private int _timeoutSeconds = 60;
    private string _systemPrompt = DefaultSystemPrompt;
    private bool _memoryEnabled = true;
    private bool _autoMemory = true;
    private bool _compactContext = true;
    private bool _compactMemory = true;
    private bool _persistChat = true;
    private PersonaMode _personaMode = PersonaMode.Pack;
    private string _personaCustom = "";
    private string _personaNotes = "";

    /// <summary>0.1.4 及以前的默认提示词。迁移时用来判断用户改没改过（03 §3.6）。</summary>
    public const string DefaultSystemPrompt =
        "你是 Astra，一只住在用户桌面上的小宠物。说话简短、温暖、带一点俏皮，每次回复不超过三句话，" +
        "不用 Markdown。你会记住用户告诉你的兴趣爱好和习惯，并在合适的时候自然地提起。";

    /// <summary>当前的提示词结构版本：2 是分层提示词（人设 + 用户补充，格式要求由程序管）。</summary>
    public const int CurrentPromptVersion = 2;

    public ObservableCollection<LlmProvider> Providers { get; set; } = new();
    public string ActiveProviderId { get => _activeProviderId; set => Set(ref _activeProviderId, value); }
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, Math.Clamp(value, 16, 8192)); }
    /// <summary>
    /// 对话参数不再给用户调：温度不发（用模型自己的默认），回复长度和上下文轮数固定成宽松的上限，
    /// 真正的上下文长度由模型窗口的预算控制、轮数不设上限（<see cref="Ai.ContextBudget"/>），超了就压缩。<see cref="Normalize"/> 会把旧设置文件里的值改回这里。
    /// </summary>
    public const int FixedMaxTokens = 4096;
    /// <summary>上下文轮数不设上限：长度只由模型窗口的预算控制，超了就压缩成摘要。</summary>
    public const int FixedContextTurns = int.MaxValue;

    /// <summary>每次请求带上最近多少轮对话（不限，见 <see cref="FixedContextTurns"/>）。</summary>
    public int ContextTurns { get => _contextTurns; set => Set(ref _contextTurns, Math.Max(0, value)); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, Math.Clamp(value, 5, 300)); }
    /// <summary>
    /// 旧版整段的系统提示词，只为迁移保留：加载时如果用户改过，搬进 <see cref="PersonaCustom"/>，之后清空不再使用。
    /// </summary>
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value ?? ""); }
    /// <summary>设置文件里的提示词结构版本，旧文件没有这个字段（0）。</summary>
    public int PromptVersion { get; set; }
    /// <summary>迁移时把用户改过的旧提示词搬成了自定义人设，控制台提示一次后清掉。</summary>
    public bool PromptMigrationNotice { get; set; }
    /// <summary>人设来源：角色包里的，还是用户整段自定义的。</summary>
    public PersonaMode PersonaMode { get => _personaMode; set => Set(ref _personaMode, value); }
    /// <summary>自定义人设（<see cref="PersonaMode.Custom"/> 时用），描述名字、性格、说话风格。</summary>
    public string PersonaCustom { get => _personaCustom; set => Set(ref _personaCustom, value ?? ""); }
    /// <summary>用户对宠物的额外要求（「叫我老板」「多用颜文字」），两种人设下都生效。</summary>
    public string PersonaNotes { get => _personaNotes; set => Set(ref _personaNotes, value ?? ""); }
    /// <summary>把记忆写进系统提示词。</summary>
    public bool MemoryEnabled { get => _memoryEnabled; set => Set(ref _memoryEnabled, value); }
    /// <summary>每轮对话后让模型提取值得记住的信息。</summary>
    public bool AutoMemory { get => _autoMemory; set => Set(ref _autoMemory, value); }
    /// <summary>超过上下文预算时，把较早的几轮交给模型压缩成摘要（写进系统提示词），而不是直接丢掉。始终开启，界面上没有开关。</summary>
    public bool CompactContext { get => _compactContext; set => Set(ref _compactContext, value); }
    /// <summary>压缩上下文时顺带从那几轮里提取用户信息记下来（不多发请求）。默认打开。</summary>
    public bool CompactMemory { get => _compactMemory; set => Set(ref _compactMemory, value); }
    /// <summary>把和智能体的对话（最近几轮和摘要）存在本机，重启后接着聊。每只宠物分开存。关掉时删除已存的记录。</summary>
    public bool PersistChat { get => _persistChat; set => Set(ref _persistChat, value); }
    public ObservableCollection<MemoryItem> Memories { get; set; } = new();
    /// <summary>宠物答应用户的定时提醒（Agent 的 remind 工具设的），到点由宠物自己开口提醒。</summary>
    public ObservableCollection<ScheduledReminder> Reminders { get; set; } = new();

    [JsonIgnore]
    public LlmProvider? ActiveProvider =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault();

    // ───── 生成模型（工作台） ─────

    private string _activeImageProviderId = "";
    private string _activeVideoProviderId = "";

    /// <summary>工作台用的图片和视频生成接口（design/pet-studio/05）。</summary>
    public ObservableCollection<MediaProvider> MediaProviders { get; set; } = new();
    public string ActiveImageProviderId { get => _activeImageProviderId; set => Set(ref _activeImageProviderId, value ?? ""); }
    public string ActiveVideoProviderId { get => _activeVideoProviderId; set => Set(ref _activeVideoProviderId, value ?? ""); }

    [JsonIgnore] public MediaProvider? ActiveImageProvider => ActiveMedia(MediaKind.Image, ActiveImageProviderId);
    [JsonIgnore] public MediaProvider? ActiveVideoProvider => ActiveMedia(MediaKind.Video, ActiveVideoProviderId);

    private MediaProvider? ActiveMedia(MediaKind kind, string id) =>
        MediaProviders.FirstOrDefault(p => p.Kind == kind && p.Id == id) ?? MediaProviders.FirstOrDefault(p => p.Kind == kind);

    // ───── 外部控制 ─────

    public const int DefaultExternalPort = 47615;

    private bool _externalApi;
    private int _externalPort = DefaultExternalPort;
    private string _externalToken = "";

    /// <summary>本地 HTTP 接口：让 Claude Code、脚本等外部 Agent 驱动宠物。默认关，只监听 127.0.0.1（03 §2）。</summary>
    public bool ExternalApi { get => _externalApi; set => Set(ref _externalApi, value); }
    public int ExternalPort { get => _externalPort; set => Set(ref _externalPort, Math.Clamp(value, 1024, 65535)); }
    /// <summary>外部请求要带的令牌（Authorization: Bearer）。打开接口时生成。</summary>
    public string ExternalToken { get => _externalToken; set => Set(ref _externalToken, value ?? ""); }

    // ───── 编程智能体联动（design/agent-hooks） ─────

    private int _agentCelebrateSeconds = 30;
    private bool _agentShowProject = true;
    private bool _agentSpeakWaiting = true;

    /// <summary>智能体一轮跑了这么久（秒）才庆祝并冒泡，短的只安静收起思考。</summary>
    public int AgentCelebrateSeconds { get => _agentCelebrateSeconds; set => Set(ref _agentCelebrateSeconds, Math.Clamp(value, 0, 3600)); }
    /// <summary>冒泡里带项目目录名。</summary>
    public bool AgentShowProject { get => _agentShowProject; set => Set(ref _agentShowProject, value); }
    /// <summary>智能体等确认时把提醒念出来（还要开了朗读提醒）。</summary>
    public bool AgentSpeakWaiting { get => _agentSpeakWaiting; set => Set(ref _agentSpeakWaiting, value); }

    // ───── 语音 ─────

    private bool _speakReplies;
    private bool _speakReminders;
    private string _voice = "";
    private int _speechRate = 200;
    private double _speechVolume = 0.8;

    /// <summary>朗读智能体的回复。</summary>
    public bool SpeakReplies { get => _speakReplies; set => Set(ref _speakReplies, value); }
    /// <summary>朗读提醒和点击台词。</summary>
    public bool SpeakReminders { get => _speakReminders; set => Set(ref _speakReminders, value); }
    /// <summary>系统语音名，空表示系统默认。</summary>
    public string Voice { get => _voice; set => Set(ref _voice, value ?? ""); }
    /// <summary>语速，每分钟字数，各平台自己换算。</summary>
    public int SpeechRate { get => _speechRate; set => Set(ref _speechRate, Math.Clamp(value, 80, 400)); }
    public double SpeechVolume { get => _speechVolume; set => Set(ref _speechVolume, Math.Clamp(value, 0, 1)); }

    // ───── 更新 ─────

    private bool _autoCheckUpdate = true;
    private string _updateChannel = "stable";
    private string _updateServer = "";
    private string _notifiedUpdate = "";

    /// <summary>启动后和之后每隔几小时自动检查更新，有新版本就在宠物头顶冒气泡。</summary>
    public bool AutoCheckUpdate { get => _autoCheckUpdate; set => Set(ref _autoCheckUpdate, value); }
    /// <summary>stable 只收正式版；preview 同时收预览版。</summary>
    public string UpdateChannel { get => _updateChannel; set => Set(ref _updateChannel, value == "preview" ? "preview" : "stable"); }
    /// <summary>更新服务器（Astra Hub 的地址）。空 = 用构建时写进程序的那个。</summary>
    public string UpdateServer { get => _updateServer; set => Set(ref _updateServer, value ?? ""); }
    /// <summary>已经用气泡提醒过的新版本号，同一个版本只提醒一次，不烦人。</summary>
    public string NotifiedUpdate { get => _notifiedUpdate; set => Set(ref _notifiedUpdate, value ?? ""); }

    // ───── 运行时状态 ─────

    private bool _paused;
    private bool _visible = true;
    private bool _musicPlaying;
    private bool _saving;

    [JsonIgnore] public bool Paused { get => _paused; set => Set(ref _paused, value); }
    [JsonIgnore] public bool Visible { get => _visible; set => Set(ref _visible, value); }
    /// <summary>检测到正在播放音乐。</summary>
    [JsonIgnore] public bool MusicPlaying { get => _musicPlaying; set => Set(ref _musicPlaying, value); }
    /// <summary>省电模式已生效（用户离开超过设定时间）。</summary>
    [JsonIgnore] public bool PowerSavingActive { get => _saving; set => Set(ref _saving, value); }

    /// <summary>实际帧率倍率：省电时减半。</summary>
    [JsonIgnore] public double EffectiveSpeed => PowerSavingActive ? Speed * 0.5 : Speed;

    public string[] ClickLineList() =>
        ClickLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>加载后调用：迁移旧提示词、补默认菜单项、挂上条目的变更通知。</summary>
    /// <param name="upgraded">设置是从已有文件读出来的（不是新安装）。</param>
    public void Normalize(bool upgraded = false)
    {
        MigratePrompt();
        if (upgraded)
        {
            // 旧设置文件里用户调过的对话参数不再生效：改回固定值（新安装本来就是这些默认值）。
            MaxTokens = FixedMaxTokens;
            ContextTurns = FixedContextTurns;
            CompactContext = true;
        }
        if (LifeVersion < CurrentLifeVersion)
        {
            // L6：老用户升级后保持「自由放置」，新安装默认「站在地面」。
            if (upgraded) Placement = PlacementFree;
            LifeVersion = CurrentLifeVersion;
        }
        foreach (var id in MenuEntry.AllIds)
            if (MenuItems.All(m => m.Id != id))
            {
                // 新版本加的项插在「退出」前面（「退出」习惯放最后）。
                var entry = new MenuEntry { Id = id, Visible = MenuEntry.DefaultVisible(id) };
                var exitAt = id == MenuEntry.Exit ? -1 : MenuItems.ToList().FindIndex(m => m.Id == MenuEntry.Exit);
                if (exitAt >= 0) MenuItems.Insert(exitAt, entry); else MenuItems.Add(entry);
            }
        foreach (var stale in MenuItems.Where(m => !MenuEntry.AllIds.Contains(m.Id)).ToList())
            MenuItems.Remove(stale);
        foreach (var exit in MenuItems.Where(m => !m.CanHide))
            exit.Visible = true;

        foreach (var empty in Reminders.Where(r => r.Text.Trim().Length == 0).ToList())
            Reminders.Remove(empty);
        foreach (var reminder in Reminders.Where(r => r.Id.Length == 0 || Reminders.Count(o => o.Id == r.Id) > 1).ToList())
            reminder.Id = ReminderSchedule.NewId(Reminders);

        foreach (var list in new INotifyCollectionChanged[] { Providers, Memories, Reminders, MenuItems, MediaProviders })
        {
            list.CollectionChanged -= OnCollectionChanged;
            list.CollectionChanged += OnCollectionChanged;
        }
        foreach (var item in Providers.Cast<INotifyPropertyChanged>().Concat(Memories).Concat(Reminders).Concat(MenuItems).Concat(MediaProviders))
        {
            item.PropertyChanged -= OnItemChanged;
            item.PropertyChanged += OnItemChanged;
        }
        PropertyChanged -= OnSelfChanged;
        PropertyChanged += OnSelfChanged;
    }

    /// <summary>
    /// 0.1.4 的整段提示词拆成「人设 + 用户补充」（03 §3.6）：没改过（等于旧默认值或空）用角色包的人设；
    /// 改过就整段当自定义人设，控制台提示一次。
    /// </summary>
    private void MigratePrompt()
    {
        if (PromptVersion >= CurrentPromptVersion) return;
        var old = SystemPrompt.Trim();
        if (old.Length > 0 && old != DefaultSystemPrompt)
        {
            PersonaMode = PersonaMode.Custom;
            PersonaCustom = old;
            PromptMigrationNotice = true;
        }
        SystemPrompt = "";
        PromptVersion = CurrentPromptVersion;
    }

    private void OnSelfChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Paused) or nameof(Visible) or nameof(MusicPlaying) or nameof(PowerSavingActive)
            or nameof(ActiveImageProvider) or nameof(ActiveVideoProvider))
            return;
        Changed?.Invoke();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MediaProvider)
        {
            Raise(nameof(ActiveImageProvider));
            Raise(nameof(ActiveVideoProvider));
        }
        Changed?.Invoke();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged item in e.NewItems ?? Array.Empty<object>())
            item.PropertyChanged += OnItemChanged;
        foreach (INotifyPropertyChanged item in e.OldItems ?? Array.Empty<object>())
            item.PropertyChanged -= OnItemChanged;
        if (sender == Providers) Raise(nameof(ActiveProvider));
        if (sender == MediaProviders)
        {
            Raise(nameof(ActiveImageProvider));
            Raise(nameof(ActiveVideoProvider));
        }
        Changed?.Invoke();
    }
}

/// <summary>人设来源。</summary>
public enum PersonaMode
{
    /// <summary>用角色包里的人设（格式 1 的角色用通用人设）。</summary>
    Pack,
    /// <summary>用户整段自定义。</summary>
    Custom,
}

/// <summary>模型支不支持工具调用（让宠物做动作要用）。</summary>
public enum ToolSupport
{
    /// <summary>先带工具请求，被拒（4xx 且提到 tool / function）就去掉工具重试，并记住这个模型不支持。</summary>
    Auto,
    On,
    Off,
}

/// <summary>大模型接口协议。</summary>
public enum LlmProtocol
{
    /// <summary>OpenAI Chat Completions：POST {base}/chat/completions。各家兼容接口大多是这个。</summary>
    ChatCompletions,
    /// <summary>OpenAI Responses：POST {base}/responses。</summary>
    Responses,
    /// <summary>Anthropic Messages：POST {base}/v1/messages。</summary>
    Anthropic,
}

/// <summary>一个大模型接口配置。</summary>
public sealed class LlmProvider : Observable
{
    private string _name = "新模型";
    private LlmProtocol _protocol;
    private string _baseUrl = "https://api.openai.com/v1";
    private string _apiKey = "";
    private string _model = "gpt-4o-mini";
    private string _presetId = "";
    private List<string> _models = new();
    private ToolSupport _toolSupport;
    private string _noToolsModel = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public LlmProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    /// <summary>对话实际使用的模型名。</summary>
    public string Model { get => _model; set { if (Set(ref _model, value)) Raise(nameof(UsesTools)); } }
    /// <summary>来自哪个预设（<see cref="ProviderPresets"/>），决定图标。旧配置里没有，按地址猜。</summary>
    public string PresetId { get => _presetId; set => Set(ref _presetId, value ?? ""); }
    /// <summary>这个接口提供的模型列表（「获取模型」或导入得到），只用来给模型输入框做候选，可以为空。整体替换，不原地改。</summary>
    public List<string> Models { get => _models; set => Set(ref _models, value ?? new()); }
    /// <summary>工具调用：自动 / 开 / 关。</summary>
    public ToolSupport ToolSupport { get => _toolSupport; set { if (Set(ref _toolSupport, value)) Raise(nameof(UsesTools)); } }
    /// <summary>自动探测发现不支持工具的模型名；换了模型就重新探测。</summary>
    public string NoToolsModel { get => _noToolsModel; set { if (Set(ref _noToolsModel, value ?? "")) Raise(nameof(UsesTools)); } }

    /// <summary>这次请求带不带工具。</summary>
    [JsonIgnore]
    public bool UsesTools => ToolSupport switch
    {
        ToolSupport.On => true,
        ToolSupport.Off => false,
        _ => !string.Equals(NoToolsModel, Model, StringComparison.Ordinal),
    };

    public LlmProvider Clone()
    {
        var copy = new LlmProvider { Id = Id };
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>把另一份的内容拷过来（Id 不变）。编辑对话框用草稿，点保存才写回。</summary>
    public void CopyFrom(LlmProvider other)
    {
        Name = other.Name;
        Protocol = other.Protocol;
        BaseUrl = other.BaseUrl;
        ApiKey = other.ApiKey;
        Model = other.Model;
        PresetId = other.PresetId;
        Models = new List<string>(other.Models);
        ToolSupport = other.ToolSupport;
        NoToolsModel = other.NoToolsModel;
    }

    public override string ToString() => $"{Name} · {Model}";
}

/// <summary>智能体记住的一条用户信息。</summary>
public sealed class MemoryItem : Observable
{
    private string _text = "";

    public string Text { get => _text; set => Set(ref _text, value); }
    public DateTime Created { get; set; } = DateTime.Now;
    /// <summary>true 表示是对话中自动提取的，false 是用户手动添加的。</summary>
    public bool Auto { get; set; }

    [JsonIgnore] public string Meta => $"{(Auto ? "自动记住" : "手动添加")} · {Created:yyyy-MM-dd HH:mm}";
}

/// <summary>右键菜单的一项：是否显示，顺序由所在列表决定。</summary>
public sealed class MenuEntry : Observable
{
    public const string Chat = "chat";
    public const string Console = "console";
    public const string Pause = "pause";
    public const string StayStill = "still";
    public const string Pomodoro = "pomodoro";
    public const string Mute = "mute";
    public const string Center = "center";
    public const string Hide = "hide";
    public const string Exit = "exit";
    public const string Feed = "feed";
    public const string Play = "play";
    public const string Sleep = "sleep";
    public const string Status = "status";
    public const string Actions = "actions";
    /// <summary>「做个动作」子菜单里每个动作的命令：action:&lt;动作 id&gt;。</summary>
    public const string ActionPrefix = "action:";
    /// <summary>喂食子菜单里每个食物的命令：feed:&lt;食物 id&gt;。</summary>
    public const string FeedPrefix = "feed:";

    public static readonly string[] AllIds = [Chat, Actions, Feed, Play, Sleep, Status, Console, Pause, StayStill, Pomodoro, Mute, Center, Hide, Exit];

    private bool _visible = true;

    public string Id { get; set; } = "";
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    [JsonIgnore] public string Label => LabelOf(Id);
    [JsonIgnore] public string Description => Id switch
    {
        Chat => "打开对话输入框，和智能体聊天。",
        Console => "打开这个控制台。",
        Pause => "暂停 / 继续动画。",
        StayStill => "切换「不移动」。",
        Pomodoro => "开始或停止番茄钟。",
        Mute => "切换朗读回复。",
        Center => "把宠物放回屏幕中央。",
        Hide => "隐藏宠物，可在控制台重新显示。",
        Exit => "退出 Astra Pet。始终保留。",
        Feed => "喂它吃东西：饭团、牛奶不限量，零食靠完成番茄钟攒。",
        Play => "陪它玩一分钟：它会追着光标跑。",
        Sleep => "哄它睡觉 / 叫醒它（这只宠物有睡觉动作时才显示）。",
        Status => "打开控制台的「状态」页。",
        Actions => "从这只宠物的动作里挑一个让它做（还没解锁、或现在姿势做不了的会置灰）。",
        _ => "",
    };
    /// <summary>退出项不能隐藏，否则没有托盘时就关不掉了。</summary>
    [JsonIgnore] public bool CanHide => Id != Exit;

    public static string LabelOf(string id) => id switch
    {
        Chat => "和我聊天",
        Console => "控制台",
        Pause => "暂停动画",
        StayStill => "不移动",
        Pomodoro => "番茄钟",
        Mute => "朗读回复",
        Center => "回到屏幕中央",
        Hide => "隐藏宠物",
        Exit => "退出",
        Feed => "喂食",
        Play => "陪我玩",
        Sleep => "哄睡",
        Status => "查看状态",
        Actions => "做个动作",
        _ => id,
    };

    public static bool DefaultVisible(string id) => id is not (Mute or Hide);
}
