using System.Collections.ObjectModel;
using System.ComponentModel;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Pet;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using MemoryItem = AstraPet.Core.MemoryItem;

namespace AstraPet.ControlPanel;

/// <summary>侧边栏的一页。</summary>
public sealed class NavItem(string key, string label, string description, string icon) : Observable
{
    private bool _active;

    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public Geometry Icon { get; } = Geometry.Parse(icon);
    public bool IsActive { get => _active; set => Set(ref _active, value); }
}

/// <summary>互动页的动作预览按钮。</summary>
public sealed record ActionOption(string Category, string Label);

/// <summary>侧边栏的一组页面，Title 为空的组不显示组名。</summary>
public sealed record NavGroup(string Title, NavItem[] Items)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>常规页「主色」的一个色块。</summary>
public sealed class AccentOption(string name, string hex) : Observable
{
    private bool _selected;

    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex));
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// 控制台的视图模型。设置项直接绑到 <see cref="PetSettings"/>，改了立即生效并自动存盘；
/// 这里只放需要额外逻辑的东西：页面切换、记忆、语音列表、菜单排序、番茄钟状态；
/// 提供商（卡片、添加、导入）单独放在 <see cref="ProvidersViewModel"/>。
/// </summary>
public sealed class ConsoleViewModel : Observable, IDisposable, Studio.IStudioHost
{
    private readonly PetWindow _pet;
    private readonly PetCompanion _companion;
    private readonly PetDirector _director;
    private readonly IPlatform _platform;
    private readonly IClipDecoder _decoder;
    private readonly PetCatalog _catalog;
    private readonly string _ffmpeg;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private NavItem _page;
    private string _newMemory = "";
    private string _promptPreview = "";
    private string _speechSample = "你好呀，我是 Astra，今天也要开开心心的！";
    private IReadOnlyList<VoiceOption> _voices = [VoiceOption.SystemDefault];
    private VoiceOption? _selectedVoice;
    private string _pomodoroStatus = "";
    private IReadOnlyList<PetEntry> _pets = [];
    private PetEntry? _selectedPet;
    private PetEntry _currentPet = null!;
    private string _petProblems = "";
    private string _petStatus = "";
    private int _petSwitch;

    public ConsoleViewModel(PetWindow pet, PetCompanion companion, PetSettings settings, PetDirector director,
        IPlatform platform, IClipDecoder decoder, PetCatalog catalog, string ffmpeg)
    {
        _pet = pet;
        _companion = companion;
        _director = director;
        _platform = platform;
        _decoder = decoder;
        _catalog = catalog;
        _ffmpeg = ffmpeg;
        Settings = settings;
        Hub = new ProvidersViewModel(settings, platform.Shell);
        Update = new UpdateViewModel(companion, settings, platform, Exit);
        Actions = new ActionsViewModel(director, companion, settings, ffmpeg);
        Life = new LifeViewModel(companion, director, pet, settings, platform.Capabilities, platform.Hotkey.Display);
        Market = new MarketViewModel(App.Composition!.Market, App.Composition.HubClient, platform.Shell,
            RefreshPets, UseInstalledPet, () => IsMarket);
        companion.ExternalStatusChanged += RaiseExternal;
        // 菜单预览和真实菜单是同一份数据（MenuBehavior.Rows）；设置一变（勾选、排序、宠物体系开关）就重算。
        // 具名方法：Dispose 时要退订，不然设置一直拽着整个控制台（连同窗口）释放不掉。
        settings.Changed += OnSettingsChanged;

        Pages =
        [
            new("Overview", "概览", "它现在在做什么，以及最常用的几个开关。",
                "M 4,4 L 10,4 L 10,11 L 4,11 Z M 14,4 L 20,4 L 20,8 L 14,8 Z M 14,12 L 20,12 L 20,20 L 14,20 Z M 4,15 L 10,15 L 10,20 L 4,20 Z"),
            new("Pet", "角色", "换角色，调大小、透明度、动画节奏和省电。",
                "M 12,3 A 9,9 0 1 0 12,21 A 9,9 0 1 0 12,3 Z M 8,14 Q 12,18 16,14 M 9,9 L 9,10 M 15,9 L 15,10"),
            new("Life", "状态", "饱腹、精力、心情和亲密度；喂食、性格和口味。",
                "M 12,20 C 5,15 3,11 3,8 A 4.5,4.5 0 0 1 12,6 A 4.5,4.5 0 0 1 21,8 C 21,11 19,15 12,20 Z"),
            new("Place", "位置", "站在地面还是自由放置，爬墙、甩出去和几个试玩按钮。",
                "M 3,20 L 21,20 M 5,20 L 5,4 M 9,16 A 2,2 0 1 0 9,12 A 2,2 0 1 0 9,16 Z M 9,16 L 9,20"),
            new("Interaction", "互动", "点击、双击、拖拽与摸头时它会怎么回应。",
                "M 9,12 L 9,5 A 1.5,1.5 0 0 1 12,5 L 12,11 M 12,10 L 12,9 A 1.5,1.5 0 0 1 15,9 L 15,12 M 15,11 A 1.5,1.5 0 0 1 18,11 L 18,15 A 6,6 0 0 1 12,21 L 11.5,21 A 6,6 0 0 1 6.5,18 L 4,14 A 1.5,1.5 0 0 1 7,13 L 9,15"),
            new("Actions", "动作", "它会哪些动作：预览、启用、调随机权重、改台词、绑定触发。",
                "M 12,4 A 2,2 0 1 0 12,8 A 2,2 0 1 0 12,4 Z M 12,8 L 12,15 M 12,15 L 8,21 M 12,15 L 16,21 M 6,11 L 12,10 L 18,7"),
            new("Menu", "右键菜单", "右键宠物时弹出哪些项，按什么顺序。",
                "M 4,4 L 20,4 L 20,20 L 4,20 Z M 8,9 L 16,9 M 8,13 L 16,13 M 8,17 L 13,17"),
            new("Widgets", "提醒与专注", "问候、报时、喝水与久坐提醒、番茄钟。",
                "M 12,6 A 7.5,7.5 0 1 0 12,21 A 7.5,7.5 0 1 0 12,6 Z M 12,10 L 12,14 L 14.5,15.5 M 10,3 L 14,3"),
            new("Ai", "模型与记忆", "大模型接口、对话参数、人设与记忆。",
                "M 12,3 L 14,10 L 21,12 L 14,14 L 12,21 L 10,14 L 3,12 L 10,10 Z"),
            new("Voice", "语音", "用系统语音朗读回复和提醒。",
                "M 9,5 A 3,3 0 0 1 15,5 L 15,11 A 3,3 0 0 1 9,11 Z M 5,10 A 7,7 0 0 0 19,10 M 12,17 L 12,21"),
            new("Market", "宠物市场", "浏览、购买和下载其他创作者做的角色。",
                "M 4,9 L 5.5,4 L 18.5,4 L 20,9 M 4,9 L 20,9 M 4,9 A 2.7,2.7 0 0 0 9.3,9 A 2.7,2.7 0 0 0 14.7,9 A 2.7,2.7 0 0 0 20,9 M 5,11.5 L 5,20 L 19,20 L 19,11.5 M 10,20 L 10,15 L 14,15 L 14,20"),
            new("General", "常规", "外观、启动、窗口层级、更新与数据。",
                "M 12,8 A 4,4 0 1 0 12,16 A 4,4 0 1 0 12,8 Z M 12,2 L 12,5 M 12,19 L 12,22 M 2,12 L 5,12 M 19,12 L 22,12 M 4.9,4.9 L 7,7 M 17,17 L 19.1,19.1 M 4.9,19.1 L 7,17 M 17,7 L 19.1,4.9"),
        ];
        _page = Pages[0];
        _page.IsActive = true;
        NavItem Page(string key) => Pages.First(p => p.Key == key);
        NavGroups =
        [
            new("", [Page("Overview")]),
            new("宠物", [Page("Pet"), Page("Life"), Page("Actions"), Page("Place"), Page("Interaction"), Page("Menu")]),
            new("陪伴", [Page("Widgets"), Page("Ai"), Page("Voice")]),
            new("市场", [Page("Market")]),
            new("应用", [Page("General")]),
        ];
        if (Environment.GetEnvironmentVariable("ASTRA_PET_PAGE") is { } start) Navigate(start);
        if (Environment.GetEnvironmentVariable("ASTRA_PET_DIALOG") is { } dialog)
            Dispatcher.UIThread.Post(() => Hub.OpenForTest(dialog));

        AccentOptions =
        [
            new("薄荷绿", PetSettings.DefaultAccent),
            new("湖蓝", "#2F80ED"),
            new("鸢尾紫", "#7B61FF"),
            new("莓果", "#D9467A"),
            new("珊瑚", "#EE6C4D"),
            new("琥珀", "#D99A1E"),
            new("石墨", "#6B7280"),
        ];
        SyncAccent();

        ActionOptions = BuildActionOptions();
        RefreshPets(settings.PetId);

        // 设置文件里的开关可能和系统实际状态不一致（比如用户手动删了 plist），以系统为准。
        if (AutoStartSupported) settings.AutoStart = platform.AutoStart.IsEnabled();

        settings.PropertyChanged += OnSettingChanged;
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
        _ = LoadVoicesAsync();
    }

    public PetSettings Settings { get; }
    public IPlatform Platform => _platform;
    public ProvidersViewModel Hub { get; }
    public UpdateViewModel Update { get; }
    public ActionsViewModel Actions { get; }
    public LifeViewModel Life { get; }
    public MarketViewModel Market { get; }
    public NavItem[] Pages { get; }
    public NavGroup[] NavGroups { get; }
    public ActionOption[] ActionOptions { get; private set; }

    /// <summary>宠物窗口：概览页的实时预览从它取帧。</summary>
    public PetWindow Pet => _pet;

    /// <summary>macOS 上窗口按钮画在左上角（红黄绿），其他系统在右上角。</summary>
    public bool IsMac => OperatingSystem.IsMacOS();
    public bool IsNotMac => !IsMac;

    // ───── 布局 ─────

    public string CurrentKey => _page.Key;
    public string PageTitle => _page.Label;
    public string PageDescription => _page.Description;
    public bool IsOverview => _page.Key == "Overview";
    public bool IsGeneral => _page.Key == "General";
    public bool IsPet => _page.Key == "Pet";
    public bool IsInteraction => _page.Key == "Interaction";
    public bool IsActions => _page.Key == "Actions";
    public bool IsMenu => _page.Key == "Menu";
    public bool IsWidgets => _page.Key == "Widgets";
    public bool IsAi => _page.Key == "Ai";
    public bool IsVoice => _page.Key == "Voice";
    public bool IsLife => _page.Key == "Life";
    public bool IsPlace => _page.Key == "Place";
    public bool IsMarket => _page.Key == "Market";

    public void Navigate(string key)
    {
        if (Pages.FirstOrDefault(p => p.Key == key) is not { } page || page == _page) return;
        _page.IsActive = false;
        _page = page;
        page.IsActive = true;
        foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(IsOverview), nameof(IsGeneral),
                     nameof(IsPet), nameof(IsInteraction), nameof(IsActions), nameof(IsMenu), nameof(IsWidgets), nameof(IsAi), nameof(IsVoice),
                     nameof(IsLife), nameof(IsPlace), nameof(IsMarket), nameof(CurrentKey) })
            Raise(name);
        if (IsMarket) Market.Activate();
        if (IsMenu) RaiseMenuPreview();
    }

    // ───── 概览 ─────

    public string PetName => _currentPet.Name;

    /// <summary>正在用的模型，「名字 · 模型」；还没配就提示去配。</summary>
    public string ActiveProviderText => Settings.ActiveProvider is { } p ? $"{p.Name} · {p.Model}" : "还没有配置模型";
    public bool HasProvider => Settings.ActiveProvider is not null;

    // ───── 外观 ─────

    public AccentOption[] AccentOptions { get; }

    public void SelectAccent(AccentOption option) => Settings.Accent = option.Hex;

    private void SyncAccent()
    {
        foreach (var option in AccentOptions)
            option.IsSelected = string.Equals(option.Hex, Settings.Accent, StringComparison.OrdinalIgnoreCase);
    }

    // ───── 常规 ─────

    private PlatformCapabilities Caps => _platform.Capabilities;

    public bool AutoStartSupported => Caps.AutoStart;
    public bool CanHideInFullscreen => Caps.OverFullscreen;
    public bool CanExcludeCapture => Caps.CaptureExclusion;
    public bool CanClickThrough => Caps.ClickThrough;
    public string SettingsPath => SettingsStore.FilePath;

    // ───── 外部控制（03 §2） ─────

    public string ExternalStatusText => _companion.ExternalStatus
        ?? "让 Claude Code 这类编程 Agent、脚本通过本机接口驱动宠物：做动作、说话、发事件。只监听 127.0.0.1，要带令牌。";

    public string ExternalPortText
    {
        get => Settings.ExternalPort.ToString();
        set
        {
            if (int.TryParse(value, out var port) && port is >= 1024 and <= 65535) Settings.ExternalPort = port;
            Raise(nameof(ExternalPortText));
        }
    }

    /// <summary>令牌只显示前后几位，复制按钮复制完整的。</summary>
    public string ExternalTokenMasked => Settings.ExternalToken is { Length: > 8 } t ? $"{t[..4]}…{t[^4..]}" : "（打开接口时生成）";

    public void RegenerateExternalToken() => Settings.ExternalToken = Core.External.ExternalApi.NewToken();

    private static string ExePath => Environment.ProcessPath ?? "AstraPet";

    /// <summary>加到 Claude Code：claude mcp add。</summary>
    public string McpCommandText => $"claude mcp add astrapet -- \"{ExePath}\" mcp";

    /// <summary>直接 curl 发事件的例子（hooks 里用）。</summary>
    public string CurlExample =>
        $"curl -s -X POST http://127.0.0.1:{Settings.ExternalPort}/v1/events \\\n" +
        $"  -H \"Authorization: Bearer {Settings.ExternalToken}\" \\\n" +
        "  -d '{\"type\":\"external.done\"}'";

    private void RaiseExternal()
    {
        Raise(nameof(ExternalStatusText));
        Raise(nameof(ExternalPortText));
        Raise(nameof(ExternalTokenMasked));
        Raise(nameof(CurlExample));
    }

    /// <summary>窗口相关开关的说明（哪些不支持、为什么），由平台的能力描述拼出来。</summary>
    public string PlatformNote => string.Join(" ", new[]
        {
            nameof(PlatformCapabilities.OverFullscreen),
            nameof(PlatformCapabilities.CaptureExclusion),
            nameof(PlatformCapabilities.ClickThrough),
            nameof(PlatformCapabilities.AutoStart),
        }
        .Select(Caps.NoteFor).OfType<string>());

    /// <summary>源码仓库（侧栏左下角的 GitHub 按钮）。</summary>
    public const string RepositoryUrl = "https://github.com/239573049/Astra-Pet";

    /// <summary>用系统默认浏览器打开源码仓库。</summary>
    public void OpenRepository() => _platform.Shell.OpenUrl(RepositoryUrl);

    /// <summary>程序目录下的 licenses 文件夹：LICENSE、宠物素材许可、第三方声明和 LGPL 全文。</summary>
    public void OpenLicensesFolder()
    {
        var dir = Path.Combine(_platform.Paths.ResourceDirectory, "licenses");
        if (!Directory.Exists(dir)) { PetLog.Write($"找不到许可文件夹: {dir}"); return; }
        _platform.Shell.OpenFolder(dir);
    }

    public void OpenSettingsFolder() => _platform.Shell.OpenFolder(SettingsStore.Directory);

    public string LogDirectory => _platform.Paths.LogDirectory;
    public string LogNote => $"按天一个文件，保留 {PetLog.KeepDays} 天。遇到问题时把当天的日志发给开发者。";

    public void OpenLogFolder() => _platform.Shell.OpenFolder(LogDirectory);

    // ───── 桌宠 ─────

    public bool SingAvailable => _director.Library.Has(PetLibrary.Sing);
    public string MusicNote => !Caps.AudioDetection
        ? Caps.NoteFor(nameof(PlatformCapabilities.AudioDetection)) ?? "当前系统暂不支持检测音乐播放。"
        : SingAvailable
            ? "检测到有声音在播放时（音乐、视频都算）改播唱歌动画。"
            : "检测到在放音乐时唱歌。当前角色没有唱歌素材（videos/sing），暂时用动作片段代替。";

    public string StatusText => Settings switch
    {
        { Visible: false } => "已隐藏",
        { Paused: true } => "已暂停",
        { PowerSavingActive: true } => "省电中",
        { MusicPlaying: true, MusicDetect: true } => "正在唱歌",
        _ => "运行中",
    };

    /// <summary>概览页状态徽标的色调：运行中绿，暂停、隐藏中性，其余提示色。</summary>
    public BadgeTone StatusTone => Settings switch
    {
        { Visible: false } or { Paused: true } => BadgeTone.Neutral,
        { PowerSavingActive: true } => BadgeTone.Warning,
        _ => BadgeTone.Success,
    };

    public void ResetPosition() => _pet.ResetPosition();

    // ───── 角色 ─────

    public string PetsDirectory => _catalog.UserDirectory;

    /// <summary>能用的角色：内置的加上角色目录里素材齐全的。</summary>
    public IReadOnlyList<PetEntry> Pets { get => _pets; private set => Set(ref _pets, value); }

    /// <summary>选一个就切过去：先在后台加载，成功了才换，失败了选择退回原来的。</summary>
    public PetEntry? SelectedPet
    {
        get => _selectedPet;
        set
        {
            // 列表整体替换时 ComboBox 会先把选中项清成 null，不是用户的选择。
            if (value is null || value == _selectedPet) return;
            _selectedPet = value;
            Raise(nameof(SelectedPet));
            if (value != _currentPet)
            {
                _ = SwitchPetAsync(value);
                return;
            }
            // 选回当前角色：取消还没加载完的那次切换。
            _petSwitch++;
            PetStatus = "";
        }
    }

    public string PetInfo => $"{_currentPet.Name} · {_director.Library.ClipCount} 段动画" +
                             (_currentPet.IsBuiltIn ? " · 内置" : _currentPet.Id == _currentPet.Name ? "" : $" · {_currentPet.Id}");

    /// <summary>当前角色的作者、许可和署名（来自角色包），一行一条。没有就是空串。</summary>
    public string PetCredits => _currentPet.Credits is not { } c ? "" : string.Join("\n",
        new[] { c.Author.Length > 0 ? $"作者：{c.Author}" : "", c.License.Length > 0 ? $"许可：{c.License}" : "" }
            .Concat(c.Lines).Where(l => l.Length > 0));

    public bool HasPetCredits => PetCredits.Length > 0;
    public bool HasPetLicenseUrl => _currentPet.Credits is { LicenseUrl.Length: > 0 };

    public void OpenPetLicense()
    {
        if (_currentPet.Credits is { LicenseUrl.Length: > 0 } c) _platform.Shell.OpenUrl(c.LicenseUrl);
    }

    /// <summary>目录里素材不全的文件夹，一行一个，说明缺什么。没有就是空串。</summary>
    public string PetProblems { get => _petProblems; private set { if (Set(ref _petProblems, value)) Raise(nameof(HasPetProblems)); } }
    public bool HasPetProblems => _petProblems.Length > 0;

    /// <summary>切换的进度和失败原因。</summary>
    public string PetStatus { get => _petStatus; private set { if (Set(ref _petStatus, value)) Raise(nameof(HasPetStatus)); } }
    public bool HasPetStatus => _petStatus.Length > 0;

    public void OpenPetsFolder() => _platform.Shell.OpenFolder(_catalog.UserDirectory);

    /// <summary>
    /// 市场页的「使用」：重新扫描角色目录，按 id（{packId}.astrapet）选中，走和「角色」页下拉框同一条切换路径。
    /// 找不到或素材有问题时返回原因，成功返回 null（加载失败的话 PetStatus 里有）。
    /// </summary>
    private string? UseInstalledPet(string id)
    {
        RefreshPets();
        if (Pets.FirstOrDefault(p => p.Id == id) is { } entry)
        {
            // 更新的正是正在用的角色：RefreshPets 已经把它当成当前角色，SelectedPet 的 setter 会认为没变而跳过，
            // 桌面上还是旧包加载好的动作。同 id 时强制重新加载。
            if (entry == _currentPet)
            {
                _tryoutReturn = null;
                _selectedPet = entry;
                Raise(nameof(SelectedPet));
                _ = SwitchPetAsync(entry);
                return null;
            }
            SelectedPet = entry;
            return null;
        }
        return _catalog.Scan().FirstOrDefault(p => p.Id == id)?.Problem ?? "在角色目录里没找到这个角色，试试刷新";
    }

    /// <summary>重新扫描角色目录。用户往里拷了新角色后点「刷新」。</summary>
    public void RefreshPets() => RefreshPets(_currentPet.Id);

    private void RefreshPets(string currentId)
    {
        // 选中项会被重置回当前角色，还没加载完的切换也得一起取消，免得界面和实际对不上。
        _petSwitch++;
        PetStatus = "";
        var all = _catalog.Scan();
        _currentPet = all.FirstOrDefault(p => p.Id == currentId) ?? all[0];
        PetProblems = string.Join("\n", all.Where(p => p.Problem is not null).Select(p => $"{(p.IsBuiltIn ? p.Name : p.Id)}：{p.Problem}"));
        Pets = all.Where(p => p.Problem is null).ToList();
        // Pets 换了之后 ComboBox 的选中项被清掉了，重新选一遍。
        _selectedPet = Pets.FirstOrDefault(p => p == _currentPet);
        Raise(nameof(SelectedPet));
        RaisePetInfo();
    }

    private async Task SwitchPetAsync(PetEntry pet)
    {
        var ticket = ++_petSwitch;
        PetStatus = $"正在加载「{pet.Name}」…";
        try
        {
            // 角色包要先解压，每段还要起一次 ffmpeg 探测，都放到线程池上，期间宠物照常动。
            var library = await Task.Run(() => _catalog.Load(pet, _decoder));
            if (ticket != _petSwitch) return;   // 加载期间又选了别的，以最后一次为准
            ApplyLibrary(library, pet, persist: true);
            _tryoutReturn = null;
        }
        catch (Exception e)
        {
            if (ticket != _petSwitch) return;
            PetLog.Write($"切换角色失败: {pet.Name}: {e.Message}");
            // 探测失败的信息里带着 ffmpeg 的整段输出，界面上只留第一行。
            PetStatus = $"「{pet.Name}」加载失败：{e.Message.Split('\n')[0]}";
            _selectedPet = Pets.FirstOrDefault(p => p == _currentPet);
            Raise(nameof(SelectedPet));
        }
    }

    private void ApplyLibrary(PetLibrary library, PetEntry pet, bool persist)
    {
        Actions.Save();
        _director.UseLibrary(library);
        Actions.Reload();
        _currentPet = pet;
        if (persist) Settings.PetId = pet.Id;
        PetStatus = "";
        PetLog.Write($"已切换角色: {pet.Name}" + (persist ? "" : "（试用，不记进设置）"));
        RaisePetInfo();
        Raise(nameof(SingAvailable));
        Raise(nameof(MusicNote));
        ActionOptions = BuildActionOptions();
        Raise(nameof(ActionOptions));
    }

    // ───── 工作台：试用和安装 ─────

    /// <summary>试用前的角色；不在试用时为 null。</summary>
    private PetEntry? _tryoutReturn;

    public bool IsTryingOut => _tryoutReturn is not null;

    /// <summary>
    /// 工作台 ⑦：把临时导出的包直接放到桌面上试，不改设置（程序重启后还是原来的角色），不套用户对动作的自定义。
    /// 失败抛异常，宠物保持原样。
    /// </summary>
    public async Task TryoutAsync(string packPath, string name)
    {
        var entry = new PetEntry("studio-tryout", name + "（试用）", packPath, IsPack: true, IsBuiltIn: false, null);
        var cache = App.Composition?.Platform.Paths.CacheDirectory ?? Path.GetTempPath();
        var library = await Task.Run(() =>
        {
            var dir = PetPack.Extract(packPath, cache);
            var lib = PetLibrary.Load(dir, _decoder, PetLog.Write);
            lib.Name = entry.Name;
            return lib;
        });
        _petSwitch++;
        _tryoutReturn ??= _currentPet;
        ApplyLibrary(library, entry, persist: false);
        _selectedPet = null;
        Raise(nameof(SelectedPet));
    }

    /// <summary>结束试用，回到试用前的角色。</summary>
    public async Task EndTryoutAsync()
    {
        if (_tryoutReturn is not { } back) return;
        _tryoutReturn = null;
        RefreshPets(back.Id);
        await SwitchPetAsync(_currentPet);
    }

    /// <summary>工作台 ⑧：把导出的包拷进角色目录（同名覆盖）并切过去。返回装好的文件路径。</summary>
    public async Task<string> InstallPackAsync(string packPath, string id)
    {
        Directory.CreateDirectory(_catalog.UserDirectory);
        var target = Path.Combine(_catalog.UserDirectory, id + PetPack.Extension);
        if (!string.Equals(Path.GetFullPath(packPath), Path.GetFullPath(target), StringComparison.Ordinal))
            File.Copy(packPath, target, overwrite: true);
        _tryoutReturn = null;
        RefreshPets(_currentPet.Id);
        var entry = Pets.FirstOrDefault(p => p.Id == Path.GetFileName(target))
                    ?? throw new InvalidDataException("装好的角色包没出现在角色列表里：" +
                                                     (_catalog.Scan().FirstOrDefault(p => p.Id == Path.GetFileName(target))?.Problem ?? "原因不明"));
        _selectedPet = entry;
        Raise(nameof(SelectedPet));
        await SwitchPetAsync(entry);
        if (_currentPet != entry) throw new InvalidDataException(PetStatus.Length > 0 ? PetStatus : "切换失败");
        return target;
    }

    /// <summary>按动作 id 在宠物上播一次（工作台 ⑦ 用）。没有这个动作返回 false。</summary>
    public bool PlayActionById(string id)
    {
        if (_director.Library.Actions.Find(id) is not { } def) return false;
        _director.Request(new ActionRequest(def with { Enabled = true }, ActionPriority.User, "studio"));
        return true;
    }

    private ActionOption[] BuildActionOptions() =>
    [
        new(PetLibrary.Click, "点击回应"),
        new(PetLibrary.Turn, "东张西望"),
        new(PetLibrary.Move, "走两步"),
        new(PetLibrary.Drag, "悬空挣扎"),
        new(PetLibrary.Sing, SingAvailable ? "唱歌" : "唱歌（缺素材）"),
        new(PetLibrary.Idle, "回到待机"),
    ];

    // ───── 互动 ─────

    public void PlayAction(string category) => _pet.Play(category);

    public void SayRandomLine()
    {
        var lines = Settings.ClickLineList();
        _companion.Say(lines.Length > 0 ? lines[Random.Shared.Next(lines.Length)] : "（还没有台词）");
    }

    // ───── 菜单 ─────
    // 顺序由菜单页的 ReorderableList 直接在 Settings.MenuItems 上移动，这里只管恢复默认。

    /// <summary>预览：和右键宠物时弹出的菜单一模一样（文字、勾、分隔线、子菜单），出现不了的项置灰并说明原因。</summary>
    public IReadOnlyList<MenuRow> MenuPreview => _companion.Menu.Rows();

    public void RaiseMenuPreview() => Raise(nameof(MenuPreview));

    public void ResetMenu()
    {
        Settings.MenuItems.Clear();
        Settings.Normalize();
    }

    // ───── 桌面组件 ─────

    public string PomodoroStatus { get => _pomodoroStatus; private set => Set(ref _pomodoroStatus, value); }
    public string PomodoroButtonText => _companion.Pomodoro.Running ? "停止" : "开始专注";

    public void TogglePomodoro()
    {
        if (_companion.Pomodoro.Running) _companion.Pomodoro.Stop(); else _companion.Pomodoro.Start();
        RefreshStatus();
    }

    public void TestBubble() => _companion.Say($"现在是 {DateTime.Now:HH:mm}，这是一条测试提醒～");

    // ───── 智能 ─────

    /// <summary>人设用自己写的，不用角色包里的（03 §3.3）。</summary>
    public bool UseCustomPersona
    {
        get => Settings.PersonaMode == PersonaMode.Custom;
        set
        {
            Settings.PersonaMode = value ? PersonaMode.Custom : PersonaMode.Pack;
            // 第一次切到自定义时给个起点：把角色包的人设抄过来改。
            if (value && Settings.PersonaCustom.Trim().Length == 0) CopyPackPersona();
        }
    }

    /// <summary>角色包里的人设，给人看的几行。</summary>
    public string PackPersonaText
    {
        get
        {
            if (_director.Library.Prompt is { } prompt)
                return prompt.Length <= PackPromptPreview ? prompt : prompt[..PackPromptPreview] + $"\n……（共 {prompt.Length:N0} 字，只显示开头）";
            var p = _director.Library.Persona;
            var lines = new List<string> { $"名字：{p.Name}" };
            if (p.Identity.Length > 0) lines.Add($"样子：{p.Identity}");
            if (p.Personality.Length > 0) lines.Add($"性格：{p.Personality}");
            if (p.Speech.Length > 0) lines.Add($"说话风格：{p.Speech}");
            if (p.Likes.Count > 0) lines.Add($"喜欢：{string.Join("、", p.Likes)}");
            if (p.Catchphrases.Count > 0) lines.Add($"口头禅：{string.Join("、", p.Catchphrases)}");
            return string.Join("\n", lines);
        }
    }

    private const int PackPromptPreview = 1200;

    public string PackPersonaSource => _director.Library.Prompt is not null
        ? $"来自角色包「{_currentPet.Name}」自带的提示词（.md）"
        : _director.Library.Manifest is { } m && Ai.PetPersona.Parse(m) is not null
        ? $"来自角色包「{_currentPet.Name}」"
        : $"「{_currentPet.Name}」的角色包没有写人设，用的是通用人设";

    public void CopyPackPersona() => Settings.PersonaCustom = _director.Library.Prompt ?? PackPersonaText;

    /// <summary>旧版改过的提示词被搬成了自定义人设，提示一次。</summary>
    public bool ShowPromptMigrationNotice => Settings.PromptMigrationNotice;

    public void DismissPromptMigrationNotice()
    {
        Settings.PromptMigrationNotice = false;
        Raise(nameof(ShowPromptMigrationNotice));
        SettingsStore.Save(Settings);
    }

    /// <summary>当前模型能不能让宠物做动作，一行说明。</summary>
    public string AgentActionText => Settings.ActiveProvider switch
    {
        null => "还没有配置模型。",
        { UsesTools: true } => $"聊天时它可以自己选动作（{Ai.PlayActionTool.Choosable(_director.Library.Actions).Count} 个动作和 {Ai.PlayActionTool.Moods.Count} 种心情可选）。动作的描述在「动作」页改。",
        { ToolSupport: ToolSupport.Off } => "当前模型关掉了工具调用，回复后宠物随机做一个反应。可以在模型设置里改。",
        _ => "当前模型不支持工具调用，回复后宠物随机做一个反应。",
    };

    public string PromptPreview { get => _promptPreview; private set => Set(ref _promptPreview, value); }

    /// <summary>完整的系统提示词；带工具时后面附上 play_action 的工具说明（动作清单在那里）。</summary>
    public void RefreshPromptPreview()
    {
        var agent = _companion.Agent;
        var text = agent.BuildSystemPrompt();
        if (Settings.ActiveProvider is { UsesTools: true } && agent.PlayAction.Definition() is { } tool)
            text += "\n\n──────── 工具 play_action 的说明（随请求发送）────────\n" + tool.Description;
        PromptPreview = text;
        Raise(nameof(AgentActionText));
    }

    public string NewMemory { get => _newMemory; set => Set(ref _newMemory, value); }

    public void AddMemory()
    {
        if (_companion.Agent.AddMemory(NewMemory, auto: false)) NewMemory = "";
    }

    public void RemoveMemory(MemoryItem item) => Settings.Memories.Remove(item);

    public void ClearMemories() => Settings.Memories.Clear();

    // ───── 语音 ─────

    public bool SpeechSupported => Caps.Speech;
    public string SpeechNote => Caps.NoteFor(nameof(PlatformCapabilities.Speech)) ?? "";
    public bool HasSpeechNote => SpeechNote.Length > 0;

    public IReadOnlyList<VoiceOption> Voices { get => _voices; private set => Set(ref _voices, value); }

    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!Set(ref _selectedVoice, value) || value is null) return;
            Settings.Voice = value.Name;
        }
    }

    public string SpeechSample { get => _speechSample; set => Set(ref _speechSample, value); }

    public void TestSpeech() => _companion.Speak(SpeechSample);

    public void StopSpeech() => _companion.StopSpeaking();

    private async Task LoadVoicesAsync()
    {
        var voices = await _platform.Speech.ListVoicesAsync();
        Voices = voices;
        _selectedVoice = voices.FirstOrDefault(v => v.Name == Settings.Voice) ?? voices[0];
        Raise(nameof(SelectedVoice));
    }

    // ───── 公共 ─────

    public void OpenChat() => _companion.RequestChat();

    /// <summary>「角色」页的「打开角色工作台」。窗口由 App 建。</summary>
    public event Action? StudioRequested;

    public void OpenStudio() => StudioRequested?.Invoke();

    public void Exit() => _pet.Close();

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetSettings.AutoStart):
                var autoStart = _platform.AutoStart;
                if (AutoStartSupported && autoStart.IsEnabled() != Settings.AutoStart
                    && !autoStart.Apply(Settings.AutoStart, AutoStartEnvironment()))
                    Dispatcher.UIThread.Post(() => Settings.AutoStart = autoStart.IsEnabled());
                break;
            case nameof(PetSettings.Visible):
            case nameof(PetSettings.Paused):
            case nameof(PetSettings.PowerSavingActive):
            case nameof(PetSettings.MusicPlaying):
            case nameof(PetSettings.MusicDetect):
                Raise(nameof(StatusText));
                Raise(nameof(StatusTone));
                break;
            case nameof(PetSettings.Accent):
                SyncAccent();
                break;
            case nameof(PetSettings.ActiveProviderId):
            case nameof(PetSettings.ActiveProvider):
                RaiseProvider();
                break;
            case nameof(PetSettings.PersonaMode):
                Raise(nameof(UseCustomPersona));
                break;
            case nameof(PetSettings.ExternalPort):
            case nameof(PetSettings.ExternalToken):
                RaiseExternal();
                break;
        }
    }

    private void RaiseProvider()
    {
        Raise(nameof(AgentActionText));
        Raise(nameof(ActiveProviderText));
        Raise(nameof(HasProvider));
    }

    /// <summary>自启时带上这次用的 ffmpeg（和开发时换掉的默认角色素材），让自启的那次和这次一样。</summary>
    private Dictionary<string, string> AutoStartEnvironment()
    {
        var env = new Dictionary<string, string> { ["FFMPEG"] = _ffmpeg };
        if (_catalog.DefaultOverride is { } videos) env["ASTRA_PET_VIDEOS"] = videos;
        return env;
    }

    private void RaisePetInfo()
    {
        Raise(nameof(PackPersonaText));
        Raise(nameof(PackPersonaSource));
        Raise(nameof(AgentActionText));
        if (PromptPreview.Length > 0) RefreshPromptPreview();
        Raise(nameof(PetName));
        Raise(nameof(PetInfo));
        Raise(nameof(PetCredits));
        Raise(nameof(HasPetCredits));
        Raise(nameof(HasPetLicenseUrl));
    }

    private void RefreshStatus()
    {
        PomodoroStatus = _companion.Pomodoro.Status;
        Raise(nameof(PomodoroButtonText));
        Raise(nameof(PomodoroRunning));
        // 提供商的名字和模型在对话框里改，不走 Settings 的属性通知，顺带刷一下。
        RaiseProvider();
    }

    public bool PomodoroRunning => _companion.Pomodoro.Running;

    /// <summary>有没有跑到一半的活（导入动作、安装更新）。有的话控制台不释放。</summary>
    public bool Busy => Actions.Import.IsProcessing || Update.Busy || Market.Busy;

    private void OnSettingsChanged()
    {
        if (IsMenu) RaiseMenuPreview();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        Actions.Save();
        Settings.PropertyChanged -= OnSettingChanged;
        Settings.Changed -= OnSettingsChanged;
        _companion.ExternalStatusChanged -= RaiseExternal;
        Hub.Dispose();
        Update.Dispose();
        Life.Dispose();
        Market.Dispose();
    }
}
