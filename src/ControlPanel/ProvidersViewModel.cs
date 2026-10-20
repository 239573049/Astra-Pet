using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Import;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AstraPet.ControlPanel;

public sealed record ProtocolOption(LlmProtocol Protocol, string Label, string Hint)
{
    public static readonly ProtocolOption[] All =
    [
        new(LlmProtocol.ChatCompletions, "OpenAI Chat Completions", "POST {地址}/chat/completions · 大多数兼容接口（DeepSeek、通义、Kimi、Ollama…）用这个"),
        new(LlmProtocol.Responses, "OpenAI Responses", "POST {地址}/responses · OpenAI 新版接口"),
        new(LlmProtocol.Anthropic, "Anthropic Messages", "POST {地址}/v1/messages · Claude 官方接口"),
    ];

    public override string ToString() => Label;
}

/// <summary>添加提供商时选择列表里的一块。</summary>
public sealed class PresetItem(ProviderPreset preset)
{
    public ProviderPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    /// <summary>点进去后实际会预填的地址（以模型库为准），所以这里也按模型库算。</summary>
    public string Host => Preset.BaseUrl.Length == 0 ? "填写自己的接口地址" : ProviderText.Host(ModelCatalog.Current.Apply(Preset).BaseUrl);
    public Bitmap? Icon { get; } = ProviderIcons.Get(preset.Icon);
    public bool HasIcon => Icon is not null;
    public string Glyph => Preset.Glyph is { Length: > 0 } g ? g : ProviderIcons.FirstGlyph(Preset.Name);
}

public sealed record PresetGroup(string Title, IReadOnlyList<PresetItem> Items);

/// <summary>导入对话框里的一个来源：名字、位置、检测结果，点进去看里面的提供商。</summary>
public sealed class ImportSourceItem : Observable
{
    private string _status = "正在检测…";
    private IReadOnlyList<ImportCandidate> _candidates = [];
    private bool _scanned;
    private bool _found;

    public ImportSourceItem(string name, string location, string icon, string glyph)
    {
        Name = name;
        Where = location;
        Icon = ProviderIcons.Get(icon);
        Glyph = glyph;
    }

    public string Name { get; }
    public string Where { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Glyph { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public IReadOnlyList<ImportCandidate> Candidates => _candidates;
    public bool Found => _found;
    public bool Scanned => _scanned;
    /// <summary>有东西可以导入，才能点进去。</summary>
    public bool CanOpen => _candidates.Count > 0;

    public void SetResult(ImportScan scan)
    {
        _candidates = scan.Candidates;
        _found = scan.Found;
        _scanned = true;
        Status = scan.Status;
        Raise(nameof(Candidates));
        Raise(nameof(Found));
        Raise(nameof(Scanned));
        Raise(nameof(CanOpen));
    }
}

public enum ProviderDialog { None, Picker, Form, Import }

/// <summary>
/// 「智能」页上提供商这一块的视图模型：卡片列表、「添加提供商」选择、新增 / 编辑表单、「从其他应用导入」。
/// 对话框是窗口里叠的一层，不是独立窗口，所以这里用一个 <see cref="Dialog"/> 状态管着显示哪一屏。
/// 编辑用草稿：点保存才写回设置，取消不留痕迹。
/// </summary>
public sealed class ProvidersViewModel : Observable
{
    private readonly PetSettings _settings;
    private readonly Dictionary<string, ProviderCard> _cardById = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private ProviderDialog _dialog;
    private string _notice = "";
    private string _search = "";
    private IReadOnlyList<PresetGroup> _groups = [];

    // 表单
    private LlmProvider? _draft;
    private LlmProvider? _editing;
    private ProviderPreset? _draftPreset;
    private string _formTestText = "";
    private bool _formTestOk;
    private bool _formTesting;
    private string _fetchText = "";
    private bool _fetchOk;
    private bool _fetching;
    private bool _confirmingDelete;

    // 导入
    private IReadOnlyList<ImportSourceItem> _sources = [];
    private ImportSourceItem? _currentSource;
    private string _importError = "";

    public ProvidersViewModel(PetSettings settings)
    {
        _settings = settings;
        Rebuild();
        settings.Providers.CollectionChanged += OnProvidersChanged;
        settings.PropertyChanged += OnSettingChanged;
        _noticeTimer.Tick += (_, _) => Notice = "";
        ModelCatalog.Updated += OnCatalogUpdated;
        RebuildGroups();
    }

    // ───── 卡片 ─────

    public ObservableCollection<ProviderCard> Cards { get; } = new();
    public bool HasCards => Cards.Count > 0;
    public bool IsEmpty => Cards.Count == 0;
    public string CountText => Cards.Count == 0 ? "还没有提供商" : $"共 {Cards.Count} 个，对话使用开关打开的那个";

    public string Notice
    {
        get => _notice;
        private set
        {
            if (!Set(ref _notice, value)) return;
            Raise(nameof(HasNotice));
            _noticeTimer.Stop();
            if (value.Length > 0) _noticeTimer.Start();
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public bool IsActive(LlmProvider provider) => _settings.ActiveProvider?.Id == provider.Id;

    public void Activate(LlmProvider provider)
    {
        if (IsActive(provider) && _settings.ActiveProviderId == provider.Id) return;
        _settings.ActiveProviderId = provider.Id;
    }

    private void Rebuild()
    {
        Cards.Clear();
        foreach (var provider in _settings.Providers)
        {
            if (!_cardById.TryGetValue(provider.Id, out var card))
                _cardById[provider.Id] = card = new ProviderCard(provider, this);
            Cards.Add(card);
        }
        foreach (var gone in _cardById.Keys.Where(id => _settings.Providers.All(p => p.Id != id)).ToList())
            _cardById.Remove(gone);
        Raise(nameof(HasCards));
        Raise(nameof(IsEmpty));
        Raise(nameof(CountText));
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
        RefreshActive();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PetSettings.ActiveProviderId) or nameof(PetSettings.ActiveProvider))
            RefreshActive();
    }

    /// <summary>「正在用谁」变了：所有卡片的开关、状态和边框都要跟着变。</summary>
    private void RefreshActive()
    {
        foreach (var card in Cards) card.Refresh();
    }

    /// <summary>卡片上的测试按钮：发一条 “Say 1”，结果写在这张卡上。</summary>
    public async Task TestCardAsync(ProviderCard card)
    {
        if (card.Testing) return;
        var p = card.Provider;
        card.Testing = true;
        card.TestOk = false;
        card.TestText = "正在请求…";
        try
        {
            var (_, elapsed) = await LlmClient.TestAsync(p, Math.Max(64, _settings.MaxTokens),
                TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            card.TestOk = true;
            card.TestText = $"连接成功 · {elapsed.TotalMilliseconds:0} ms";
            PetLog.Write($"模型测试成功 {p.Name}/{p.Model}");
        }
        catch (Exception e)
        {
            card.TestText = e.Message;
            PetLog.Write($"模型测试失败 {p.Name}/{p.Model}: {e.Message}");
        }
        finally
        {
            card.Testing = false;
        }
    }

    // ───── 对话框总控 ─────

    public ProviderDialog Dialog
    {
        get => _dialog;
        private set
        {
            if (!Set(ref _dialog, value)) return;
            Raise(nameof(IsOpen));
            Raise(nameof(IsPicker));
            Raise(nameof(IsForm));
            Raise(nameof(IsImport));
        }
    }

    public bool IsOpen => _dialog != ProviderDialog.None;
    public bool IsPicker => _dialog == ProviderDialog.Picker;
    public bool IsForm => _dialog == ProviderDialog.Form;
    public bool IsImport => _dialog == ProviderDialog.Import;

    public void Close()
    {
        Dialog = ProviderDialog.None;
        SetDraft(null);
        _editing = null;
        ConfirmingDelete = false;
    }

    // ───── 添加提供商：选择 ─────

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) RebuildGroups(); }
    }

    public IReadOnlyList<PresetGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
    public bool NoPresetMatch => _groups.Count == 0;

    private void RebuildGroups()
    {
        var q = _search.Trim();
        var matched = ProviderPresets.All.Where(p => q.Length == 0
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.BaseUrl.Contains(q, StringComparison.OrdinalIgnoreCase));
        Groups = matched
            .GroupBy(p => p.Group)
            .OrderBy(g => g.Key switch
            {
                ProviderPreset.GroupRecommended => 0,
                ProviderPreset.GroupChina => 1,
                ProviderPreset.GroupGlobal => 2,
                _ => 3,
            })
            .Select(g => new PresetGroup(g.Key, g.Select(p => new PresetItem(p)).ToList()))
            .ToList();
        Raise(nameof(NoPresetMatch));
    }

    public void OpenPicker()
    {
        Search = "";
        Dialog = ProviderDialog.Picker;
    }

    // ───── 新增 / 编辑表单 ─────

    public LlmProvider? Draft => _draft;
    public bool IsEditing => _editing is not null;
    public string FormTitle => IsEditing ? "编辑提供商" : "添加提供商";
    public string SaveText => IsEditing ? "保存" : "添加";

    public Bitmap? FormIcon => ProviderIcons.Get(_draftPreset?.Icon);
    public bool FormHasIcon => FormIcon is not null;
    public string FormGlyph => _draftPreset?.Glyph is { Length: > 0 } g ? g : ProviderIcons.FirstGlyph(_draft?.Name ?? "");
    public string FormSubtitle => _draftPreset?.Name ?? "自定义接口";
    public string PresetHint => _draftPreset?.Hint ?? "";
    public bool HasPresetHint => PresetHint.Length > 0;
    public bool HasKeyUrl => !string.IsNullOrEmpty(_draftPreset?.KeyUrl);

    public IReadOnlyList<ProtocolOption> ProtocolList => ProtocolOption.All;

    public ProtocolOption? DraftProtocol
    {
        get => _draft is { } d ? ProtocolOption.All.First(o => o.Protocol == d.Protocol) : null;
        set
        {
            if (value is null || _draft is not { } d || d.Protocol == value.Protocol) return;
            d.Protocol = value.Protocol;
        }
    }

    public string ProtocolHint => DraftProtocol?.Hint ?? "";

    /// <summary>
    /// 模型输入框下面的候选：优先用「获取模型」拿到的列表，没有就用预设建议；
    /// 输入框里有内容时按它筛选，最多 10 个，点一下填进去。
    /// </summary>
    public IReadOnlyList<string> ModelSuggestions
    {
        get
        {
            var pool = PoolModels();
            var q = _draft?.Model.Trim() ?? "";
            var exact = pool.Any(m => m.Equals(q, StringComparison.OrdinalIgnoreCase));
            var list = q.Length == 0 || exact ? pool : pool.Where(m => m.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return list.Take(10).ToList();
        }
    }

    public string ModelSuggestNote
    {
        get
        {
            var pool = PoolModels().Count;
            return pool > 10 ? $"共 {pool} 个可选模型，输入关键字可以筛选" : "";
        }
    }

    public bool HasModelSuggestions => ModelSuggestions.Count > 0;

    /// <summary>候选池：「获取模型」拿到的列表 > 模型库（models.dev）里这家的模型 > 预设里手写的。</summary>
    private List<string> PoolModels()
    {
        if (_draft is null) return [];
        if (_draft.Models.Count > 0) return _draft.Models.Distinct().ToList();
        var fromCatalog = ModelCatalog.Current.SuggestIds(_draft);
        return fromCatalog.Count > 0 ? fromCatalog.ToList() : _draftPreset?.Models.ToList() ?? [];
    }

    /// <summary>当前填的模型在模型库里的参数：上下文、最大输出、能力、价格。库里没有就不显示。</summary>
    public string ModelInfoText
    {
        get
        {
            if (_draft is null || ModelCatalog.Current.Find(_draft) is not { } info) return "";
            return info.Name != info.Id ? $"{info.Name} · {info.Summary}" : info.Summary;
        }
    }

    public bool HasModelInfo => ModelInfoText.Length > 0;

    public bool CanSave => _draft is { } d
                           && !string.IsNullOrWhiteSpace(d.Name)
                           && !string.IsNullOrWhiteSpace(d.BaseUrl)
                           && !string.IsNullOrWhiteSpace(d.Model);

    public string FormTestText { get => _formTestText; private set => Set(ref _formTestText, value); }
    public bool FormTestOk { get => _formTestOk; private set => Set(ref _formTestOk, value); }
    public bool FormTesting { get => _formTesting; private set => Set(ref _formTesting, value); }
    public string FetchText { get => _fetchText; private set { if (Set(ref _fetchText, value)) Raise(nameof(HasFetchText)); } }
    public bool HasFetchText => _fetchText.Length > 0;
    public bool FetchOk { get => _fetchOk; private set => Set(ref _fetchOk, value); }
    public bool Fetching { get => _fetching; private set => Set(ref _fetching, value); }

    public bool ConfirmingDelete
    {
        get => _confirmingDelete;
        private set { if (Set(ref _confirmingDelete, value)) Raise(nameof(DeleteText)); }
    }

    public string DeleteText => ConfirmingDelete ? "再点一次确认删除" : "删除";

    /// <summary>选了预设，进入表单。预设的地址、协议、第一个建议模型预填好，只差 Key。</summary>
    public void BeginAdd(ProviderPreset preset)
    {
        // 地址、协议、首选模型以内置的模型库（models.dev）为准，库里没有的厂商用预设里手写的。
        var applied = ModelCatalog.Current.Apply(preset);
        _editing = null;
        _draftPreset = applied;
        SetDraft(new LlmProvider
        {
            Name = preset.Id.StartsWith("custom", StringComparison.Ordinal) ? "自定义接口" : preset.Name,
            Protocol = applied.Protocol,
            BaseUrl = applied.BaseUrl,
            Model = applied.Models.FirstOrDefault() ?? "",
            PresetId = preset.Id,
        });
        OpenForm();
    }

    public void BeginEdit(ProviderCard card)
    {
        _editing = card.Provider;
        _draftPreset = ProviderPresets.Resolve(card.Provider);
        SetDraft(card.Provider.Clone());
        OpenForm();
    }

    private void OpenForm()
    {
        FormTestText = "";
        FetchText = "";
        ConfirmingDelete = false;
        Dialog = ProviderDialog.Form;
        foreach (var name in new[] { nameof(IsEditing), nameof(FormTitle), nameof(SaveText), nameof(FormIcon), nameof(FormHasIcon),
                     nameof(FormGlyph), nameof(FormSubtitle), nameof(PresetHint), nameof(HasPresetHint), nameof(HasKeyUrl) })
            Raise(name);
    }

    /// <summary>表单里「返回」：新增时回到选择列表。</summary>
    public void BackToPicker()
    {
        SetDraft(null);
        Dialog = ProviderDialog.Picker;
    }

    private void SetDraft(LlmProvider? draft)
    {
        if (_draft is not null) _draft.PropertyChanged -= OnDraftChanged;
        _draft = draft;
        if (_draft is not null) _draft.PropertyChanged += OnDraftChanged;
        foreach (var name in new[] { nameof(Draft), nameof(DraftProtocol), nameof(ProtocolHint), nameof(CanSave),
                     nameof(ModelSuggestions), nameof(ModelSuggestNote), nameof(HasModelSuggestions),
                     nameof(ModelInfoText), nameof(HasModelInfo) })
            Raise(name);
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LlmProvider.Protocol):
                Raise(nameof(DraftProtocol));
                Raise(nameof(ProtocolHint));
                break;
            case nameof(LlmProvider.Name):
                Raise(nameof(FormGlyph));
                Raise(nameof(CanSave));
                break;
            case nameof(LlmProvider.Model):
            case nameof(LlmProvider.Models):
            case nameof(LlmProvider.BaseUrl):
                Raise(nameof(CanSave));
                Raise(nameof(ModelSuggestions));
                Raise(nameof(ModelSuggestNote));
                Raise(nameof(HasModelSuggestions));
                Raise(nameof(ModelInfoText));
                Raise(nameof(HasModelInfo));
                break;
            default:
                Raise(nameof(CanSave));
                break;
        }
    }

    public void PickModel(string model)
    {
        if (_draft is not null) _draft.Model = model;
    }

    public void OpenKeyUrl()
    {
        if (_draftPreset?.KeyUrl is not { Length: > 0 } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { PetLog.Write($"打开网页失败: {e.Message}"); }
    }

    /// <summary>表单里的地址：带了 /chat/completions 之类完整端点的，去掉，Astra 自己会拼。</summary>
    private LlmProvider Normalized()
    {
        var copy = _draft!.Clone();
        copy.Name = copy.Name.Trim();
        copy.BaseUrl = ImportKit.NormalizeEndpoint(copy.BaseUrl);
        copy.ApiKey = copy.ApiKey.Trim();
        copy.Model = copy.Model.Trim();
        return copy;
    }

    public async Task TestDraftAsync()
    {
        if (_draft is null || FormTesting) return;
        var p = Normalized();
        FormTesting = true;
        FormTestOk = false;
        FormTestText = $"正在请求 {p.Model}…";
        try
        {
            var (reply, elapsed) = await LlmClient.TestAsync(p, Math.Max(64, _settings.MaxTokens),
                TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            FormTestOk = true;
            FormTestText = $"连接成功 · {elapsed.TotalMilliseconds:0} ms · 模型回复：{reply}";
        }
        catch (Exception e)
        {
            FormTestText = $"测试失败：{e.Message}";
        }
        finally
        {
            FormTesting = false;
        }
    }

    /// <summary>问接口要模型列表，填进候选。很多接口不开放这个端点，失败了照常手填。</summary>
    public async Task FetchModelsAsync()
    {
        if (_draft is null || Fetching) return;
        var p = Normalized();
        Fetching = true;
        FetchOk = false;
        FetchText = "正在获取模型列表…";
        try
        {
            var models = await LlmClient.ListModelsAsync(p, TimeSpan.FromSeconds(Math.Min(30, _settings.TimeoutSeconds)));
            if (_draft is null) return;
            _draft.Models = models.ToList();
            if (string.IsNullOrWhiteSpace(_draft.Model)) _draft.Model = models[0];
            FetchOk = true;
            FetchText = $"获取到 {models.Count} 个模型";
        }
        catch (Exception e)
        {
            FetchText = $"获取失败：{e.Message}。可以直接手动填写模型名。";
        }
        finally
        {
            Fetching = false;
        }
    }

    public void SaveForm()
    {
        if (_draft is null || !CanSave) return;
        var result = Normalized();
        if (_editing is { } original)
        {
            original.CopyFrom(result);
            if (_cardById.TryGetValue(original.Id, out var card)) card.TestText = "";
        }
        else
        {
            _settings.Providers.Add(result);
            // 第一个提供商自动成为当前使用；之后新增的不抢，开关在卡片上。
            if (_settings.Providers.Count == 1) _settings.ActiveProviderId = result.Id;
            Notice = $"已添加「{result.Name}」";
        }
        Close();
    }

    /// <summary>删除要点两下：第一下按钮变成确认，第二下才真删。</summary>
    public void DeleteEditing()
    {
        if (_editing is not { } provider) return;
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return;
        }
        var name = provider.Name;
        var wasActive = _settings.ActiveProvider?.Id == provider.Id;
        _settings.Providers.Remove(provider);
        if (wasActive) _settings.ActiveProviderId = _settings.Providers.FirstOrDefault()?.Id ?? "";
        Close();
        Notice = $"已删除「{name}」";
    }

    /// <summary>
    /// 截图验证用：环境变量 ASTRA_PET_DIALOG 指定启动时打开哪个对话框。
    /// picker / add:预设id / edit / import / import:来源名（等扫描完直接点进去）。
    /// </summary>
    public void OpenForTest(string spec)
    {
        var parts = spec.Split(':', 2);
        switch (parts[0])
        {
            case "picker": OpenPicker(); break;
            case "add" when ProviderPresets.ById(parts.ElementAtOrDefault(1)) is { } preset: BeginAdd(preset); break;
            case "edit" when Cards.FirstOrDefault() is { } card: BeginEdit(card); break;
            case "import":
                OpenImport();
                if (parts.Length > 1)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        if (Sources.FirstOrDefault(s => s.Name == parts[1]) is { } item) OpenSource(item);
                    };
                    timer.Start();
                }
                break;
        }
    }

    // ───── 模型库 ─────

    private bool _catalogUpdating;
    private string _catalogMessage = "";

    /// <summary>选择对话框里的一行小字：模型库来自哪、什么时候的；更新失败时显示原因。</summary>
    public string CatalogLine => _catalogMessage.Length > 0 ? _catalogMessage : ModelCatalog.Current.StatusText;

    public bool CatalogUpdating
    {
        get => _catalogUpdating;
        private set { if (Set(ref _catalogUpdating, value)) Raise(nameof(CatalogUpdateText)); }
    }

    public string CatalogUpdateText => _catalogUpdating ? "正在更新…" : "更新模型库";

    /// <summary>重新从 models.dev 下载模型库（5~6 MB）。成功后卡片和表单里的候选自动刷新。</summary>
    public async Task UpdateCatalogAsync()
    {
        if (CatalogUpdating) return;
        CatalogUpdating = true;
        _catalogMessage = "";
        Raise(nameof(CatalogLine));
        try
        {
            await ModelCatalog.RefreshAsync();
        }
        catch (Exception e)
        {
            _catalogMessage = $"更新失败：{e.Message}";
            Raise(nameof(CatalogLine));
        }
        finally
        {
            CatalogUpdating = false;
        }
    }

    private void OnCatalogUpdated()
    {
        Raise(nameof(CatalogLine));
        RefreshActive();   // 卡片上的模型数和标签来自模型库
        foreach (var name in new[] { nameof(ModelSuggestions), nameof(ModelSuggestNote), nameof(HasModelSuggestions),
                     nameof(ModelInfoText), nameof(HasModelInfo) })
            Raise(name);
    }

    // ───── 导入 ─────

    public IReadOnlyList<ImportSourceItem> Sources { get => _sources; private set => Set(ref _sources, value); }

    public ImportSourceItem? CurrentSource
    {
        get => _currentSource;
        private set
        {
            if (!Set(ref _currentSource, value)) return;
            Raise(nameof(IsImportList));
            Raise(nameof(IsImportDetail));
            Raise(nameof(ImportCandidates));
            Raise(nameof(CurrentSourceName));
            Raise(nameof(SelectedCount));
            Raise(nameof(ImportButtonText));
            Raise(nameof(CanImport));
        }
    }

    public bool IsImportList => _currentSource is null;
    public bool IsImportDetail => _currentSource is not null;
    public string CurrentSourceName => _currentSource?.Name ?? "";
    public IReadOnlyList<ImportCandidate> ImportCandidates => _currentSource?.Candidates ?? [];
    public int SelectedCount => ImportCandidates.Count(c => c.Selected);
    public bool CanImport => SelectedCount > 0;
    public string ImportButtonText => SelectedCount > 0 ? $"导入所选（{SelectedCount}）" : "导入所选";

    public string ImportError
    {
        get => _importError;
        private set { if (Set(ref _importError, value)) Raise(nameof(HasImportError)); }
    }

    public bool HasImportError => _importError.Length > 0;

    /// <summary>打开导入对话框，并在后台把每个来源的默认位置都扫一遍，行上实时显示结果。</summary>
    public void OpenImport()
    {
        ImportError = "";
        CurrentSource = null;
        var items = ImportSources.All.Select(s => (Source: s, Item: new ImportSourceItem(s.Name, s.Where, IconOf(s.Id), GlyphOf(s.Id)))).ToList();
        Sources = items.Select(i => i.Item).ToList();
        Dialog = ProviderDialog.Import;
        foreach (var (source, item) in items)
            _ = ScanAsync(source, item);
    }

    private static string IconOf(string id) => id switch
    {
        "codex" => "openai",
        "claude" => "claude",
        "dsh" => "deepseek",
        _ => "",
    };

    private static string GlyphOf(string id) => id == "ccswitch" ? "CC" : "";

    private static async Task ScanAsync(IImportSource source, ImportSourceItem item)
    {
        var scan = await Task.Run(source.Scan);
        item.SetResult(scan);
    }

    public void OpenSource(ImportSourceItem item)
    {
        if (!item.CanOpen) return;
        ShowCandidates(item);
    }

    private void ShowCandidates(ImportSourceItem item)
    {
        foreach (var c in item.Candidates)
        {
            c.PropertyChanged -= OnCandidateChanged;
            c.MarkDuplicate(_settings.Providers.Any(c.SameAs));
            c.PropertyChanged += OnCandidateChanged;
        }
        ImportError = "";
        CurrentSource = item;
        Dialog = ProviderDialog.Import;
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportCandidate.Selected)) return;
        Raise(nameof(SelectedCount));
        Raise(nameof(ImportButtonText));
        Raise(nameof(CanImport));
    }

    public void BackToSources()
    {
        ImportError = "";
        CurrentSource = null;
    }

    /// <summary>手动选了一个配置文件（默认位置找不到时用）：按文件名和扩展名认出是哪个应用的。</summary>
    public void ImportFromFile(string path)
    {
        var source = ImportSources.ForFile(path);
        if (source is null)
        {
            ImportError = $"不认识这个文件：{Path.GetFileName(path)}。支持 CC Switch 的 .db、Codex 的 config.toml / auth.json、Claude Code 的 settings.json、DeepSeek Harness 的 .yaml。";
            return;
        }
        var item = new ImportSourceItem(source.Name, ImportKit.Pretty(path), IconOf(source.Id), GlyphOf(source.Id));
        var scan = source.ScanFile(path);
        item.SetResult(scan);
        if (!item.CanOpen)
        {
            ImportError = $"{source.Name}：{scan.Status}";
            return;
        }
        ShowCandidates(item);
    }

    public void SelectAllCandidates(bool selected)
    {
        foreach (var c in ImportCandidates) c.Selected = selected;
    }

    public void ImportSelected()
    {
        if (_currentSource is not { } source) return;
        var picked = ImportCandidates.Where(c => c.Selected).ToList();
        if (picked.Count == 0) return;
        var hadAny = _settings.Providers.Count > 0;
        LlmProvider? first = null;
        foreach (var candidate in picked)
        {
            var provider = candidate.ToProvider();
            first ??= provider;
            _settings.Providers.Add(provider);
        }
        if (!hadAny && first is not null) _settings.ActiveProviderId = first.Id;
        PetLog.Write($"从 {source.Name} 导入了 {picked.Count} 个提供商");
        Close();
        Notice = $"已从 {source.Name} 导入 {picked.Count} 个提供商" + (hadAny ? "，在卡片上打开开关即可使用" : "");
    }
}
