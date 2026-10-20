using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Import;

/// <summary>从别的应用里读到的一个提供商，还没有写进 Astra 的设置。</summary>
public sealed class ImportCandidate : Observable
{
    private bool _selected;
    private bool _duplicate;

    public string Name { get; init; } = "";
    /// <summary>来源，比如「CC Switch · Claude」。</summary>
    public string Source { get; init; } = "";
    public LlmProtocol Protocol { get; init; }
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public List<string> Models { get; init; } = new();
    public string PresetId { get; init; } = "";
    /// <summary>非空表示导入不了（比如协议不支持），内容是原因。</summary>
    public string Skip { get; init; } = "";
    /// <summary>补充说明，比如「原应用里正在使用」「没有 API Key」。</summary>
    public string Note { get; init; } = "";

    public bool CanSelect => Skip.Length == 0 && !_duplicate;
    /// <summary>有 Key（或本机接口不需要 Key）。没有 Key 的能导入，但默认不勾选。</summary>
    public bool Usable => ApiKey.Length > 0 || ProviderText.IsLocal(BaseUrl);
    public bool Selected { get => _selected; set => Set(ref _selected, value && CanSelect); }
    /// <summary>设置里已经有同地址、同 Key、同模型的提供商。</summary>
    public bool Duplicate => _duplicate;

    public string Host => ProviderText.Host(BaseUrl);
    public string ProtocolLabel => ProviderText.ProtocolShort(Protocol);
    public string KeyMask => ProviderText.MaskKey(ApiKey);

    /// <summary>列表里那一行下面的小字：地址、协议、模型、状态说明。</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Skip.Length > 0) parts.Add(Skip);
            else
            {
                parts.Add(Host);
                parts.Add(ProtocolLabel);
                if (Model.Length > 0) parts.Add(Model);
                if (Note.Length > 0) parts.Add(Note);
            }
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public void MarkDuplicate(bool duplicate)
    {
        _duplicate = duplicate;
        Raise(nameof(Duplicate));
        Raise(nameof(CanSelect));
        Raise(nameof(Detail));
        Selected = CanSelect && Usable;
    }

    public bool SameAs(LlmProvider p) =>
        string.Equals(ProviderText.TrimUrl(p.BaseUrl), ProviderText.TrimUrl(BaseUrl), StringComparison.OrdinalIgnoreCase)
        && p.ApiKey == ApiKey && p.Model == Model;

    public LlmProvider ToProvider() => new()
    {
        Name = Name,
        Protocol = Protocol,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        PresetId = PresetId,
        Models = Models.ToList(),
    };
}

/// <summary>扫描一个来源的结果。</summary>
public sealed record ImportScan(IReadOnlyList<ImportCandidate> Candidates, string Status, bool Found)
{
    public static ImportScan NotFound(string status = "未检测到") => new([], status, false);
    public static ImportScan Failed(string message) => new([], $"读取失败：{message}", true);
}

/// <summary>一个可以导入提供商的应用。</summary>
public interface IImportSource
{
    string Id { get; }
    string Name { get; }
    /// <summary>界面上展示的配置位置，比如 ~/.cc-switch/cc-switch.db。</summary>
    string Where { get; }
    /// <summary>扫描默认位置。不抛异常，出错返回 <see cref="ImportScan.Failed"/>。</summary>
    ImportScan Scan();
    /// <summary>用户手选的文件是不是这个来源的格式。</summary>
    bool Accepts(string path);
    /// <summary>扫描手选的文件。</summary>
    ImportScan ScanFile(string path);
}

public static class ImportSources
{
    public static readonly IReadOnlyList<IImportSource> All =
    [
        new CcSwitchSource(),
        new CodexSource(),
        new ClaudeCodeSource(),
        new DshSource(),
    ];

    /// <summary>手选文件时按格式找来源。</summary>
    public static IImportSource? ForFile(string path) => All.FirstOrDefault(s => s.Accepts(path));
}

/// <summary>几个来源共用的小工具。</summary>
internal static class ImportKit
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>展示用：把用户目录缩成 ~。</summary>
    public static string Pretty(string path) =>
        path.StartsWith(Home, StringComparison.Ordinal) ? "~" + path[Home.Length..] : path;

    /// <summary>JSON 对象里取字符串字段，空白当作没有。</summary>
    public static string? Str(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : null;

    /// <summary>
    /// 接口地址去掉尾部的端点：不少人会把完整的 …/chat/completions 填进来，
    /// 而 Astra 自己会拼路径。
    /// </summary>
    public static string NormalizeEndpoint(string url)
    {
        url = url.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/responses", "/v1/messages", "/messages" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length];
                break;
            }
        return url.TrimEnd('/');
    }

    /// <summary>
    /// 组一个候选。预设按 <paramref name="presetId"/> 找，找不到再按地址猜；
    /// 名字为空时用预设名，没有预设就用地址的主机名。
    /// </summary>
    public static ImportCandidate Make(string? name, string source, LlmProtocol protocol, string baseUrl,
        string? apiKey, string? model, IEnumerable<string>? models = null, string note = "", string skip = "",
        string presetId = "")
    {
        baseUrl = NormalizeEndpoint(baseUrl);
        var preset = ProviderPresets.ById(presetId) ?? ProviderPresets.MatchUrl(baseUrl);
        var list = (models ?? []).Append(model ?? "").Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim()).Distinct().ToList();
        var modelName = string.IsNullOrWhiteSpace(model)
            ? preset is null ? "" : ModelCatalog.Current.Apply(preset).Models.FirstOrDefault() ?? ""
            : model.Trim();
        var displayName = string.IsNullOrWhiteSpace(name)
            ? preset?.Name ?? ProviderText.Host(baseUrl)
            : name.Trim();
        if (modelName.Length == 0 && skip.Length == 0)
            note = string.Join(" · ", new[] { note, "没有指定模型，导入后需要填写" }.Where(n => n.Length > 0));
        return new ImportCandidate
        {
            Name = displayName,
            Source = source,
            Protocol = protocol,
            BaseUrl = baseUrl,
            ApiKey = apiKey?.Trim() ?? "",
            Model = modelName,
            Models = list,
            PresetId = preset is null ? "" : AltPresetId(preset, protocol),
            Skip = skip,
            Note = note,
        };
    }

    /// <summary>同一家有多个协议版本的预设（OpenAI / OpenAI Responses），按协议挑对应的 id。</summary>
    private static string AltPresetId(ProviderPreset preset, LlmProtocol protocol) =>
        preset.Protocol == protocol
            ? preset.Id
            : ProviderPresets.All.FirstOrDefault(p => p.Icon == preset.Icon && p.Protocol == protocol)?.Id ?? preset.Id;

    /// <summary>读 dotenv 文件：KEY=VALUE，支持 export 前缀、引号和 # 注释。</summary>
    public static Dictionary<string, string> ReadDotenv(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
                value = value[..hash].TrimEnd();
            if (value.Length > 0) map[key] = value;
        }
        return map;
    }
}
