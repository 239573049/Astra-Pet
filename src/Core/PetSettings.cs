using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

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
        MenuItems.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>任何设置（含列表里的条目）变了，存盘用。</summary>
    public event Action? Changed;

    // ───── 常规 ─────

    private bool _autoStart;
    private bool _topmost = true;
    private bool _hideInFullscreen = true;
    private bool _allowCapture = true;
    private bool _clickThrough = true;

    /// <summary>登录系统时自动启动。实际开关由 <see cref="Platform.AutoStart"/> 落到系统里。</summary>
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    /// <summary>其他应用全屏时不显示宠物（macOS：不加入全屏空间）。</summary>
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }
    /// <summary>允许 OBS 等录屏软件捕获宠物窗口。关掉后录屏里看不到宠物。</summary>
    public bool AllowCapture { get => _allowCapture; set => Set(ref _allowCapture, value); }
    /// <summary>透明像素放行鼠标（目前只有 macOS 实现）。</summary>
    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }

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

    /// <summary>选的角色，是角色目录（<see cref="PetCatalog.Directory"/>）下的文件夹名；空串是内置角色。</summary>
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
    private string _clickLines = "干嘛戳我啦～\n嘿嘿，被你发现了\n今天也要加油哦！\n我在这儿陪着你呢\n别闹，我在想事情";

    /// <summary>单击播放点击回应动作。</summary>
    public bool ClickReact { get => _clickReact; set => Set(ref _clickReact, value); }
    /// <summary>单击时随机说一句 <see cref="ClickLines"/>。</summary>
    public bool ClickBubble { get => _clickBubble; set => Set(ref _clickBubble, value); }
    public bool DoubleClickChat { get => _doubleClickChat; set => Set(ref _doubleClickChat, value); }
    public bool DragEnabled { get => _dragEnabled; set => Set(ref _dragEnabled, value); }
    /// <summary>摸头：鼠标在角色身上停留 2 秒触发一次回应。</summary>
    public bool Petting { get => _petting; set => Set(ref _petting, value); }
    /// <summary>点击台词，一行一句。</summary>
    public string ClickLines { get => _clickLines; set => Set(ref _clickLines, value); }

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
    private double _temperature = 0.8;
    private int _maxTokens = 512;
    private int _contextTurns = 10;
    private int _timeoutSeconds = 60;
    private string _systemPrompt = DefaultSystemPrompt;
    private bool _memoryEnabled = true;
    private bool _autoMemory = true;

    public const string DefaultSystemPrompt =
        "你是 Astra，一只住在用户桌面上的小宠物。说话简短、温暖、带一点俏皮，每次回复不超过三句话，" +
        "不用 Markdown。你会记住用户告诉你的兴趣爱好和习惯，并在合适的时候自然地提起。";

    public ObservableCollection<LlmProvider> Providers { get; set; } = new();
    public string ActiveProviderId { get => _activeProviderId; set => Set(ref _activeProviderId, value); }
    public double Temperature { get => _temperature; set => Set(ref _temperature, Math.Clamp(value, 0, 2)); }
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, Math.Clamp(value, 16, 8192)); }
    /// <summary>每次请求带上最近多少轮对话。</summary>
    public int ContextTurns { get => _contextTurns; set => Set(ref _contextTurns, Math.Clamp(value, 0, 50)); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, Math.Clamp(value, 5, 300)); }
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }
    /// <summary>把记忆写进系统提示词。</summary>
    public bool MemoryEnabled { get => _memoryEnabled; set => Set(ref _memoryEnabled, value); }
    /// <summary>每轮对话后让模型提取值得记住的信息。</summary>
    public bool AutoMemory { get => _autoMemory; set => Set(ref _autoMemory, value); }
    public ObservableCollection<MemoryItem> Memories { get; set; } = new();

    [JsonIgnore]
    public LlmProvider? ActiveProvider =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault();

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
    /// <summary>语速，每分钟字数（macOS say 的 -r）。</summary>
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
    /// <summary>更新服务器（LocalGate 站点的地址）。空 = 用构建时写进程序的那个。</summary>
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

    /// <summary>随机链权重，照原版 window.py：待机 30 / 转向 10 / 动作 40 / 移动 20。</summary>
    [JsonIgnore]
    public (string Category, int Weight)[] Chain => PowerSavingActive
        ? [(PetLibrary.Idle, 1)]
        : [
            (PetLibrary.Idle, 30),
            (PetLibrary.Turn, 10),
            (PetLibrary.Click, 40),
            (PetLibrary.Move, StayStill ? 0 : 20),
        ];

    /// <summary>实际帧率倍率：省电时减半。</summary>
    [JsonIgnore] public double EffectiveSpeed => PowerSavingActive ? Speed * 0.5 : Speed;

    public string[] ClickLineList() =>
        ClickLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>加载后调用：补默认菜单项、挂上条目的变更通知。</summary>
    public void Normalize()
    {
        foreach (var id in MenuEntry.AllIds)
            if (MenuItems.All(m => m.Id != id))
                MenuItems.Add(new MenuEntry { Id = id, Visible = MenuEntry.DefaultVisible(id) });
        foreach (var stale in MenuItems.Where(m => !MenuEntry.AllIds.Contains(m.Id)).ToList())
            MenuItems.Remove(stale);
        foreach (var exit in MenuItems.Where(m => !m.CanHide))
            exit.Visible = true;

        Providers.CollectionChanged -= OnCollectionChanged;
        Memories.CollectionChanged -= OnCollectionChanged;
        MenuItems.CollectionChanged -= OnCollectionChanged;
        Providers.CollectionChanged += OnCollectionChanged;
        Memories.CollectionChanged += OnCollectionChanged;
        MenuItems.CollectionChanged += OnCollectionChanged;
        foreach (var item in Providers.Cast<INotifyPropertyChanged>().Concat(Memories).Concat(MenuItems))
        {
            item.PropertyChanged -= OnItemChanged;
            item.PropertyChanged += OnItemChanged;
        }
        PropertyChanged -= OnSelfChanged;
        PropertyChanged += OnSelfChanged;
    }

    private void OnSelfChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Paused) or nameof(Visible) or nameof(MusicPlaying) or nameof(PowerSavingActive))
            return;
        Changed?.Invoke();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged item in e.NewItems ?? Array.Empty<object>())
            item.PropertyChanged += OnItemChanged;
        foreach (INotifyPropertyChanged item in e.OldItems ?? Array.Empty<object>())
            item.PropertyChanged -= OnItemChanged;
        if (sender == Providers) Raise(nameof(ActiveProvider));
        Changed?.Invoke();
    }
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

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public LlmProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    /// <summary>对话实际使用的模型名。</summary>
    public string Model { get => _model; set => Set(ref _model, value); }
    /// <summary>来自哪个预设（<see cref="ProviderPresets"/>），决定图标。旧配置里没有，按地址猜。</summary>
    public string PresetId { get => _presetId; set => Set(ref _presetId, value ?? ""); }
    /// <summary>这个接口提供的模型列表（「获取模型」或导入得到），只用来给模型输入框做候选，可以为空。整体替换，不原地改。</summary>
    public List<string> Models { get => _models; set => Set(ref _models, value ?? new()); }

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

    public static readonly string[] AllIds = [Chat, Console, Pause, StayStill, Pomodoro, Mute, Center, Hide, Exit];

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
        _ => id,
    };

    public static bool DefaultVisible(string id) => id is not (Mute or Hide);
}
