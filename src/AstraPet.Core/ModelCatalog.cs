using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstraPet.Core;

/// <summary>
/// 一个模型的参数，来自 models.dev：上下文窗口、最大输出、能力开关、输入模态、价格（美元 / 百万 token）。
/// <see cref="Temperature"/> 为 null 表示目录里没写，当作支持；false 表示这个模型不接受 temperature 参数
/// （推理模型常见），请求时要省掉它。
/// </summary>
public sealed record ModelInfo(
    string Id,
    string Name,
    int Context,
    int MaxOutput,
    bool Reasoning,
    bool ToolCall,
    bool? Temperature,
    bool Attachment,
    IReadOnlyList<string> Input,
    double? CostInput,
    double? CostOutput,
    string Released,
    bool Deprecated)
{
    /// <summary>界面上一行小字：上下文、最大输出、能力、价格。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Context > 0) parts.Add($"上下文 {Tokens(Context)}");
            if (MaxOutput > 0) parts.Add($"最大输出 {Tokens(MaxOutput)}");
            if (Reasoning) parts.Add("推理");
            if (ToolCall) parts.Add("工具调用");
            if (Input.Contains("image")) parts.Add("图片输入");
            if (Temperature == false) parts.Add("不支持 temperature");
            if (CostInput is { } i && CostOutput is { } o) parts.Add($"${Price(i)} / ${Price(o)} 每百万 token");
            if (Deprecated) parts.Add("已弃用");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 131072 → 128K、1048576 → 1M、128000 → 128K、200000 → 200K。
    /// 只有 2 的幂（8192、32768、262144…）才按 1024 算，其余按 1000——128000 恰好是 125×1024，不能当 125K。
    /// </summary>
    public static string Tokens(int n)
    {
        var pow2 = n > 0 && (n & (n - 1)) == 0;
        if (n >= 1_000_000) return pow2 ? $"{n / 1_048_576.0:0.#}M" : $"{n / 1_000_000.0:0.#}M";
        if (n >= 1000) return pow2 ? $"{n / 1024}K" : $"{n / 1000.0:0.#}K";
        return n.ToString();
    }

    private static string Price(double v) => v >= 10 ? $"{v:0}" : v >= 1 ? $"{v:0.##}" : $"{v:0.###}";
}

/// <summary>目录里的一家提供商：地址、SDK 类型（决定协议）、它的模型。</summary>
public sealed class CatalogProvider(string id, string name, string api, string npm, IReadOnlyList<ModelInfo> models)
{
    private readonly Dictionary<string, ModelInfo> _byId =
        models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>OpenAI 兼容 / Anthropic 兼容的接口地址；openai、anthropic、google 等官方 SDK 的没有。</summary>
    public string Api { get; } = api;
    public string Npm { get; } = npm;
    public IReadOnlyList<ModelInfo> Models { get; } = models;

    public ModelInfo? Find(string? modelId) => modelId is not null && _byId.TryGetValue(modelId.Trim(), out var m) ? m : null;
}

/// <summary>
/// 内置的模型参数库，来源是 https://models.dev/api.json（各家提供商的模型、上下文窗口、能力和价格）。
/// 程序里内嵌一份裁剪过的快照（Assets/models.dev.json，tools/fetch_models_dev.py 生成），
/// 用户点「更新模型库」时重新下载、裁剪，缓存在设置目录的 models.dev.json，之后启动优先用较新的那份。
/// 只留预设用到的提供商（<see cref="Whitelist"/>）和对话模型，其余约 200 家用不上。
/// 全程只读：目录里的值用来预填地址、给模型候选、在界面上显示参数，以及请求时省掉模型不支持的 temperature、
/// 把 max_tokens 限制在模型的最大输出以内；不会因为目录里没有某个模型就拒绝使用它。
/// </summary>
public sealed class ModelCatalog
{
    public const string SourceUrl = "https://models.dev/api.json";

    /// <summary>要保留的 models.dev 提供商 id。和 tools/fetch_models_dev.py 的 WHITELIST 保持一致。</summary>
    public static readonly string[] Whitelist =
    [
        "deepseek", "alibaba-cn", "alibaba", "moonshotai-cn", "moonshotai", "zhipuai", "zai",
        "minimax-cn", "minimax", "volcengine", "stepfun", "stepfun-ai", "siliconflow-cn", "siliconflow",
        "modelscope", "openai", "anthropic", "google", "xai", "openrouter", "mistral", "groq", "perplexity",
    ];

    private static readonly Lock Gate = new();
    private static ModelCatalog? _current;

    private readonly Dictionary<string, CatalogProvider> _providers;

    private ModelCatalog(IEnumerable<CatalogProvider> providers, DateTimeOffset fetched, bool fromCache)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Fetched = fetched;
        FromCache = fromCache;
    }

    /// <summary>目录更新了（点了「更新模型库」且成功）。在调用刷新的线程上触发。</summary>
    public static event Action? Updated;

    public static ModelCatalog Current
    {
        get
        {
            lock (Gate) return _current ??= LoadBest();
        }
    }

    public DateTimeOffset Fetched { get; }
    /// <summary>true 表示来自用户更新过的缓存，false 是程序内嵌的快照。</summary>
    public bool FromCache { get; }
    public IReadOnlyCollection<CatalogProvider> Providers => _providers.Values;
    public int ModelCount => _providers.Values.Sum(p => p.Models.Count);

    public string StatusText =>
        $"模型库来自 models.dev · {(FromCache ? "已更新" : "内置快照")} {Fetched.LocalDateTime:yyyy-MM-dd} · {_providers.Count} 家、{ModelCount} 个模型";

    private static string CachePath => Path.Combine(SettingsStore.Directory, "models.dev.json");

    public CatalogProvider? Provider(string? id) =>
        string.IsNullOrEmpty(id) ? null : _providers.GetValueOrDefault(id);

    /// <summary>
    /// 一个已配置提供商对应目录里的哪一家：先按接口地址的主机名精确对，这样国际版 / 国内版（moonshot.ai 与 moonshot.cn）
    /// 不会弄混；对不上再看它的预设。都对不上返回 null（本地模型、自建网关、目录里没有的厂商）。
    /// </summary>
    public CatalogProvider? ProviderFor(LlmProvider provider)
    {
        var host = HostOf(provider.BaseUrl);
        if (host.Length > 0)
            foreach (var p in _providers.Values)
                if (p.Api.Length > 0 && HostOf(p.Api) == host)
                    return p;
        return Provider(ProviderPresets.Resolve(provider)?.CatalogId);
    }

    public ModelInfo? Find(LlmProvider provider) => ProviderFor(provider)?.Find(provider.Model);

    /// <summary>可以推荐给用户的模型：对话用、没弃用，按发布日期从新到旧。</summary>
    public IReadOnlyList<ModelInfo> Suggest(CatalogProvider provider) =>
        provider.Models.Where(m => !m.Deprecated)
            .OrderByDescending(m => m.Released, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> SuggestIds(LlmProvider provider) =>
        ProviderFor(provider) is { } cp ? Suggest(cp).Select(m => m.Id).ToList() : [];

    /// <summary>
    /// 用目录里的数据修正一个预设：模型候选换成目录里的最新列表（预设里手写的模型排前面，当作首选）；
    /// 目录给了 OpenAI / Anthropic 兼容地址时，地址和协议也以目录为准。目录里没有这家就原样返回。
    /// </summary>
    public ProviderPreset Apply(ProviderPreset preset)
    {
        if (Provider(preset.CatalogId) is not { } cp) return preset;

        var ids = Suggest(cp).Select(m => m.Id).ToList();
        var preferred = preset.Models.Where(m => ids.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        var models = ids.Count == 0 ? preset.Models : preferred.Concat(ids.Except(preferred, StringComparer.OrdinalIgnoreCase)).ToArray();

        var baseUrl = preset.BaseUrl;
        var protocol = preset.Protocol;
        if (cp.Api.Length > 0 && preset.BaseUrl.Length > 0 && preset.Protocol != LlmProtocol.Responses
            && cp.Npm is "@ai-sdk/openai-compatible" or "@ai-sdk/anthropic")
        {
            baseUrl = cp.Api.TrimEnd('/');
            protocol = cp.Npm == "@ai-sdk/anthropic" ? LlmProtocol.Anthropic : LlmProtocol.ChatCompletions;
        }
        return preset with { BaseUrl = baseUrl, Protocol = protocol, Models = models };
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";

    // ───── 加载 ─────

    private static ModelCatalog LoadBest()
    {
        ModelCatalog? embedded = null, cached = null;
        try
        {
            using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("models.dev.json");
            if (stream is not null) embedded = Parse(JsonNode.Parse(stream), fromCache: false);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            PetLog.Write($"内置模型库读取失败: {e.Message}");
        }
        try
        {
            if (File.Exists(CachePath)) cached = Parse(JsonNode.Parse(File.ReadAllText(CachePath)), fromCache: true);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            PetLog.Write($"模型库缓存读取失败，改用内置快照: {e.Message}");
        }
        // 缓存是用户更新出来的，通常更新；但程序升级后内嵌的快照可能反过来更新，所以比时间。
        if (cached is { ProviderCount: > 0 } && (embedded is null || cached.Fetched >= embedded.Fetched)) return cached;
        return embedded ?? new ModelCatalog([], DateTimeOffset.MinValue, false);
    }

    private int ProviderCount => _providers.Count;

    /// <summary>
    /// 读目录 JSON。完整的 api.json（models 是以 id 为键的对象）和裁剪过的快照（models 是数组）都认，
    /// 只留 <see cref="Whitelist"/> 里的提供商和对话模型（type 缺省或 chat、输出含文本）。
    /// </summary>
    internal static ModelCatalog Parse(JsonNode? root, bool fromCache, DateTimeOffset? fetchedOverride = null)
    {
        var fetched = fetchedOverride ?? DateTimeOffset.MinValue;
        if (fetchedOverride is null && Str(root?["_meta"], "fetched") is { } stamp
            && DateTimeOffset.TryParse(stamp, out var parsed))
            fetched = parsed;

        var providers = new List<CatalogProvider>();
        if (root is JsonObject obj)
        {
            foreach (var id in Whitelist)
            {
                if (obj[id] is not JsonObject p) continue;
                var items = p["models"] switch
                {
                    JsonObject map => map.Select(kv => kv.Value),
                    JsonArray list => list,
                    _ => [],
                };
                var models = items.Select(ParseModel).Where(m => m is not null).Select(m => m!).ToList();
                providers.Add(new CatalogProvider(id, Str(p, "name") ?? id, Str(p, "api") ?? "", Str(p, "npm") ?? "", models));
            }
        }
        return new ModelCatalog(providers, fetched, fromCache);
    }

    private static ModelInfo? ParseModel(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o, "id") is not { } id) return null;
        if (Str(o, "type") is { } type && type != "chat") return null;
        var input = Strings(o["modalities"]?["input"]);
        var output = Strings(o["modalities"]?["output"]);
        if (output.Count > 0 && !output.Contains("text")) return null;

        return new ModelInfo(
            id,
            Str(o, "name") ?? id,
            (int)Num(o["limit"]?["context"]),
            (int)Num(o["limit"]?["output"]),
            Bool(o["reasoning"]) == true,
            Bool(o["tool_call"]) == true,
            Bool(o["temperature"]),
            Bool(o["attachment"]) == true,
            input.Count == 0 ? ["text"] : input,
            o["cost"]?["input"] is JsonValue ci && ci.TryGetValue<double>(out var cin) ? cin : null,
            o["cost"]?["output"] is JsonValue co && co.TryGetValue<double>(out var cout) ? cout : null,
            Str(o, "release_date") ?? "",
            Str(o, "status") == "deprecated");
    }

    private static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static double Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null).Select(s => s!).ToList()
            : [];

    // ───── 在线更新 ─────

    /// <summary>
    /// 从 models.dev 重新下载，裁剪后存进设置目录并替换当前目录。失败（断网、返回的不是目录）抛异常，当前目录不变。
    /// 下载的是 5~6 MB 的完整文件，解析放在后台线程。
    /// </summary>
    public static async Task<ModelCatalog> RefreshAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AstraPet");
        var bytes = await http.GetByteArrayAsync(SourceUrl, ct);

        var catalog = await Task.Run(() => Parse(JsonNode.Parse(bytes), fromCache: true, DateTimeOffset.UtcNow), ct);
        if (catalog.ProviderCount == 0 || catalog.ModelCount == 0)
            throw new InvalidDataException("下载到的内容不是 models.dev 的模型目录");

        Directory.CreateDirectory(SettingsStore.Directory);
        var temp = CachePath + ".tmp";
        await File.WriteAllTextAsync(temp, catalog.ToJson(), ct);
        File.Move(temp, CachePath, overwrite: true);

        lock (Gate) _current = catalog;
        PetLog.Write($"模型库已更新: {catalog.ProviderCount} 家、{catalog.ModelCount} 个模型");
        Updated?.Invoke();
        return catalog;
    }

    /// <summary>写成和内嵌快照一样的裁剪格式（tools/fetch_models_dev.py 的输出）。</summary>
    private string ToJson()
    {
        var root = new JsonObject
        {
            ["_meta"] = new JsonObject { ["source"] = SourceUrl, ["fetched"] = Fetched.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
        };
        foreach (var p in _providers.Values)
        {
            var models = new JsonArray();
            foreach (var m in p.Models)
            {
                var o = new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["reasoning"] = m.Reasoning, ["tool_call"] = m.ToolCall, ["attachment"] = m.Attachment };
                if (m.Temperature is { } t) o["temperature"] = t;
                if (m.Released.Length > 0) o["release_date"] = m.Released;
                if (m.Deprecated) o["status"] = "deprecated";
                o["modalities"] = new JsonObject
                {
                    ["input"] = new JsonArray(m.Input.Select(x => (JsonNode?)x).ToArray()),
                    ["output"] = new JsonArray("text"),
                };
                o["limit"] = new JsonObject { ["context"] = m.Context, ["output"] = m.MaxOutput };
                if (m.CostInput is { } ci && m.CostOutput is { } co) o["cost"] = new JsonObject { ["input"] = ci, ["output"] = co };
                models.Add((JsonNode)o);
            }
            var entry = new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["npm"] = p.Npm, ["models"] = models };
            if (p.Api.Length > 0) entry["api"] = p.Api;
            root[p.Id] = entry;
        }
        return root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
