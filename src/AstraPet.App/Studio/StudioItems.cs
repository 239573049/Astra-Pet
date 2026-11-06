using System.Globalization;
using AstraPet.Core;
using AstraPet.Core.Studio;
using Avalonia.Media.Imaging;

namespace AstraPet.Studio;

/// <summary>工作台调用宿主（控制台）做的事：把包放到桌面上试、安装并切换、按 id 播动作。</summary>
public interface IStudioHost
{
    bool IsTryingOut { get; }
    Task TryoutAsync(string packPath, string name);
    Task EndTryoutAsync();
    Task<string> InstallPackAsync(string packPath, string id);
    bool PlayActionById(string id);
}

/// <summary>读图片文件做缩略图；文件不在或读坏了返回 null。按路径、修改时间和宽度缓存（只在界面线程用）。</summary>
public static class Thumbs
{
    private static readonly Dictionary<(string, DateTime, int), Bitmap> Cache = [];

    public static Bitmap? Load(string path, int width = 320)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return null;
            var key = (path, File.GetLastWriteTimeUtc(path), width);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (Cache.Count > 400) Cache.Clear();   // 不主动 Dispose：可能还有控件在显示
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, width);
            Cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception e)
        {
            PetLog.Write($"工作台：缩略图读不出来 {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>工作台窗口关掉后调：已经没有控件在显示，位图的原生内存可以直接还掉。</summary>
    public static void Clear()
    {
        foreach (var bitmap in Cache.Values) bitmap.Dispose();
        Cache.Clear();
    }
}

/// <summary>左侧的一步。</summary>
public sealed class StepItem(StepId id, string number, string title, string hint) : Observable
{
    private StepState _state;
    private bool _selected;

    public StepId Id { get; } = id;
    public string Number { get; } = number;
    public string Title { get; } = title;
    public string Hint { get; } = hint;

    public StepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StateText));
            Raise(nameof(IsDone));
            Raise(nameof(IsStale));
            Raise(nameof(IsLocked));
            Raise(nameof(IsRunning));
        }
    }

    public string StateText => _state switch
    {
        StepState.Locked => "未解锁",
        StepState.Ready => "待完成",
        StepState.Running => "进行中",
        StepState.Done => "完成",
        StepState.Stale => "需更新",
        _ => "",
    };

    public bool IsDone => _state == StepState.Done;
    public bool IsStale => _state == StepState.Stale;
    public bool IsLocked => _state == StepState.Locked;
    public bool IsRunning => _state == StepState.Running;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>一张候选图或参考图。</summary>
public sealed class ImageItem(string rel, string path) : Observable
{
    private bool _selected;
    private Bitmap? _thumb;

    public string Rel { get; } = rel;
    public string Path { get; } = path;
    public string Label => System.IO.Path.GetFileNameWithoutExtension(Rel);
    public Bitmap? Thumb => _thumb ??= Thumbs.Load(Path);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>首页上的一个工程。</summary>
public sealed class ProjectCard(StudioProject project, string summary) : Observable
{
    private Bitmap? _thumb;

    public StudioProject Project { get; } = project;
    public string Name => Project.Data.Name;
    public string Summary { get; } = summary;
    public string Updated => Project.Data.Updated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public Bitmap? Thumb => _thumb ??= Thumbs.Load(File.Exists(Project.PathOf(StudioProject.KeyedFigureFile))
        ? Project.PathOf(StudioProject.KeyedFigureFile)
        : Project.Data.Sheet.Selected is { } s ? Project.PathOf(s) : "", 160);

    public bool HasThumb => Thumb is not null;
}

public sealed record Choice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>④ 里的一个动作，直接改 <see cref="PlannedAction"/>（工程定时存盘）。</summary>
public sealed class PlanItem(PlannedAction action) : Observable
{
    public static readonly IReadOnlyList<int> Durations = [4, 5, 6, 8, 10, 12];

    public PlannedAction Action { get; } = action;

    public string Name { get => Action.Name; set { Action.Name = value ?? ""; Raise(nameof(Name)); } }
    public string Id { get => Action.Id; set { Action.Id = (value ?? "").Trim(); Raise(nameof(Id)); } }
    public string Description { get => Action.Description; set => Action.Description = value ?? ""; }

    public string RolesText
    {
        get => string.Join(" ", Action.Roles);
        set => Action.Roles = Split(value, ' ', ',', '，');
    }

    public int Duration
    {
        get => Action.Duration;
        set { Action.Duration = value; Raise(nameof(Duration)); Raise(nameof(DurationValue)); }
    }

    /// <summary>给分段选择用的字符串形式。</summary>
    public string DurationValue
    {
        get => Duration.ToString(CultureInfo.InvariantCulture);
        set { if (int.TryParse(value, CultureInfo.InvariantCulture, out var n) && Durations.Contains(n)) Duration = n; }
    }

    public string Prompt { get => Action.Prompt; set { Action.Prompt = value ?? ""; Raise(nameof(Prompt)); } }

    public string LinesText
    {
        get => string.Join("\n", Action.Lines);
        set => Action.Lines = Split(value, '\n');
    }

    public string TemplateNote =>
        IsCustom ? "自定义动作（提示词按描述生成，可以直接改）" :
        StudioPrompts.Template(Action.Template) is { } t ? $"模板：{t.Name}" + (t.Required ? "（必需）" : "") : "自定义动作";

    public bool IsCustom => Action.Template == StudioPrompts.CustomTemplate;

    /// <summary>按模板（自定义动作按描述）和当前时长重写提示词。</summary>
    public void ResetPrompt()
    {
        if (IsCustom) Prompt = StudioPrompts.Custom(Action.Name, Action.Description, Action.Roles, Action.Duration).Render(Action.Duration);
        else if (StudioPrompts.Template(Action.Template) is { } t) Prompt = t.Render(Action.Duration);
    }

    public bool HasTemplate => IsCustom || StudioPrompts.Template(Action.Template) is not null;
    public string ResetText => IsCustom ? "按描述和时长重写" : "按模板和时长重写";

    internal static List<string> Split(string? text, params char[] separators) =>
        (text ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>⑤⑥ 里一个动作的一个 take。同一段任务一直用同一个实例（<see cref="Update"/> 原地刷新），列表里的预览动画才不会每次轮询都重来。</summary>
public sealed class TakeItem : Observable
{
    private bool _processing;
    private string _progress = "";

    public TakeItem(ActionRow row, TaskRecord record, StudioProject project, bool stale)
    {
        Row = row;
        Update(record, project, stale);
    }

    /// <summary>任务记录、文件或采用状态变了：重新读一遍，所有绑定一起刷新。</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Record))]
    public void Update(TaskRecord record, StudioProject project, bool stale)
    {
        Record = record;
        IsStale = stale;
        var clip = project.ClipFile(record.Action, record.Take);
        HasClip = File.Exists(clip);
        ClipPath = HasClip ? clip : null;
        HasVideo = record.HasVideo(project);
        IsAdopted = Row.Action.Adopted == record.Take && HasClip;
        Thumb = HasClip ? Thumbs.Load(Infrastructure.Studio.StudioEngine.ThumbFile(project, record.Action, record.Take)) : null;
        FirstFrame = Thumbs.Load(project.PathOf(StudioProject.FirstFrameFile), 640);
        Warnings = "";
        QaText = "";
        if (Infrastructure.Studio.StudioEngine.LoadReport(project.QaFile(record.Action, record.Take)) is { } qa)
        {
            var warnings = qa["warnings"] is System.Text.Json.Nodes.JsonArray w ? w.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() : [];
            Warnings = string.Join("\n", warnings);
            QaText = string.Create(CultureInfo.InvariantCulture,
                $"{(int?)qa["frames"] ?? 0} 帧 · 首尾相似 {(double?)qa["loopIou"] ?? 0:0.00} · 漂移 {(double?)qa["driftX"] ?? 0:0.0}px");
        }
        Raise("");   // 空名字 = 全部属性都变了
    }

    public ActionRow Row { get; }
    public TaskRecord Record { get; private set; }
    public int Take => Record.Take;
    public string Title => $"第 {Record.Take} 次";
    public bool IsStale { get; private set; }
    public bool HasClip { get; private set; }
    public bool HasVideo { get; private set; }
    public bool IsAdopted { get; private set; }
    public Bitmap? Thumb { get; private set; }
    public bool HasThumb => Thumb is not null;
    /// <summary>处理好的片段（webm），给悬停预览和大图预览播。</summary>
    public string? ClipPath { get; private set; }
    public string QaText { get; private set; } = "";
    public string Warnings { get; private set; } = "";
    public bool HasWarnings => Warnings.Length > 0;

    // ───── ⑤ 的预览卡 ─────
    // 视频的第一帧就是首尾帧，所以拿它当底图：生成中从模糊慢慢变清楚，做完是清楚的首帧，处理好换成抠完的缩略图。

    /// <summary>当前的首尾帧（工程里的 first_frame）。</summary>
    public Bitmap? FirstFrame { get; private set; }

    public TKCode.Arc.ImageRevealStatus RevealStatus => Record.State switch
    {
        VideoTaskState.Queued => TKCode.Arc.ImageRevealStatus.Queued,
        VideoTaskState.Running => TKCode.Arc.ImageRevealStatus.Generating,
        VideoTaskState.Succeeded => TKCode.Arc.ImageRevealStatus.Done,
        _ => TKCode.Arc.ImageRevealStatus.Error,
    };

    /// <summary>估算的进度：视频接口不报进度，按提交后过了多久渐近（2.5 分钟约 60%，5 分钟约 82%），没做完不会走满。</summary>
    public double RevealProgress
    {
        get
        {
            if (Record.State != VideoTaskState.Running) return Record.State == VideoTaskState.Succeeded ? 1 : 0;
            var seconds = Math.Max(0, (DateTime.Now - Record.SubmittedAt).TotalSeconds);
            return 0.95 * (1 - Math.Exp(-seconds / 180));
        }
    }

    public string RevealError => Record.State switch
    {
        VideoTaskState.Cancelled => "已取消",
        VideoTaskState.Expired => "已过期",
        _ => "生成失败",
    };

    /// <summary>每秒一次，让生成中的进度往前走。</summary>
    public void Tick()
    {
        if (Record.State == VideoTaskState.Running) Raise(nameof(RevealProgress));
    }

    private double _percent;

    public bool IsProcessing { get => _processing; set { if (Set(ref _processing, value)) { Raise(nameof(CanProcess)); Raise(nameof(StatusText)); } } }
    public string Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(StatusText)); } }
    /// <summary>处理进度 0～100（给进度条用；排队时是 0）。</summary>
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    public bool CanProcess => HasVideo && !_processing;
    public bool CanAdopt => HasClip && !IsAdopted;

    public string StatusText
    {
        get
        {
            if (_processing) return "处理中 " + _progress;
            var r = Record;
            var text = r.State switch
            {
                VideoTaskState.Queued => "排队中",
                VideoTaskState.Running => "生成中",
                VideoTaskState.Succeeded when HasClip => "已处理",
                VideoTaskState.Succeeded when HasVideo => "已下载，待处理",
                VideoTaskState.Succeeded when r.DownloadError is not null => "下载失败：" + r.DownloadError,
                VideoTaskState.Succeeded => "生成完成，下载中",
                VideoTaskState.Failed => "失败：" + (r.Error ?? "未知原因"),
                VideoTaskState.Cancelled => "已取消",
                VideoTaskState.Expired => "已过期",
                _ => "",
            };
            if (r.Error is { } e && r.State is not VideoTaskState.Failed) text += $"（{e}）";
            if (IsStale) text += " · 提示词或首尾帧改过";
            return text;
        }
    }

    public string Meta => $"{Record.Model} · {Record.Resolution} · {Record.Duration} 秒 · {Record.SubmittedAt:MM-dd HH:mm}";
}

/// <summary>⑤⑥⑦ 里的一个动作。</summary>
public sealed class ActionRow(PlannedAction action) : Observable
{
    private int _waiting;
    private string _note = "";

    public PlannedAction Action { get; } = action;
    public string Name => Action.Name;
    public string Id => Action.Id;
    public string RolesText => string.Join(" ", Action.Roles);
    public bool IsMove => Action.Roles.Contains(Roles.Move);

    /// <summary>这一行的 take（新的在前）。原地增删，已有的实例不换，预览卡的动画才能接着走。</summary>
    public System.Collections.ObjectModel.ObservableCollection<TakeItem> Takes { get; } = [];

    public void SetTakes(IReadOnlyList<TakeItem> takes)
    {
        for (var i = Takes.Count - 1; i >= 0; i--)
            if (!takes.Contains(Takes[i])) Takes.RemoveAt(i);
        for (var i = 0; i < takes.Count; i++)
        {
            if (i < Takes.Count && ReferenceEquals(Takes[i], takes[i])) continue;
            var at = Takes.IndexOf(takes[i]);
            if (at >= 0) Takes.Move(at, i);
            else Takes.Insert(i, takes[i]);
        }
        Raise(nameof(Summary));
        Raise(nameof(Busy));
        Raise(nameof(CanGenerateAgain));
        Raise(nameof(HasTakes));
        Raise(nameof(AdoptedThumb));
        Raise(nameof(HasAdopted));
    }

    public bool HasTakes => Takes.Count > 0;
    public bool HasWaiting => _waiting > 0;
    public int Waiting
    {
        get => _waiting;
        set { if (Set(ref _waiting, value)) { Raise(nameof(Summary)); Raise(nameof(WaitingSlots)); Raise(nameof(Busy)); Raise(nameof(CanGenerateAgain)); Raise(nameof(HasWaiting)); } }
    }

    /// <summary>这一行有段在排队或生成：再点「再来一段」会重复提交、多花钱，先等它结束。</summary>
    public bool Busy => _waiting > 0 || Takes.Any(t => !t.Record.IsFinished);

    public bool CanGenerateAgain => !Busy;

    /// <summary>还在本地排队、没提交的段数，⑤ 里合成一条「等待提交」的提示。</summary>
    public IReadOnlyList<int> WaitingSlots => Enumerable.Range(1, _waiting).ToList();

    /// <summary>这一行的一句话说明（提交失败之类）。</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;

    public TakeItem? Adopted => Takes.FirstOrDefault(t => t.IsAdopted);
    public bool HasAdopted => Adopted is not null;
    public Avalonia.Media.Imaging.Bitmap? AdoptedThumb => Adopted?.Thumb;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (_waiting > 0) parts.Add($"{_waiting} 段等待提交");
            var active = Takes.Count(t => !t.Record.IsFinished);
            if (active > 0) parts.Add($"{active} 段生成中");
            var videos = Takes.Count(t => t.HasVideo);
            parts.Add(videos == 0 ? "还没有视频" : $"{videos} 段视频");
            if (Adopted is { } a) parts.Add($"采用第 {a.Take} 次");
            return string.Join(" · ", parts);
        }
    }

    // ⑦ 的参数：直接改动作，工程定时存盘。
    public bool Mirror { get => Action.Mirror; set { Action.Mirror = value; Raise(nameof(Mirror)); } }

    /// <summary>步幅（640×360 画面里每走一圈前进的像素），给数字框用。</summary>
    public double Stride
    {
        get => Action.Stride ?? Infrastructure.Studio.StudioEngine.DefaultStride;
        set
        {
            Action.Stride = Math.Clamp(value, 20, 640);
            Raise(nameof(Stride));
            Raise(nameof(StrideText));
        }
    }

    public string StrideText
    {
        get => (Action.Stride ?? Infrastructure.Studio.StudioEngine.DefaultStride).ToString("0", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 20 and <= 640)
                Action.Stride = v;
            Raise(nameof(StrideText));
            Raise(nameof(Stride));
        }
    }

    public string LinesText
    {
        get => string.Join("\n", Action.Lines);
        set => Action.Lines = PlanItem.Split(value, '\n');
    }
}


/// <summary>生成中的占位卡：一张要生成的图一张卡，图回来就撤掉换成真正的候选。</summary>
public sealed class PendingImage(int seed, double aspectRatio, double width) : Observable
{
    private double _progress;

    public int Seed { get; } = seed;
    public double AspectRatio { get; } = aspectRatio;
    public double Width { get; } = width;
    public double Height => Width / AspectRatio;

    /// <summary>估算的进度（0~0.95）：接口不报进度，按已等时间渐近，图没回来前不会走满。</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }
}

/// <summary>占位卡（ImageReveal）上的中文字。进度是估的，所以药丸里只显示百分比，不装作知道模型走到第几步。</summary>
public static class RevealLabels
{
    public static TKCode.Arc.ImageRevealLabels Chinese { get; } = new()
    {
        Queued = "排队中",
        Generating = "正在画",
        Done = "画好了",
        Failed = "生成失败",
        Retry = "重试",
        GeneratedImage = "生成的图",
        GeneratingImage = "正在生成图片",
        ImageReady = "图片生成好了",
        NoImage = "还没有图",
        Simulated = "",
        Count = (step, total) => $"{step * 100 / Math.Max(1, total)}%",
        StepOf = (step, total) => $"大约 {step * 100 / Math.Max(1, total)}%",
    };
}
