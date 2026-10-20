using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Pet;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

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

/// <summary>
/// 控制台的视图模型。设置项直接绑到 <see cref="PetSettings"/>，改了立即生效并自动存盘；
/// 这里只放需要额外逻辑的东西：页面切换、记忆、语音列表、菜单排序、番茄钟状态；
/// 提供商（卡片、添加、导入）单独放在 <see cref="ProvidersViewModel"/>。
/// </summary>
public sealed class ConsoleViewModel : Observable, IDisposable
{
    private readonly PetWindow _pet;
    private readonly PetCompanion _companion;
    private readonly PetDirector _director;
    private readonly string _builtInDir;
    private readonly string _ffmpeg;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private NavItem _page;
    private bool _sidebarExpanded = true;
    private string _newMemory = "";
    private string _promptPreview = "";
    private string _speechSample = "你好呀，我是 Astra，今天也要开开心心的！";
    private IReadOnlyList<VoiceOption> _voices = [new("", "")];
    private VoiceOption? _selectedVoice;
    private string _pomodoroStatus = "";
    private IReadOnlyList<PetEntry> _pets = [];
    private PetEntry? _selectedPet;
    private PetEntry _currentPet = null!;
    private string _petProblems = "";
    private string _petStatus = "";
    private int _petSwitch;

    public ConsoleViewModel(PetWindow pet, PetCompanion companion, PetSettings settings, PetDirector director,
        string builtInVideosDir, string ffmpeg)
    {
        _pet = pet;
        _companion = companion;
        _director = director;
        _builtInDir = builtInVideosDir;
        _ffmpeg = ffmpeg;
        Settings = settings;
        Hub = new ProvidersViewModel(settings);
        Update = new UpdateViewModel(companion, settings);

        Pages =
        [
            new("General", "常规", "启动、窗口层级与录屏。",
                "M 12,8 A 4,4 0 1 0 12,16 A 4,4 0 1 0 12,8 Z M 12,2 L 12,5 M 12,19 L 12,22 M 2,12 L 5,12 M 19,12 L 22,12 M 4.9,4.9 L 7,7 M 17,17 L 19.1,19.1 M 4.9,19.1 L 7,17 M 17,7 L 19.1,4.9"),
            new("Pet", "桌宠", "角色外观、动画节奏与行为。",
                "M 12,3 A 9,9 0 1 0 12,21 A 9,9 0 1 0 12,3 Z M 8,14 Q 12,18 16,14 M 9,9 L 9,10 M 15,9 L 15,10"),
            new("Interaction", "互动", "点击、双击、拖拽与摸头时它会怎么回应。",
                "M 9,12 L 9,5 A 1.5,1.5 0 0 1 12,5 L 12,11 M 12,10 L 12,9 A 1.5,1.5 0 0 1 15,9 L 15,12 M 15,11 A 1.5,1.5 0 0 1 18,11 L 18,15 A 6,6 0 0 1 12,21 L 11.5,21 A 6,6 0 0 1 6.5,18 L 4,14 A 1.5,1.5 0 0 1 7,13 L 9,15"),
            new("Menu", "菜单", "右键宠物时弹出的菜单项和顺序。",
                "M 4,6 L 20,6 M 4,12 L 20,12 M 4,18 L 14,18"),
            new("Widgets", "桌面组件", "问候、报时、喝水与久坐提醒、番茄钟。",
                "M 4,4 L 10,4 L 10,10 L 4,10 Z M 14,4 L 20,4 L 20,10 L 14,10 Z M 4,14 L 10,14 L 10,20 L 4,20 Z M 14,14 L 20,14 L 20,20 L 14,20 Z"),
            new("Ai", "智能", "大模型接口、对话参数、人设与记忆。",
                "M 12,3 L 14,10 L 21,12 L 14,14 L 12,21 L 10,14 L 3,12 L 10,10 Z"),
            new("Voice", "语音", "用系统语音朗读回复和提醒。",
                "M 9,5 A 3,3 0 0 1 15,5 L 15,11 A 3,3 0 0 1 9,11 Z M 5,10 A 7,7 0 0 0 19,10 M 12,17 L 12,21"),
        ];
        _page = Pages[0];
        _page.IsActive = true;
        if (Environment.GetEnvironmentVariable("ASTRA_PET_PAGE") is { } start) Navigate(start);
        if (Environment.GetEnvironmentVariable("ASTRA_PET_DIALOG") is { } dialog)
            Dispatcher.UIThread.Post(() => Hub.OpenForTest(dialog));

        ActionOptions = BuildActionOptions();
        RefreshPets(settings.PetId);

        // 设置文件里的开关可能和系统实际状态不一致（比如用户手动删了 plist），以系统为准。
        if (AutoStart.IsSupported) settings.AutoStart = AutoStart.IsEnabled();

        settings.PropertyChanged += OnSettingChanged;
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
        _ = LoadVoicesAsync();
    }

    public PetSettings Settings { get; }
    public ProvidersViewModel Hub { get; }
    public UpdateViewModel Update { get; }
    public NavItem[] Pages { get; }
    public ActionOption[] ActionOptions { get; private set; }

    // ───── 布局 ─────

    public bool SidebarExpanded
    {
        get => _sidebarExpanded;
        set
        {
            if (!Set(ref _sidebarExpanded, value)) return;
            Raise(nameof(SidebarWidth));
            Raise(nameof(SidebarLabelOpacity));
            Raise(nameof(NavPadding));
            Raise(nameof(SidebarToggleMargin));
        }
    }

    // 收起时宽度要容得下左上角的三个窗口按钮（约 22 到 82）。
    public double SidebarWidth => SidebarExpanded ? 184 : 72;

    /// <summary>侧栏文字的透明度。用淡出代替直接隐藏，宽度过渡时才不会闪。</summary>
    public double SidebarLabelOpacity => SidebarExpanded ? 1 : 0;

    /// <summary>导航按钮的内边距：展开时图标靠左，收起后挪到按钮正中（按钮内宽 54，图标 15）。</summary>
    public Thickness NavPadding => SidebarExpanded ? new Thickness(10, 0) : new Thickness(19.5, 0, 0, 0);

    /// <summary>收起后标题隐藏，右侧的展开按钮往左挪 12，落在侧栏正中。</summary>
    public Thickness SidebarToggleMargin => SidebarExpanded ? default : new Thickness(0, 0, 12, 0);

    /// <summary>导航按钮高 34 加间距 3，和 XAML 里的尺寸对应。</summary>
    private const double NavPitch = 37;

    /// <summary>选中高亮胶囊的位置：第几项乘以每项的高度。</summary>
    public Thickness NavHighlightMargin => new(0, Array.IndexOf(Pages, _page) * NavPitch, 0, 0);
    public string CurrentKey => _page.Key;
    public string PageTitle => _page.Label;
    public string PageDescription => _page.Description;
    public bool IsGeneral => _page.Key == "General";
    public bool IsPet => _page.Key == "Pet";
    public bool IsInteraction => _page.Key == "Interaction";
    public bool IsMenu => _page.Key == "Menu";
    public bool IsWidgets => _page.Key == "Widgets";
    public bool IsAi => _page.Key == "Ai";
    public bool IsVoice => _page.Key == "Voice";

    public void Navigate(string key)
    {
        if (Pages.FirstOrDefault(p => p.Key == key) is not { } page || page == _page) return;
        _page.IsActive = false;
        _page = page;
        page.IsActive = true;
        foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(IsGeneral), nameof(IsPet),
                     nameof(IsInteraction), nameof(IsMenu), nameof(IsWidgets), nameof(IsAi), nameof(IsVoice),
                     nameof(NavHighlightMargin) })
            Raise(name);
    }

    // ───── 常规 ─────

    public bool AutoStartSupported => AutoStart.IsSupported;
    public string SettingsPath => SettingsStore.FilePath;
    public string PlatformNote => OperatingSystem.IsMacOS()
        ? "全屏隐藏：宠物不进入其他应用的全屏空间。录屏：关闭后 OBS 的窗口捕获和屏幕捕获里都看不到宠物。"
        : "全屏隐藏目前只在 macOS 上生效。录屏开关需要 Windows 10 2004 及以上。";

    /// <summary>源码仓库（侧栏左下角的 GitHub 按钮）。</summary>
    public const string RepositoryUrl = "https://github.com/239573049/Astra-Pet";

    /// <summary>用系统默认浏览器打开源码仓库。</summary>
    public void OpenRepository()
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception e) { PetLog.Write($"打开 GitHub 失败: {e.Message}"); }
    }

    /// <summary>程序目录下的 licenses 文件夹：LICENSE、宠物素材许可、第三方声明和 LGPL 全文。</summary>
    public void OpenLicensesFolder()
    {
        // macOS 的 .app 里非代码文件在 Contents/Resources，其他情况在程序旁边。
        var dir = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "licenses"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "licenses")),
        }.FirstOrDefault(Directory.Exists);
        if (dir is null) { PetLog.Write("找不到许可文件夹"); return; }
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception e) { PetLog.Write($"打开许可文件夹失败: {e.Message}"); }
    }

    public void OpenSettingsFolder() => OpenFolder(SettingsStore.Directory);

    /// <summary>目录不存在就先建出来，再用系统的文件管理器打开。</summary>
    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        var opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        try { Process.Start(opener, directory); }
        catch (Exception e) { PetLog.Write($"打开目录失败: {e.Message}"); }
    }

    // ───── 桌宠 ─────

    public bool SingAvailable => _director.Library.Has(PetLibrary.Sing);
    public string MusicNote => !SystemMonitor.CanDetectAudio
        ? "当前系统暂不支持检测音乐播放。"
        : SingAvailable
            ? "检测到有声音在播放时（音乐、视频都算）改播唱歌动画。"
            : "检测到在放音乐时唱歌。还没有唱歌素材（Assets/videos/sing），暂时用动作片段代替。";

    public string StatusText => Settings switch
    {
        { Visible: false } => "已隐藏",
        { Paused: true } => "已暂停",
        { PowerSavingActive: true } => "省电中",
        { MusicPlaying: true, MusicDetect: true } => "正在唱歌",
        _ => "运行中",
    };

    public void ResetPosition() => _pet.ResetPosition();

    // ───── 角色 ─────

    public string PetsDirectory => PetCatalog.Directory;

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
                             (_currentPet.IsBuiltIn || _currentPet.Id == _currentPet.Name ? "" : $" · 文件夹 {_currentPet.Id}");

    /// <summary>目录里素材不全的文件夹，一行一个，说明缺什么。没有就是空串。</summary>
    public string PetProblems { get => _petProblems; private set { if (Set(ref _petProblems, value)) Raise(nameof(HasPetProblems)); } }
    public bool HasPetProblems => _petProblems.Length > 0;

    /// <summary>切换的进度和失败原因。</summary>
    public string PetStatus { get => _petStatus; private set { if (Set(ref _petStatus, value)) Raise(nameof(HasPetStatus)); } }
    public bool HasPetStatus => _petStatus.Length > 0;

    public void OpenPetsFolder() => OpenFolder(PetCatalog.Directory);

    /// <summary>重新扫描角色目录。用户往里拷了新角色后点「刷新」。</summary>
    public void RefreshPets() => RefreshPets(_currentPet.Id);

    private void RefreshPets(string currentId)
    {
        // 选中项会被重置回当前角色，还没加载完的切换也得一起取消，免得界面和实际对不上。
        _petSwitch++;
        PetStatus = "";
        var all = PetCatalog.Scan(_builtInDir);
        _currentPet = all.FirstOrDefault(p => p.Id == currentId) ?? all[0];
        PetProblems = string.Join("\n", all.Where(p => p.Problem is not null).Select(p => $"{p.Id}：{p.Problem}"));
        Pets = all.Where(p => p.Problem is null).ToList();
        // Pets 换了之后 ComboBox 的选中项被清掉了，重新选一遍。
        _selectedPet = Pets.FirstOrDefault(p => p == _currentPet);
        Raise(nameof(SelectedPet));
        Raise(nameof(PetInfo));
    }

    private async Task SwitchPetAsync(PetEntry pet)
    {
        var ticket = ++_petSwitch;
        PetStatus = $"正在加载「{pet.Name}」…";
        try
        {
            // 每段都要起一次 ffmpeg 探测，放到线程池上，期间宠物照常动。
            var library = await Task.Run(() => PetLibrary.Load(pet.VideosDir, _ffmpeg, PetLog.Write));
            if (ticket != _petSwitch) return;   // 加载期间又选了别的，以最后一次为准
            _director.UseLibrary(library);
            _currentPet = pet;
            Settings.PetId = pet.Id;
            PetStatus = "";
            PetLog.Write($"已切换角色: {pet.Name} ({pet.VideosDir})");
            Raise(nameof(PetInfo));
            Raise(nameof(SingAvailable));
            Raise(nameof(MusicNote));
            ActionOptions = BuildActionOptions();
            Raise(nameof(ActionOptions));
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

    public void MoveMenuItem(MenuEntry entry, int delta)
    {
        var items = Settings.MenuItems;
        var from = items.IndexOf(entry);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= items.Count) return;
        items.Move(from, to);
    }

    public void ResetMenu()
    {
        Settings.MenuItems.Clear();
        Settings.Normalize();
    }

    // ───── 桌面组件 ─────

    public string PomodoroStatus { get => _pomodoroStatus; private set => Set(ref _pomodoroStatus, value); }
    public string PomodoroButtonText => _companion.PomodoroRunning ? "停止" : "开始专注";

    public void TogglePomodoro()
    {
        if (_companion.PomodoroRunning) _companion.StopPomodoro(); else _companion.StartPomodoro();
        RefreshStatus();
    }

    public void TestBubble() => _companion.Say($"现在是 {DateTime.Now:HH:mm}，这是一条测试提醒～");

    // ───── 智能 ─────

    public void ResetSystemPrompt() => Settings.SystemPrompt = PetSettings.DefaultSystemPrompt;

    public string PromptPreview { get => _promptPreview; private set => Set(ref _promptPreview, value); }

    public void RefreshPromptPreview() => PromptPreview = _companion.Agent.BuildSystemPrompt();

    public string NewMemory { get => _newMemory; set => Set(ref _newMemory, value); }

    public void AddMemory()
    {
        if (_companion.Agent.AddMemory(NewMemory, auto: false)) NewMemory = "";
    }

    public void RemoveMemory(MemoryItem item) => Settings.Memories.Remove(item);

    public void ClearMemories() => Settings.Memories.Clear();

    // ───── 语音 ─────

    public bool SpeechSupported => Speech.IsSupported;

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

    public void StopSpeech() => Speech.Stop();

    private async Task LoadVoicesAsync()
    {
        var voices = await Task.Run(Speech.ListVoices);
        Voices = voices;
        _selectedVoice = voices.FirstOrDefault(v => v.Name == Settings.Voice) ?? voices[0];
        Raise(nameof(SelectedVoice));
    }

    // ───── 公共 ─────

    public void OpenChat() => _companion.RequestChat();

    public void Exit() => _pet.Close();

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetSettings.AutoStart):
                if (AutoStart.IsSupported && AutoStart.IsEnabled() != Settings.AutoStart
                    && !AutoStart.Apply(Settings.AutoStart, _builtInDir, _ffmpeg))
                    Dispatcher.UIThread.Post(() => Settings.AutoStart = AutoStart.IsEnabled());
                break;
            case nameof(PetSettings.Visible):
            case nameof(PetSettings.Paused):
            case nameof(PetSettings.PowerSavingActive):
            case nameof(PetSettings.MusicPlaying):
            case nameof(PetSettings.MusicDetect):
                Raise(nameof(StatusText));
                break;
        }
    }

    private void RefreshStatus()
    {
        PomodoroStatus = _companion.PomodoroStatus;
        Raise(nameof(PomodoroButtonText));
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        Settings.PropertyChanged -= OnSettingChanged;
    }
}
