using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using AstraPet.Core;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using AstraPet.Platform;
using Avalonia.Threading;

namespace AstraPet.Studio;

/// <summary>
/// 工作台窗口（04）：首页是工程列表，打开一个工程后是 ⓪～⑧ 九步（<see cref="ProjectViewModel"/>）。
/// 这里还管两件和工程无关的事：生成接口的配置（存在设置里，所有工程共用）和费用确认框（<see cref="ICostGate"/>）。
/// </summary>
public sealed class StudioViewModel : Observable, ICostGate, IDisposable
{
    private readonly PetSettings _settings;
    private ProjectViewModel? _current;
    private string _newName = "";
    private string _homeMessage = "";

    public StudioViewModel(PetSettings settings, StudioEngine engine, string root, IClipDecoder decoder, string cacheRoot,
        IShell shell, IStudioHost host)
    {
        _settings = settings;
        Engine = engine;
        Root = root;
        Decoder = decoder;
        ClipPreview.Decoder = decoder;
        CacheRoot = cacheRoot;
        Shell = shell;
        Host = host;
        EnsureFixedProviders();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PetSettings.ActiveImageProvider) or nameof(PetSettings.ActiveVideoProvider)
                or nameof(PetSettings.ActiveImageProviderId) or nameof(PetSettings.ActiveVideoProviderId))
                RaiseProviders();
        };
        RefreshProjects();
    }

    public StudioEngine Engine { get; }
    public string Root { get; }
    public IClipDecoder Decoder { get; }
    public string CacheRoot { get; }
    public IShell Shell { get; }
    public IStudioHost Host { get; }

    // ───── 首页 ─────

    public ObservableCollection<ProjectCard> Projects { get; } = [];
    public bool HasProjects => Projects.Count > 0;

    public string NewName { get => _newName; set => Set(ref _newName, value ?? ""); }

    public string HomeMessage { get => _homeMessage; set { if (Set(ref _homeMessage, value)) Raise(nameof(HasHomeMessage)); } }
    public bool HasHomeMessage => _homeMessage.Length > 0;

    public ProjectViewModel? Current
    {
        get => _current;
        private set
        {
            var old = _current;
            if (!Set(ref _current, value)) return;
            old?.Dispose();
            Raise(nameof(IsHome));
            Raise(nameof(IsProject));
        }
    }

    public bool IsHome => _current is null;
    public bool IsProject => _current is not null;

    public void RefreshProjects()
    {
        Projects.Clear();
        foreach (var p in StudioProject.List(Root).OrderByDescending(p => p.Data.Updated))
        {
            var done = p.Data.Actions.Count(a => p.AdoptedTake(a) is not null);
            var summary = p.Data.Actions.Count == 0 ? "刚开始" : $"{done}/{p.Data.Actions.Count} 个动作做好了";
            if (p.Data.Export.LastPath.Length > 0) summary += " · 导出过";
            Projects.Add(new ProjectCard(p, summary));
        }
        Raise(nameof(HasProjects));
    }

    public void CreateProject()
    {
        var name = NewName.Trim() is { Length: > 0 } n ? n : "新角色";
        try
        {
            var project = StudioProject.Create(Root, name);
            project.Data.Resolution = DefaultResolution;
            project.Save();
            NewName = "";
            HomeMessage = "";
            Open(project);
        }
        catch (Exception e)
        {
            HomeMessage = "建工程失败：" + e.Message;
        }
    }

    public void Open(StudioProject project)
    {
        // 已经开着同一个工程就不重建：重建会丢掉正在处理的片段和界面状态。
        if (_current?.Project.Id == project.Id) return;
        Current = new ProjectViewModel(this, project);
        Current.FitResolution();
        PetLog.Write($"工作台：打开工程 {project.Id}「{project.Data.Name}」");
    }

    public void BackHome()
    {
        _current?.SaveNow();
        Current = null;
        RefreshProjects();
    }

    public void OpenRootFolder() => Shell.OpenFolder(Root);

    // ───── ⓪ 生成接口 ─────
    // 暂时写死：图片和视频都走 NextCoWork（图片是 OpenAI Images 兼容接口，视频是方舟任务协议），用户只填一个 Key。
    // 设置里仍按 MediaProvider 存两条（引擎和任务恢复都按它找），地址、协议、模型每次打开都按预设改回去。

    public const string KeyUrl = "https://nextco.work/dashboard/api-keys";

    public MediaProvider? ImageProvider => _settings.ActiveImageProvider;
    public MediaProvider? VideoProvider => _settings.ActiveVideoProvider;
    public bool ImageReady => ImageProvider is { IsConfigured: true };
    public bool VideoReady => VideoProvider is { IsConfigured: true };

    /// <summary>NextCoWork 的 Key，同时用于图片和视频。只存在本机设置里。</summary>
    public string ApiKey
    {
        get => ImageProvider?.ApiKey ?? "";
        set
        {
            var key = (value ?? "").Trim();
            if (key == ApiKey) return;
            foreach (var p in FixedProviders()) p.ApiKey = key;
            TestText = "";
            Raise(nameof(ApiKey));
            RaiseProviders();
        }
    }

    public bool HasKey => ApiKey.Length > 0;

    /// <summary>对话模型里已经有 NextCoWork 的 Key（而且和这里的不一样）时，提供一键沿用。</summary>
    public bool CanUseChatKey => ChatKey() is { } k && k != ApiKey;

    public string ModelsText =>
        $"图片：{ImageProvider?.Model}（设定图、站姿）　视频：{VideoProvider?.Model}（动作，方舟接口格式）";

    public string PricesText
    {
        get
        {
            static string Join(MediaProvider? p) => p is null ? "" : string.Join("，", p.Prices.Select(kv => $"{(kv.Key == "*" ? "默认" : kv.Key)} ¥{kv.Value:0.##}"));
            return $"图片每张约：{Join(ImageProvider)}；视频每秒约：{Join(VideoProvider)}。{MediaPresets.Find(MediaPresets.Defaults[0])?.PriceNote}";
        }
    }

    private string _testText = "";
    private bool _testOk;
    private bool _testing;
    public string TestText { get => _testText; private set { if (Set(ref _testText, value)) Raise(nameof(HasTestText)); } }
    public bool HasTestText => _testText.Length > 0;
    public bool TestOk { get => _testOk; private set { if (Set(ref _testOk, value)) Raise(nameof(TestTone)); } }
    public TKCode.Arc.AlertTone TestTone => _testOk ? TKCode.Arc.AlertTone.Success : TKCode.Arc.AlertTone.Danger;
    public bool Testing { get => _testing; private set => Set(ref _testing, value); }

    private IEnumerable<MediaProvider> FixedProviders() =>
        MediaPresets.Defaults.Select(id => _settings.MediaProviders.FirstOrDefault(p => p.PresetId == id)).OfType<MediaProvider>();

    private string? ChatKey() =>
        _settings.Providers.FirstOrDefault(p => MediaPresets.HostOf(p.BaseUrl) == MediaPresets.NextCoWorkHost && p.ApiKey.Trim().Length > 0)
            ?.ApiKey.Trim();

    /// <summary>保证设置里有两条 NextCoWork 的生成接口并设为当前；没填 Key 时沿用对话里的 NextCoWork Key。</summary>
    private void EnsureFixedProviders()
    {
        var key = FixedProviders().Select(p => p.ApiKey.Trim()).FirstOrDefault(k => k.Length > 0) ?? ChatKey() ?? "";
        foreach (var id in MediaPresets.Defaults)
        {
            var preset = MediaPresets.Find(id)!;
            var provider = _settings.MediaProviders.FirstOrDefault(p => p.PresetId == id);
            if (provider is null)
            {
                provider = preset.Create();
                _settings.MediaProviders.Add(provider);
            }
            var fresh = preset.Create();
            provider.Name = fresh.Name;
            provider.Kind = fresh.Kind;
            provider.Protocol = fresh.Protocol;
            provider.BaseUrl = fresh.BaseUrl;
            // 视频模型可以在四个 Seedance 里选（选过就保留），单价跟着模型走。
            var model = provider.Kind == MediaKind.Video ? MediaPresets.VideoModelOf(provider.Model) : null;
            provider.Model = model?.Id ?? fresh.Model;
            if (model is not null) provider.Name = "NextCoWork " + model.Name;
            provider.Prices = model is null ? fresh.Prices : new Dictionary<string, double>(model.Prices);
            provider.ApiKey = key;
            if (provider.Kind == MediaKind.Image) _settings.ActiveImageProviderId = provider.Id;
            else _settings.ActiveVideoProviderId = provider.Id;
        }
    }

    public IReadOnlyList<VideoModel> VideoModels => MediaPresets.NextCoWorkVideoModels;

    /// <summary>当前的视频模型。换了以后单价跟着换，开着的工程分辨率换成这个模型能出的最高一档。</summary>
    public string VideoModelId
    {
        get => VideoProvider?.Model ?? "";
        set
        {
            if (VideoProvider is not { } p || MediaPresets.VideoModelOf(value) is not { } m || p.Model == m.Id) return;
            p.Model = m.Id;
            p.Name = "NextCoWork " + m.Name;
            p.Prices = new Dictionary<string, double>(m.Prices);
            Raise(nameof(VideoModelId));
            Raise(nameof(VideoModelNote));
            RaiseProviders();
            _current?.UseModelResolution();
        }
    }

    public string VideoModelNote => MediaPresets.VideoModelOf(VideoModelId) is { } m
        ? $"{m.Id} · {m.Note} · 每秒约 " + string.Join("，", m.Resolutions.Select(r => $"{r} ¥{m.Prices[r]:0.00}"))
        : "";

    /// <summary>新工程默认用当前视频模型能出的最高分辨率。</summary>
    private string DefaultResolution => MediaPresets.VideoModelOf(VideoModelId)?.Best ?? "720p";

    public void UseChatKey() { if (ChatKey() is { } k) ApiKey = k; }

    public void OpenKeyPage() => Shell.OpenUrl(KeyUrl);

    /// <summary>测试连接：图片和视频各发一个不计费的请求（见 05 §4），不读聊天记录和设置里的其他东西。</summary>
    public async Task TestAsync()
    {
        if (!HasKey) { TestOk = false; TestText = "先填 Key"; return; }
        Testing = true;
        TestText = "";
        var lines = new List<string>();
        var ok = true;
        foreach (var (label, provider) in new[] { ("图片", ImageProvider), ("视频", VideoProvider) })
        {
            if (provider is null) continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                lines.Add($"{label}：{await MediaClients.TestAsync(provider, cts.Token)}");
            }
            catch (Exception e) when (e is GenerationException or OperationCanceledException or InvalidOperationException)
            {
                ok = false;
                lines.Add($"{label}：{(e is OperationCanceledException ? "连接超时" : e.Message)}");
            }
        }
        TestOk = ok;
        TestText = string.Join("\n", lines);
        Testing = false;
    }

    /// <summary>Key 变了也要刷新各步的解锁状态。</summary>
    internal void RaiseProviders()
    {
        Raise(nameof(ImageProvider));
        Raise(nameof(VideoProvider));
        Raise(nameof(ImageReady));
        Raise(nameof(VideoReady));
        Raise(nameof(HasKey));
        Raise(nameof(CanUseChatKey));
        Raise(nameof(ModelsText));
        Raise(nameof(PricesText));
        _current?.RefreshStates();
    }

    // ───── 费用确认（ICostGate） ─────

    private TaskCompletionSource<bool>? _costAnswer;
    private CostEstimate? _cost;

    public bool IsCostOpen
    {
        get => _cost is not null;
        set
        {
            if (value || _cost is null) return;
            AnswerCost(false);   // Esc、点右上角都算取消
        }
    }

    public string CostTitle => _cost?.Title ?? "";
    public string CostProvider => _cost is null ? "" : $"{_cost.Provider} · {_cost.Model}";
    public IReadOnlyList<string> CostLines => _cost?.Items.Select(i => $"{i.Label}　{Money(i.Amount)}").ToList() ?? [];
    public string CostTotal => _cost is null ? "" : "预估合计：" + _cost.TotalText;
    public string CostNote => _cost is null ? "" :
        (_cost.Note is { Length: > 0 } n && !n.Contains("账单") ? n + "。" : "") +
        "金额按你在 ⓪ 填的单价估算，以服务商账单为准。确认后立即提交；已提交的不会自动重试，失败了需要你再点一次。" +
        (_current is { } p ? $"\n这个工程到目前约花了 {Money(p.Project.LedgerTotal())}。" : "");

    public static string Money(double? amount) =>
        amount is { } v ? "¥" + v.ToString("0.00", CultureInfo.InvariantCulture) : "价格未知";

    public async Task<bool> ConfirmAsync(CostEstimate estimate, CancellationToken ct)
    {
        // 生成调用可能从后台线程来，确认框必须在界面线程上。
        if (Dispatcher.UIThread.CheckAccess()) return await OpenCost(estimate, ct);
        var opened = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => opened.SetResult(OpenCost(estimate, ct)));
        return await await opened.Task;
    }

    private Task<bool> OpenCost(CostEstimate estimate, CancellationToken ct)
    {
        _costAnswer?.TrySetResult(false);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _costAnswer = answer;
        _cost = estimate;
        RaiseCost();
        ct.Register(() => Dispatcher.UIThread.Post(() => { if (_costAnswer == answer) AnswerCost(false); }));
        return answer.Task;
    }

    public void AnswerCost(bool ok)
    {
        if (_costAnswer is not { } answer) return;
        _costAnswer = null;
        _cost = null;
        RaiseCost();
        answer.TrySetResult(ok);
    }

    private void RaiseCost()
    {
        Raise(nameof(IsCostOpen));
        Raise(nameof(CostTitle));
        Raise(nameof(CostProvider));
        Raise(nameof(CostLines));
        Raise(nameof(CostTotal));
        Raise(nameof(CostNote));
    }

    public void Dispose()
    {
        _current?.SaveNow();
        Current = null;
        AnswerCost(false);
    }
}
