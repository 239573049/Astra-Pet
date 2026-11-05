using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json;
using AstraPet.Core;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AstraPet.Studio;

/// <summary>
/// 打开的一个工程：⓪～⑧ 九步（04 §3）。文本框直接改 <see cref="ProjectData"/>，每 1.5 秒比对一次有改动就存盘并刷新各步状态。
/// 慢的事（解码图片、抠图、打包）放到线程池上，期间不自动存盘，免得和后台改数据撞上。
/// </summary>
public sealed partial class ProjectViewModel : Observable, IDisposable
{
    private readonly StudioViewModel _owner;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _previewTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly HashSet<StepId> _running = [];
    private readonly HashSet<string> _processing = [];
    private string _lastSaved = "";
    private int _background;
    private StepItem _step;
    private string _message = "";
    private bool _messageError;
    private string _figureProblems = "";
    private int _imageCount = 2;

    public ProjectViewModel(StudioViewModel owner, StudioProject project)
    {
        _owner = owner;
        Project = project;
        Steps =
        [
            new(StepId.Bind, "⓪", "绑定模型", "选图片和视频模型，填 API Key"),
            new(StepId.Describe, "①", "描述角色", "一句话描述、参考图、风格"),
            new(StepId.Sheet, "②", "设定图", "先定下长什么样"),
            new(StepId.Figure, "③", "站姿与首尾帧", "每段动画的第一帧和最后一帧"),
            new(StepId.Plan, "④", "规划动作", "要做哪些动作、每段的提示词"),
            new(StepId.Generate, "⑤", "生成视频", "提交给视频模型，等它做完"),
            new(StepId.Process, "⑥", "处理与挑选", "抠像、对齐、挑一段最好的"),
            new(StepId.Tryout, "⑦", "试用", "放到桌面上看看效果"),
            new(StepId.Export, "⑧", "导出", "打成角色包，装上或者分享"),
        ];
        foreach (var a in project.Data.Actions) PlanItems.Add(new PlanItem(a));
        foreach (var r in project.Data.References) References.Add(new ImageItem(r, project.PathOf(r)));
        LoadCandidates();
        InitLife();
        RebuildRows();
        _lastSaved = Serialize();
        RefreshStates();
        _step = Steps.FirstOrDefault(s => s.State is StepState.Ready or StepState.Stale) ?? Steps[^1];
        _step.IsSelected = true;

        _autosave.Tick += (_, _) => SaveIfChanged();
        _autosave.Start();
        _previewTick.Tick += (_, _) => { if (IsGenerate) foreach (var row in Rows) foreach (var take in row.Takes) take.Tick(); };
        _previewTick.Start();
        owner.Engine.Queue.Changed += OnTaskChanged;
        owner.Engine.Queue.WaitingChanged += OnWaitingChanged;

        // 打开时：没在跟踪的任务接着查（启动时已经恢复过的会跳过），下载好了还没处理的接着处理。
        var resumed = owner.Engine.Queue.Resume(project);
        if (resumed > 0) Say($"接着查询 {resumed} 个没完成的视频任务（不会重新提交）");
        _ = ProcessPendingAsync();
    }

    public StudioProject Project { get; }
    public ProjectData Data => Project.Data;
    public StudioViewModel Owner => _owner;
    private StudioEngine Engine => _owner.Engine;

    public string Title => Data.Name;
    public string SpentText => "已花费约 " + StudioViewModel.Money(Project.LedgerTotal());

    // ───── 导航和状态 ─────

    public IReadOnlyList<StepItem> Steps { get; }

    public StepItem Step => _step;

    public void Select(StepItem step)
    {
        if (step == _step) return;
        SaveNow();
        _step.IsSelected = false;
        _step = step;
        step.IsSelected = true;
        Message = "";
        Raise(nameof(Step));
        foreach (var name in StepFlags) Raise(name);
        if (step.Id is StepId.Generate or StepId.Process or StepId.Tryout) RebuildRows();
        if (step.Id == StepId.Plan) RaisePlan();
        if (step.IsLocked) Say("前面的步骤还没完成，这一步暂时用不了", error: true);
    }

    private static readonly string[] StepFlags =
        [nameof(IsBind), nameof(IsDescribe), nameof(IsSheet), nameof(IsFigure), nameof(IsPlan), nameof(IsGenerate), nameof(IsProcess), nameof(IsTryout), nameof(IsExport)];

    public bool IsBind => _step.Id == StepId.Bind;
    public bool IsDescribe => _step.Id == StepId.Describe;
    public bool IsSheet => _step.Id == StepId.Sheet;
    public bool IsFigure => _step.Id == StepId.Figure;
    public bool IsPlan => _step.Id == StepId.Plan;
    public bool IsGenerate => _step.Id == StepId.Generate;
    public bool IsProcess => _step.Id == StepId.Process;
    public bool IsTryout => _step.Id == StepId.Tryout;
    public bool IsExport => _step.Id == StepId.Export;

    public void Next()
    {
        var i = Steps.ToList().IndexOf(_step);
        if (i >= Steps.Count - 1) return;
        // 下一步还没解锁就留在这一步，直接说清楚差什么，别跳过去再报「前面没完成」
        if (Steps[i + 1].IsLocked) { Say(BlockedReason(_step.Id), error: true); return; }
        Select(Steps[i + 1]);
    }

    private string BlockedReason(StepId id) => id switch
    {
        StepId.Sheet when SheetCandidates.Count > 0 => "还没选定设定图：点一下上面的图片选定，再点下一步",
        StepId.Sheet => "先生成或上传设定图，再点一张选定",
        StepId.Figure when FigureCandidates.Count > 0 => "还没选定站姿：点一下上面的图片，选定后会自动做首尾帧",
        StepId.Figure => "先生成或上传站姿，再点一张选定",
        _ => "这一步还没完成，完成后才能进行下一步",
    };

    /// <summary>有候选但还没选：在候选图下面显示「点一张选定」，不然只有悬停提示，看不出图能点。</summary>
    public bool SheetNeedsPick => SheetCandidates.Count > 0 && !SheetCandidates.Any(c => c.IsSelected);
    public bool FigureNeedsPick => FigureCandidates.Count > 0 && !FigureCandidates.Any(c => c.IsSelected);

    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public bool MessageError { get => _messageError; private set { if (Set(ref _messageError, value)) Raise(nameof(MessageTone)); } }
    public TKCode.Arc.AlertTone MessageTone => _messageError ? TKCode.Arc.AlertTone.Danger : TKCode.Arc.AlertTone.Info;

    private void Say(string text, bool error = false)
    {
        MessageError = error;
        Message = text;
    }

    public void RefreshStates()
    {
        var imageReady = _owner.ImageReady;
        foreach (var s in Steps) s.State = Project.StateOf(s.Id, imageReady, IsRunning);
        Raise(nameof(SpentText));
        Raise(nameof(ImageEstimate));
        Raise(nameof(PlanProblem));
        Raise(nameof(HasPlanProblem));
        Raise(nameof(PlanEstimate));
        Raise(nameof(CanGenerateVideos));
        Raise(nameof(VideoNote));
        Raise(nameof(ExportNote));
    }

    private bool IsRunning(StepId step) => _running.Contains(step)
        || (step == StepId.Generate && Rows.Any(r => r.Waiting > 0 || r.Takes.Any(t => !t.Record.IsFinished)))
        || (step == StepId.Process && _processing.Count > 0);

    private async Task<T> RunAsync<T>(StepId step, Func<T> work)
    {
        SaveNow();
        _running.Add(step);
        _background++;
        RefreshStates();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _background--;
            _running.Remove(step);
            _lastSaved = Serialize();   // 后台那一步自己存过盘了
            RefreshStates();
        }
    }

    private async Task RunTaskAsync(StepId step, Func<Task> work)
    {
        SaveNow();
        _running.Add(step);
        _background++;
        RefreshStates();
        try
        {
            await Task.Run(work);
        }
        finally
        {
            _background--;
            _running.Remove(step);
            _lastSaved = Serialize();
            RefreshStates();
        }
    }

    // ───── 存盘 ─────

    private string Serialize() => JsonSerializer.Serialize(Data, StudioJsonContext.Default.ProjectData);

    private void SaveIfChanged()
    {
        if (_background > 0) return;
        var now = Serialize();
        if (now == _lastSaved) return;
        try
        {
            Project.Save();
            _lastSaved = Serialize();
        }
        catch (IOException e)
        {
            Say("保存失败：" + e.Message, error: true);
            return;
        }
        RefreshStates();
    }

    public void SaveNow() => SaveIfChanged();

    // ───── ① 描述 ─────

    public IReadOnlyList<string> Styles => StudioPrompts.Styles;
    public IReadOnlyList<double> HeadRatios { get; } = [2, 2.5, 3, 3.5, 4.5, 6, 7.5];

    public string Description { get => Data.Description; set => Data.Description = value ?? ""; }
    public string Style { get => Data.Style; set { if (value is not null) Data.Style = value; } }
    public double HeadRatio { get => Data.HeadRatio; set => Data.HeadRatio = value; }
    public string Companion { get => Data.Companion; set => Data.Companion = value ?? ""; }
    public string CardName { get => Data.Card.Name; set => Data.Card.Name = value ?? ""; }
    public string Species { get => Data.Card.Species; set => Data.Card.Species = value ?? ""; }
    public string Appearance { get => Data.Card.Appearance; set => Data.Card.Appearance = value ?? ""; }
    public string Palette { get => string.Join("、", Data.Card.Palette); set => Data.Card.Palette = PlanItem.Split(value, '、', ',', '，'); }
    public string Props { get => string.Join("、", Data.Card.Props); set => Data.Card.Props = PlanItem.Split(value, '、', ',', '，'); }
    public string Personality { get => Data.Card.Personality; set => Data.Card.Personality = value ?? ""; }
    public string Speech { get => Data.Card.Speech; set => Data.Card.Speech = value ?? ""; }
    public string Likes { get => string.Join("、", Data.Card.Likes); set => Data.Card.Likes = PlanItem.Split(value, '、', ',', '，'); }
    public string Greeting { get => Data.Card.Greeting; set => Data.Card.Greeting = value ?? ""; }

    public ObservableCollection<ImageItem> References { get; } = [];

    public async Task AddReferencesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var rel = await RunAsync(StepId.Describe, () => Engine.AddReference(Project, path));
                References.Add(new ImageItem(rel, Project.PathOf(rel)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Say($"「{Path.GetFileName(path)}」读不出来：{e.Message}", error: true);
            }
        }
    }

    public void RemoveReference(ImageItem item)
    {
        Data.References.Remove(item.Rel);
        References.Remove(item);
        SaveNow();
    }

    // ───── ② ③ 图片 ─────

    public IReadOnlyList<int> ImageCounts { get; } = [1, 2, 3, 4];
    public int ImageCount { get => _imageCount; set { if (Set(ref _imageCount, value)) Raise(nameof(ImageEstimate)); } }

    /// <summary>给分段选择用的字符串形式。</summary>
    public string ImageCountValue
    {
        get => ImageCount.ToString(CultureInfo.InvariantCulture);
        set { if (int.TryParse(value, CultureInfo.InvariantCulture, out var n) && ImageCounts.Contains(n)) { ImageCount = n; Raise(nameof(ImageCountValue)); } }
    }

    public string ImageEstimate => _owner.ImageProvider is { } p
        ? $"{p.Name} · 预估 {StudioViewModel.Money(p.PriceOf("*") * _imageCount)}" : "还没选图片模型";

    public ObservableCollection<ImageItem> SheetCandidates { get; } = [];
    public ObservableCollection<ImageItem> FigureCandidates { get; } = [];

    public string SheetPrompt { get => Data.Sheet.Prompt; set => Data.Sheet.Prompt = value ?? ""; }
    public string FigurePrompt { get => Data.Figure.Prompt; set => Data.Figure.Prompt = value ?? ""; }

    public bool SheetBusy => _running.Contains(StepId.Sheet);
    public bool FigureBusy => _running.Contains(StepId.Figure);

    /// <summary>生成中的占位卡，张数和这次要生成的一样，图回来就清掉。</summary>
    public ObservableCollection<PendingImage> SheetPending { get; } = [];
    public ObservableCollection<PendingImage> FigurePending { get; } = [];

    private string _waitText = "";
    /// <summary>生成时占位卡上方那行会闪的状态：几张、等了多久。</summary>
    public string WaitText { get => _waitText; private set => Set(ref _waitText, value); }

    public void FillSheetPrompt()
    {
        SheetPrompt = StudioPrompts.Sheet(Data, Data.References.Count > 0);
        Raise(nameof(SheetPrompt));
    }

    public void FillFigurePrompt()
    {
        FigurePrompt = StudioPrompts.Figure(Data);
        Raise(nameof(FigurePrompt));
    }

    private void LoadCandidates()
    {
        SheetCandidates.Clear();
        foreach (var c in Data.Sheet.Candidates.Where(c => File.Exists(Project.PathOf(c))))
            SheetCandidates.Add(new ImageItem(c, Project.PathOf(c)) { IsSelected = c == Data.Sheet.Selected });
        FigureCandidates.Clear();
        foreach (var c in Data.Figure.Candidates.Where(c => File.Exists(Project.PathOf(c))))
            FigureCandidates.Add(new ImageItem(c, Project.PathOf(c)) { IsSelected = c == Data.Figure.Selected });
        Raise(nameof(SheetNeedsPick));
        Raise(nameof(FigureNeedsPick));
        Raise(nameof(FirstFrame));
        Raise(nameof(HasFirstFrame));
        Raise(nameof(KeyColor));
        Raise(nameof(KeyColorId));
    }

    public async Task GenerateSheetsAsync()
    {
        if (_owner.ImageProvider is not { IsConfigured: true } provider) { Say("先在 ⓪ 配好图片模型", error: true); return; }
        if (!Project.HasDescription) { Say("先在 ① 写一句描述或者放一张参考图", error: true); return; }
        await GenerateImagesAsync(StepId.Sheet, "设定图", () => Engine.GenerateSheetsAsync(Project, provider, _imageCount, _life.Token));
        Raise(nameof(SheetPrompt));
    }

    public async Task GenerateFiguresAsync()
    {
        if (_owner.ImageProvider is not { IsConfigured: true } provider) { Say("先在 ⓪ 配好图片模型", error: true); return; }
        if (Data.Sheet.Selected is null) { Say("先在 ② 选一张设定图", error: true); return; }
        await GenerateImagesAsync(StepId.Figure, "站姿", () => Engine.GenerateFiguresAsync(Project, provider, _imageCount, _life.Token));
        Raise(nameof(FigurePrompt));
    }

    private async Task GenerateImagesAsync(StepId step, string what, Func<Task<IReadOnlyList<string>>> generate)
    {
        // 接口一次请求出全部图、不报进度：放同样张数的占位卡，按已等时间渐近地走进度，图回来再换成真图
        var pending = step == StepId.Sheet ? SheetPending : FigurePending;
        var (aspect, width) = step == StepId.Sheet ? (1.5, 300.0) : (140.0 / 210.0, 140.0);
        var count = _imageCount;
        pending.Clear();
        for (var i = 0; i < count; i++) pending.Add(new PendingImage(Random.Shared.Next(), aspect, width));
        var started = DateTime.UtcNow;
        void Tick()
        {
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            var progress = 0.95 * (1 - Math.Exp(-seconds / 45));   // 45 秒约 60%，两分钟约 88%
            foreach (var p in pending) p.Progress = progress;
            WaitText = $"正在生成 {count} 张{what} · 已等 {(int)seconds} 秒 · 一般要半分钟到两分钟";
        }
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        Tick();
        timer.Start();
        Say("");
        RaiseBusy();
        try
        {
            IReadOnlyList<string> saved = [];
            await RunTaskAsync(step, async () => saved = await generate());
            timer.Stop();
            pending.Clear();
            LoadCandidates();
            Say(saved.Count == 0 ? "已取消，没有发出请求" : $"生成了 {saved.Count} 张{what}，点一张选定");
        }
        catch (Exception e) when (e is GenerationException or IOException or InvalidDataException or InvalidOperationException)
        {
            Say($"{what}生成失败：{e.Message}", error: true);
        }
        catch (OperationCanceledException)
        {
            Say("已取消", error: true);
        }
        finally
        {
            timer.Stop();
            pending.Clear();
            RaiseBusy();
        }
    }

    private void RaiseBusy()
    {
        Raise(nameof(SheetBusy));
        Raise(nameof(FigureBusy));
        Raise(nameof(ExportBusy));
        Raise(nameof(TryoutBusy));
    }

    public async Task ImportCandidatesAsync(string folder, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await RunAsync(folder == "sheet" ? StepId.Sheet : StepId.Figure, () => Engine.ImportCandidate(Project, folder, path));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Say($"「{Path.GetFileName(path)}」读不出来：{e.Message}", error: true);
            }
        }
        LoadCandidates();
    }

    public async Task SelectSheetAsync(ImageItem item)
    {
        try
        {
            var before = Data.KeyColor;
            await RunAsync(StepId.Sheet, () => { Engine.SelectSheet(Project, item.Rel); return 0; });
            LoadCandidates();
            Raise(nameof(FigurePrompt));
            Say(Data.KeyColor != before
                ? $"已选定。角色身上的颜色和{StudioPrompts.KeyName(before)}背景太像，站姿改用{StudioPrompts.KeyName(Data.KeyColor)}背景"
                : "已选定，下一步生成站姿");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Say("选定失败：" + e.Message, error: true);
        }
    }

    public async Task SelectFigureAsync(ImageItem item)
    {
        try
        {
            var outcome = await RunAsync(StepId.Figure, () => Engine.SelectFigure(Project, item.Rel));
            LoadCandidates();
            FigureProblems = string.Join("\n", outcome.Problems);
            Raise(nameof(Prefix));
            Say(outcome.Problems.Count == 0 ? "首尾帧做好了，检查一下脚是不是在下方横线上、头有没有被切" : "首尾帧做好了，但自动检查发现了问题（见下方）", outcome.Problems.Count > 0);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            FigureProblems = "";
            Say(e.Message, error: true);
        }
    }

    public string FigureProblems { get => _figureProblems; private set { if (Set(ref _figureProblems, value)) Raise(nameof(HasFigureProblems)); } }
    public bool HasFigureProblems => _figureProblems.Length > 0;

    public IReadOnlyList<Choice> KeyColors { get; } = StudioPrompts.KeyColors.Select(k => new Choice(k, StudioPrompts.KeyName(k) + "背景")).ToList();

    /// <summary>抠像色：选设定图时自动定，也可以手动改；改了自动用当前站姿重新合成首尾帧。</summary>
    public Choice? KeyColor
    {
        get => KeyColors.FirstOrDefault(k => k.Id == Data.KeyColor);
        set
        {
            if (value is null || value.Id == Data.KeyColor) return;
            Data.KeyColor = value.Id;
            Data.Figure.Prompt = "";
            Raise(nameof(FigurePrompt));
            Raise(nameof(KeyColor));
            Raise(nameof(KeyColorId));
            if (FigureCandidates.FirstOrDefault(c => c.IsSelected) is { } selected) _ = SelectFigureAsync(selected);
            else SaveNow();
        }
    }

    /// <summary>给分段选择用：抠像色的 id。</summary>
    public string? KeyColorId
    {
        get => Data.KeyColor;
        set => KeyColor = KeyColors.FirstOrDefault(k => k.Id == value);
    }

    public Bitmap? FirstFrame => Thumbs.Load(Project.PathOf(StudioProject.FirstFrameFile), 640);
    public bool HasFirstFrame => FirstFrame is not null;

    public void OpenFolder(string rel) => _owner.Shell.OpenFolder(Project.PathOf(rel));

    // ───── ④ 规划 ─────

    public ObservableCollection<PlanItem> PlanItems { get; } = [];
    public IReadOnlyList<string> Resolutions => StudioPrompts.Resolutions;
    public IReadOnlyList<int> Durations => PlanItem.Durations;

    public IReadOnlyList<ActionTemplate> TemplateChoices =>
        StudioPrompts.Templates.Where(t => Data.Actions.All(a => a.Template != t.Id && a.Id != t.Id)).ToList();

    /// <summary>宠物体系的模板（design/pet-life/05 §1 ④）：地面上的吃、饿、下落……，墙组、坐、睡。</summary>
    public IReadOnlyList<ActionTemplate> LifeTemplateChoices =>
        StudioPrompts.LifeTemplates.Where(t => Data.Actions.All(a => a.Template != t.Id && a.Id != t.Id)).ToList();

    /// <summary>④ 的提醒：不完整的姿态组。</summary>
    public string PlanWarnings => string.Join("\n", Project.PlanWarnings());
    public bool HasPlanWarnings => Project.PlanWarnings().Count > 0;

    public bool HasTemplateChoices => TemplateChoices.Count > 0;

    public string Prefix { get => Data.Prefix; set => Data.Prefix = value ?? ""; }
    public string Resolution
    {
        get => Data.Resolution;
        set
        {
            if (value is null || value == Data.Resolution) return;
            if (VideoModel is { } m && !m.Supports(value)) return;   // Fast / Mini 选不了 1080p
            Data.Resolution = value;
            Raise(nameof(Resolution));
            Raise(nameof(PlanEstimate));
            RefreshStates();
        }
    }

    private VideoModel? VideoModel => MediaPresets.VideoModelOf(_owner.VideoProvider?.Model);
    public bool No1080 => VideoModel is { } m && !m.Supports("1080p");
    public string ResolutionNote => VideoModel is { } m ? $"{m.Name} 能出 {string.Join(" / ", m.Resolutions)}，默认用最高的 {m.Best}" : "";

    /// <summary>⓪ 换了视频模型：分辨率跟着换成这个模型能出的最高一档（Fast / Mini 是 720p，2.0 / 2.5 是 1080p）。</summary>
    public void UseModelResolution()
    {
        if (VideoModel is { } m && m.Best != Data.Resolution)
        {
            Data.Resolution = m.Best;
            Raise(nameof(Resolution));
            SaveNow();
        }
        Raise(nameof(No1080));
        Raise(nameof(ResolutionNote));
        RefreshStates();
    }

    /// <summary>打开工程时：所选分辨率当前模型出不了，就退到它能出的最高一档。</summary>
    internal void FitResolution()
    {
        if (VideoModel is { } m && !m.Supports(Data.Resolution)) { Data.Resolution = m.Best; SaveNow(); }
    }

    public string? PlanProblem => Project.PlanProblem();
    public bool HasPlanProblem => PlanProblem is not null;

    public string PlanEstimate => _owner.VideoProvider is { } p
        ? $"全部生成一遍：{Data.Actions.Sum(a => a.Duration)} 秒视频，{p.Name} · 预估 {StudioViewModel.Money(StudioEngine.VideoEstimate(Project, Data.Actions, p).Total)}"
        : "还没选视频模型（在 ⓪ 配）";

    public void ResetPrefix()
    {
        Prefix = StudioPrompts.Prefix(Data);
        Raise(nameof(Prefix));
    }

    public void AddTemplate(ActionTemplate template)
    {
        var action = template.Plan();
        Data.Actions.Add(action);
        PlanItems.Add(new PlanItem(action));
        RaisePlan();
    }

    public void AddBlank()
    {
        var n = 1;
        while (Data.Actions.Any(a => a.Id == $"action-{n}")) n++;
        var action = new PlannedAction { Id = $"action-{n}", Name = "新动作", Roles = ["happy"], Duration = 8 };
        Data.Actions.Add(action);
        PlanItems.Add(new PlanItem(action));
        RaisePlan();
    }

    // 自定义动作：名称 + 一句话描述 + 触发时机 + 时长，提示词按描述套时间轴生成（StudioPrompts.Custom）。

    private string _customName = "";
    private string _customDescription = "";
    private string _customDuration = "8";
    private IReadOnlyList<string> _customRoles = [Core.Roles.Click];

    public static IReadOnlyList<Choice> RoleChoices { get; } = StudioPrompts.RoleNames.Select(r => new Choice(r.Role, r.Name)).ToList();

    public string CustomName { get => _customName; set { if (Set(ref _customName, value ?? "")) Raise(nameof(CanAddCustom)); } }
    public string CustomDescription { get => _customDescription; set { if (Set(ref _customDescription, value ?? "")) Raise(nameof(CanAddCustom)); } }
    public string CustomDuration { get => _customDuration; set => Set(ref _customDuration, value ?? "8"); }

    /// <summary>选中的触发时机（角色 id），界面上的标签组改了就整体换掉。</summary>
    public IReadOnlyList<string> CustomRoles
    {
        get => _customRoles;
        set { _customRoles = value ?? []; Raise(nameof(CustomRoles)); Raise(nameof(CanAddCustom)); }
    }

    public bool CanAddCustom => _customName.Trim().Length > 0 && _customDescription.Trim().Length > 0 && _customRoles.Count > 0;

    public void AddCustom()
    {
        if (!CanAddCustom) { Say("自定义动作要写名称、想让它做什么，再选至少一个触发时机", error: true); return; }
        var duration = int.TryParse(_customDuration, out var d) && PlanItem.Durations.Contains(d) ? d : 8;
        var n = 1;
        while (Data.Actions.Any(a => a.Id == $"custom-{n}")) n++;
        var template = StudioPrompts.Custom(_customName, _customDescription, _customRoles.ToList(), duration);
        var action = template.Plan();
        action.Id = $"custom-{n}";
        Data.Actions.Add(action);
        PlanItems.Add(new PlanItem(action));
        CustomName = "";
        CustomDescription = "";
        RaisePlan();
        Say($"加好了「{action.Name}」，提示词在上面，可以再改");
    }

    public void RemovePlanItem(PlanItem item)
    {
        Data.Actions.Remove(item.Action);
        PlanItems.Remove(item);
        RaisePlan();
    }

    private void RaisePlan()
    {
        Raise(nameof(TemplateChoices));
        Raise(nameof(HasTemplateChoices));
        Raise(nameof(LifeTemplateChoices));
        Raise(nameof(PlanWarnings));
        Raise(nameof(HasPlanWarnings));
        SaveNow();
        RefreshStates();
    }

    public void ConfirmPlan()
    {
        SaveNow();
        if (Engine.ConfirmPlan(Project) is { } problem) { Say(problem, error: true); return; }
        _lastSaved = Serialize();
        RefreshStates();
        RebuildRows();
        Raise(nameof(Prefix));
        Say("规划已确认，下一步生成视频");
    }

    // ───── ⑤ 生成 ─────

    public ObservableCollection<ActionRow> Rows { get; } = [];

    public bool CanGenerateVideos => _owner.VideoReady && Steps[(int)StepId.Plan].State == StepState.Done;

    public string VideoNote =>
        !_owner.VideoReady ? "先在 ⓪ 配好视频模型" :
        Steps[(int)StepId.Plan].State != StepState.Done ? "先在 ④ 确认规划（改过首尾帧的话要重新确认）" :
        $"{_owner.VideoProvider!.Name} · {Data.Resolution}。每段一般要 1～5 分钟；可以关掉窗口甚至退出程序，下次打开接着查，不会重复提交。";

    private void RebuildRows()
    {
        var existing = Rows.ToDictionary(r => r.Action);
        Rows.Clear();
        foreach (var a in Data.Actions)
        {
            var row = existing.GetValueOrDefault(a) ?? new ActionRow(a);
            Rows.Add(row);
            RefreshRow(row);
        }
    }

    private void RefreshRow(ActionRow row)
    {
        var basis = Project.TakeBasis(row.Action);
        var old = row.Takes.ToDictionary(t => t.Take);
        var takes = Project.Takes(row.Id).OrderByDescending(t => t.Take)
            .Select(r =>
            {
                if (!old.TryGetValue(r.Take, out var item)) return new TakeItem(row, r, Project, r.Basis != basis);
                item.Update(r, Project, r.Basis != basis);   // 同一段沿用原来的实例：处理进度、预览动画都接着走
                return item;
            }).ToList();
        row.SetTakes(takes);
        row.Waiting = Engine.Queue.WaitingCount(Project, row.Id);
    }

    private ActionRow? RowOf(string action) => Rows.FirstOrDefault(r => r.Id == action);

    /// <summary>缺视频（或视频是按旧提示词做的）、又没在生成的动作。</summary>
    private List<PlannedAction> Missing() => Rows.Where(r =>
            r.Waiting == 0 && r.Takes.All(t => t.Record.IsFinished)
            && !r.Takes.Any(t => t.HasVideo && !t.IsStale))
        .Select(r => r.Action).ToList();

    public Task GenerateMissingAsync()
    {
        var missing = Missing();
        if (missing.Count == 0) { Say("每个动作都已经有视频了；想要别的版本就在那一行点「再来一段」"); return Task.CompletedTask; }
        return GenerateAsync(missing);
    }

    public Task GenerateIdleAsync()
    {
        var idle = Data.Actions.FirstOrDefault(a => a.Roles.Contains(Roles.Idle));
        return idle is null ? Task.CompletedTask : GenerateAsync([idle]);
    }

    public Task GenerateOneAsync(ActionRow row) => GenerateAsync([row.Action]);

    private async Task GenerateAsync(IReadOnlyList<PlannedAction> actions)
    {
        SaveNow();
        if (!CanGenerateVideos) { Say(VideoNote, error: true); return; }
        foreach (var a in actions) if (RowOf(a.Id) is { } r) r.Note = "";
        try
        {
            var tasks = await Engine.GenerateVideosAsync(Project, actions, _owner.VideoProvider!, CancellationToken.None);
            if (tasks.Count == 0) { Say("已取消，没有提交"); return; }
            Say($"已排队 {tasks.Count} 段，最多同时生成 2 段");
            foreach (var (action, task) in tasks) _ = WatchAsync(action, task);
        }
        catch (Exception e) when (e is GenerationException or InvalidOperationException or IOException)
        {
            Say(e.Message, error: true);
        }
        catch (OperationCanceledException) { }
        RefreshRowsAndStates();
    }

    private async Task WatchAsync(PlannedAction action, Task<TaskRecord> task)
    {
        try
        {
            await task;
        }
        catch (GenerationException e)
        {
            // 提交失败：没有任务记录，也不自动重试（可能已经计费），让用户决定要不要再点一次。
            if (RowOf(action.Id) is { } row) row.Note = "提交失败：" + e.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            PetLog.Write($"工作台：{action.Id} 出错：{e}");
            if (RowOf(action.Id) is { } row) row.Note = "出错了：" + e.Message;
        }
        RefreshRowsAndStates();
    }

    public async Task CancelTakeAsync(TakeItem take)
    {
        try
        {
            await Engine.Queue.CancelAsync(take.Record, _life.Token);
        }
        catch (Exception e) when (e is GenerationException or IOException)
        {
            take.Row.Note = "取消失败：" + e.Message;
        }
        RefreshRowsAndStates();
    }

    private void OnTaskChanged(TaskRecord record) => Dispatcher.UIThread.Post(() =>
    {
        if (_life.IsCancellationRequested || record.FilePath is not { } file
            || !file.StartsWith(Project.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        if (RowOf(record.Action) is { } row) RefreshRow(row);
        RefreshStates();
        if (record.Downloaded && !File.Exists(Project.ClipFile(record.Action, record.Take)))
            _ = ProcessAsync(record.Action, record.Take);
    });

    private void OnWaitingChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_life.IsCancellationRequested) return;
        foreach (var row in Rows) row.Waiting = Engine.Queue.WaitingCount(Project, row.Id);
        RefreshStates();
    });

    private void RefreshRowsAndStates()
    {
        foreach (var row in Rows) RefreshRow(row);
        RefreshStates();
    }

    // ───── ⑥ 处理与挑选 ─────

    private async Task ProcessPendingAsync()
    {
        foreach (var r in Project.AllTakes().Where(r => r.HasVideo(Project) && !File.Exists(Project.ClipFile(r.Action, r.Take))).ToList())
            await ProcessAsync(r.Action, r.Take);
    }

    public bool IsProcessingAny => _processing.Count > 0;

    public Task ReprocessAsync(TakeItem take) => ProcessAsync(take.Record.Action, take.Take);

    /// <summary>抠像、定位、编码（本地，免费）。一次只处理一段，ffmpeg 很占 CPU。还没采用过片段的动作自动采用第一段处理好的。</summary>
    private async Task ProcessAsync(string actionId, int take)
    {
        var key = $"{actionId}/{take}";
        if (Data.Actions.FirstOrDefault(a => a.Id == actionId) is not { } action || !_processing.Add(key)) return;
        RefreshStates();
        void Mark(bool on, string progress = "", double percent = 0)
        {
            if (RowOf(actionId)?.Takes.FirstOrDefault(t => t.Take == take) is { } item)
            {
                item.IsProcessing = on;
                item.Progress = progress;
                item.Percent = percent;
            }
        }
        Mark(true, "排队");
        try
        {
            await _processLock.WaitAsync(_life.Token);
            try
            {
                var progress = new Progress<double>(p => Mark(true, $"{p:0%}", p * 100));
                var report = await Task.Run(() => Engine.ProcessTakeAsync(Project, action, take, progress, _life.Token));
                if (action.Adopted is null || Project.AdoptedTake(action) is null)
                {
                    action.Adopted = take;
                    SaveNow();
                }
                if (RowOf(actionId) is { } row)
                    row.Note = report.Warnings.Count > 0 ? $"第 {take} 次：{string.Join("；", report.Warnings)}" : "";
            }
            finally
            {
                _processLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            PetLog.Write($"工作台：处理 {key} 失败：{e}");
            if (RowOf(actionId) is { } row) row.Note = $"第 {take} 次处理失败：{e.Message.Split('\n')[0]}";
        }
        finally
        {
            _processing.Remove(key);
            Mark(false);
            if (!_life.IsCancellationRequested) RefreshRowsAndStates();
        }
    }

    public void Adopt(TakeItem take)
    {
        take.Row.Action.Adopted = take.Take;
        SaveNow();
        RefreshRowsAndStates();
    }

    public void OpenRawFolder(ActionRow row) => _owner.Shell.OpenFolder(Project.RawDir(row.Id));

    // ───── ⑦ 试用 ─────

    private bool _tryoutBusy;
    public bool TryoutBusy { get => _tryoutBusy; private set => Set(ref _tryoutBusy, value); }
    public bool IsTryingOut => _owner.Host.IsTryingOut;

    /// <summary>临时打一个包（不写导出信息），直接换到桌面上，不改设置。</summary>
    public async Task TryoutAsync()
    {
        SaveNow();
        TryoutBusy = true;
        try
        {
            var path = Path.Combine(_owner.CacheRoot, "studio-tryout", Project.Id + ".astrapet");
            var name = await RunAsync(StepId.Tryout, () =>
            {
                var spec = Engine.BuildSpec(Project);
                PetPackWriter.Write(spec, path);
                return spec.Name;
            });
            await _owner.Host.TryoutAsync(path, name);
            Say("已经换到桌面上了。改了下面的参数后再点一次「放到桌面上试」；试完点「换回原来的角色」");
        }
        catch (Exception e)
        {
            PetLog.Write($"工作台：试用失败：{e}");
            Say("试用失败：" + e.Message.Split('\n')[0], error: true);
        }
        finally
        {
            TryoutBusy = false;
            Raise(nameof(IsTryingOut));
        }
    }

    public async Task EndTryoutAsync()
    {
        await _owner.Host.EndTryoutAsync();
        Raise(nameof(IsTryingOut));
        Say("已换回原来的角色");
    }

    public void Play(ActionRow row)
    {
        if (!_owner.Host.IsTryingOut) { Say("先点「放到桌面上试」", error: true); return; }
        if (!_owner.Host.PlayActionById(row.Id)) Say($"桌面上的角色里没有「{row.Name}」，改过规划的话重新放一次", error: true);
    }

    // ───── ⑧ 导出 ─────

    public IReadOnlyList<string> Licenses { get; } = ["保留所有权利", "CC BY 4.0", "CC BY-NC 4.0", "CC BY-NC-SA 4.0", "CC0 1.0"];

    public string ExportId { get => Data.Export.Id; set => Data.Export.Id = (value ?? "").Trim(); }
    public string ExportIdHint => "留空用 " + StudioEngine.SuggestId(ExportNameOrDefault, Project.Id);
    public string ExportName { get => Data.Export.Name; set { Data.Export.Name = value ?? ""; Raise(nameof(ExportIdHint)); } }
    private string ExportNameOrDefault => Data.Export.Name.Trim() is { Length: > 0 } n ? n : Data.Card.Name.Trim() is { Length: > 0 } c ? c : Data.Name;
    public string ExportNameHint => "留空用 " + ExportNameOrDefault;
    public string Author { get => Data.Export.Author; set => Data.Export.Author = value ?? ""; }
    public string License { get => Data.Export.License; set => Data.Export.License = value ?? ""; }
    public string LicenseUrl { get => Data.Export.LicenseUrl; set => Data.Export.LicenseUrl = value ?? ""; }
    public string Homepage { get => Data.Export.Homepage; set => Data.Export.Homepage = value ?? ""; }
    public bool IncludeSource { get => Data.Export.IncludeSource; set => Data.Export.IncludeSource = value; }

    public bool ExportBusy => _running.Contains(StepId.Export);

    public string ExportNote => Data.Export.LastPath.Length == 0 ? "还没导出过" :
        Steps[(int)StepId.Export].State == StepState.Done ? "上次导出：" + Data.Export.LastPath : "上次导出之后又改过，需要重新导出：" + Data.Export.LastPath;

    public string SuggestedFileName => StudioEngine.SuggestId(ExportNameOrDefault, Project.Id) + ".astrapet";

    public async Task<bool> ExportAsync(string path)
    {
        RaiseBusy();
        try
        {
            await RunAsync(StepId.Export, () => { Engine.Export(Project, path, _owner.Decoder, _owner.CacheRoot); return 0; });
            Say("导出好了，已经用加载角色的同一套检查验过：" + path);
            return true;
        }
        catch (Exception e)
        {
            PetLog.Write($"工作台：导出失败：{e}");
            Say("导出失败：" + e.Message.Split('\n')[0], error: true);
            return false;
        }
        finally
        {
            RaiseBusy();
            Raise(nameof(ExportNote));
        }
    }

    /// <summary>导出到工程的 export/ 下，再拷进「我的角色」并切过去。</summary>
    public async Task InstallAsync()
    {
        var path = Project.PathOf("export/" + SuggestedFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!await ExportAsync(path)) return;
        try
        {
            var installed = await _owner.Host.InstallPackAsync(path, Data.Export.Id);
            Raise(nameof(IsTryingOut));
            Say($"装好了，桌面上已经换成「{ExportNameOrDefault}」。文件在 {installed}，控制台「角色」里可以换回去");
        }
        catch (Exception e)
        {
            Say("安装失败：" + e.Message.Split('\n')[0], error: true);
        }
    }

    public void Dispose()
    {
        _autosave.Stop();
        _previewTick.Stop();
        SaveIfChanged();
        _life.Cancel();
        Engine.Queue.Changed -= OnTaskChanged;
        Engine.Queue.WaitingChanged -= OnWaitingChanged;
    }
}
