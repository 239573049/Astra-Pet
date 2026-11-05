using System.Runtime.InteropServices;
using AstraPet.Core;
using AstraPet.Infrastructure.Studio;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AstraPet.ControlPanel;

/// <summary>抠像色下拉框的一项；Key 为 null 表示自动判断。</summary>
public sealed record KeyOption(KeyColor? Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 「动作」页的「导入视频」（01 §4.3 的前两种途径）：选一个纯色背景或带透明通道的视频 → 本机处理成 640×360 带 alpha 的 webm
/// （design/pet-studio/06）→ 看预览和质检 → 加到覆盖层的 added 里，片段放在 <c>&lt;覆盖层目录&gt;/clips/&lt;id&gt;/a.webm</c>。
/// </summary>
public sealed class ActionImportViewModel(ActionsViewModel owner, string ffmpeg) : Observable
{
    public enum Stage { None, Ready, Processing, Done }

    public static IReadOnlyList<KeyOption> KeyOptions { get; } =
    [
        new(null, "自动判断"), new(KeyColor.Green, "绿幕"), new(KeyColor.Blue, "蓝幕"), new(KeyColor.Magenta, "品红"),
    ];

    private readonly ClipProcessor _processor = new(ffmpeg);
    private Stage _stage;
    private string? _source;
    private SourceInfo? _info;
    private string _name = "", _rolesText = Roles.Happy, _description = "", _status = "", _error = "";
    private KeyOption _keyOption = KeyOptions[0];
    private double _progress;
    private ClipReport? _report;
    private string? _clipDir, _id;
    private CancellationTokenSource? _cts;

    public Stage Current
    {
        get => _stage;
        private set
        {
            if (!Set(ref _stage, value)) return;
            foreach (var n in new[] { nameof(IsOpen), nameof(IsReady), nameof(IsProcessing), nameof(IsDone), nameof(CanEdit) }) Raise(n);
        }
    }

    public bool IsOpen => _stage != Stage.None;
    public bool IsReady => _stage == Stage.Ready;
    public bool IsProcessing => _stage == Stage.Processing;
    public bool IsDone => _stage == Stage.Done;
    public bool CanEdit => _stage is Stage.Ready or Stage.Done;

    public string SourceText => _info is { } i
        ? $"{Path.GetFileName(_source)} · {i.Width}×{i.Height} · {i.Fps:0.##} fps · {i.Seconds:0.0} 秒 · " + (i.HasAlpha ? "自带透明通道，不用抠像" : "需要抠像")
        : "";

    public bool NeedsKey => _info is { HasAlpha: false };
    public KeyOption KeyOption { get => _keyOption; set => Set(ref _keyOption, value); }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string RolesText { get => _rolesText; set => Set(ref _rolesText, value); }
    public string Description { get => _description; set => Set(ref _description, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }
    public bool HasError => _error.Length > 0;

    /// <summary>处理结果的中间一帧，画在棋盘格上看透明边缘。</summary>
    public Bitmap? Preview { get; private set; }
    public string ReportText => _report is { } r
        ? $"{r.Frames} 帧 · 首尾重合度 {r.LoopIou:0.00} · 横向漂移 {r.DriftX:0} px · 边缘残留 {r.EdgeLeak:0.0%} · 背景雾 {r.Haze:0.0%}"
          + (r.Key is { } k ? $" · 抠像色 {KeyOptions.First(o => o.Key == k).Label}" : "")
        : "";
    public IReadOnlyList<string> Warnings => _report?.Warnings ?? [];
    public bool HasWarnings => Warnings.Count > 0;
    public bool AllGood => _report is not null && Warnings.Count == 0;

    private static WriteableBitmap ToBitmap(byte[] rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(PetClip.Width, PetClip.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bitmap.Lock();
        for (var y = 0; y < PetClip.Height; y++)
            Marshal.Copy(rgba, y * PetClip.Width * 4, fb.Address + y * fb.RowBytes, PetClip.Width * 4);
        return bitmap;
    }

    /// <summary>选好了文件：探测一下，填默认名字。</summary>
    public async Task OpenAsync(string path)
    {
        Discard();
        Error = "";
        try
        {
            _info = await _processor.ProbeAsync(path);
        }
        catch (ClipProcessingException e)
        {
            _info = null;
            Current = Stage.None;
            Error = e.Message;
            return;
        }
        _source = path;
        Name = Path.GetFileNameWithoutExtension(path);
        Description = "";
        KeyOption = KeyOptions[0];
        Raise(nameof(SourceText));
        Raise(nameof(NeedsKey));
        Status = "";
        Current = Stage.Ready;
    }

    private List<string>? ParseRoles()
    {
        var roles = RolesText.Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.ToLowerInvariant()).Distinct().ToList();
        if (roles.Count == 0) { Error = "至少填一个角色，比如 happy、celebrate、think"; return null; }
        if (roles.FirstOrDefault(r => !ActionDef.IsValidId(r)) is { } bad) { Error = $"角色「{bad}」不合法：只能用小写字母、数字和连字符"; return null; }
        if (roles.Contains(Roles.Move)) { Error = "移动（move）动作需要步态数据，导入暂不支持；换个角色吧"; return null; }
        return roles;
    }

    /// <summary>开始处理。处理在线程池上跑，宠物照常动。</summary>
    public async Task StartAsync()
    {
        if (_source is null || _info is null || owner.OverlayDirectory is not { } overlay) return;
        Error = "";
        if (ParseRoles() is not { } roles) return;

        DeleteClip();
        _id = owner.NewActionId();
        _clipDir = Path.Combine(overlay, "clips", _id);
        _report = null;
        RaiseReport();
        Progress = 0;
        Status = "正在处理…";
        Current = Stage.Processing;
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p => { Progress = p * 100; Status = $"正在处理… {p:0%}"; });
        try
        {
            var options = new ClipOptions { Key = _keyOption.Key, IsDrag = roles.Contains(Roles.Drag) };
            var source = _source;
            var destination = Path.Combine(_clipDir, "a.webm");
            _report = await Task.Run(() => _processor.ProcessAsync(source, destination, options, progress, _cts.Token));
            Status = "处理好了，看看效果再决定要不要";
            Current = Stage.Done;
        }
        catch (Exception e) when (e is ClipProcessingException or OperationCanceledException or IOException)
        {
            DeleteClip();
            Error = e is OperationCanceledException ? "" : e.Message;
            Status = e is OperationCanceledException ? "已取消" : "";
            Current = Stage.Ready;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
        RaiseReport();
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>加到宠物上：写进覆盖层，马上播一次。</summary>
    public void Add()
    {
        if (_report is null || _id is null || ParseRoles() is not { } roles) return;
        var added = new AddedAction
        {
            Id = _id,
            Name = Name.Trim() is { Length: > 0 } n ? n : _id,
            Description = Description.Trim(),
            Roles = roles,
            Clips = [$"clips/{_id}/a.webm"],
        };
        _clipDir = null;   // 已经归动作所有，不再当临时文件删
        _report = null;
        Current = Stage.None;
        RaiseReport();
        owner.AddImported(added);
    }

    /// <summary>放弃：删掉处理出来的片段。</summary>
    public void Discard()
    {
        Cancel();
        DeleteClip();
        _report = null;
        _source = null;
        _info = null;
        Current = Stage.None;
        Error = "";
        RaiseReport();
    }

    private void DeleteClip()
    {
        if (_clipDir is null) return;
        try { if (Directory.Exists(_clipDir)) Directory.Delete(_clipDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PetLog.Write($"删除导入的临时片段失败: {e.Message}"); }
        _clipDir = null;
    }

    private void RaiseReport()
    {
        Preview = _report?.Preview is { } rgba ? ToBitmap(rgba) : null;
        foreach (var n in new[] { nameof(Preview), nameof(ReportText), nameof(Warnings), nameof(HasWarnings), nameof(AllGood) }) Raise(n);
    }
}
