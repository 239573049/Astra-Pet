using AstraPet.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace AstraPet.Import;

/// <summary>
/// DeepSeek Harness（deepseek-ai/deepseek-harness，命令 dsh）：配置目录是 $DSH_HOME，默认 ~/.dsh。
/// API Key 存在 .credentials.yaml 的 refs 下（按环境变量名，比如 DEEPSEEK_API_KEY）；
/// 模型提供商写在 profiles/&lt;档案名&gt;/cordis.patch.yml 里 id 为 llm-pi-ai 的条目的 config.providers 下
/// （每个提供商有 apiKeyEnv、baseURL、api、models），DeepSeek 自己的接口是 llm-deepseek 条目。
/// 只填了 DeepSeek Key、没写任何提供商（设置页里最常见的情况）时，按 refs 里的环境变量名认出是哪家。
/// </summary>
public sealed class DshSource : IImportSource
{
    public string Id => "dsh";
    public string Name => "DeepSeek Harness";
    public string Where => ImportKit.Pretty(Home);

    private static string Home =>
        Environment.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } custom ? custom : Path.Combine(ImportKit.Home, ".dsh");

    /// <summary>环境变量名 → 预设。dsh 内置目录里的提供商（route id）和这里对得上的，不写 baseURL 也能还原。</summary>
    private static readonly (string Route, string Env, string Preset, string? BaseUrl)[] Known =
    [
        ("deepseek", "DEEPSEEK_API_KEY", "deepseek", null),
        ("openai", "OPENAI_API_KEY", "openai", null),
        ("anthropic", "ANTHROPIC_API_KEY", "anthropic", null),
        ("moonshotai", "MOONSHOT_API_KEY", "kimi", "https://api.moonshot.ai/v1"),
        ("moonshotai-cn", "MOONSHOT_API_KEY", "kimi", "https://api.moonshot.cn/v1"),
        ("zai", "ZAI_API_KEY", "zhipu", "https://api.z.ai/api/paas/v4"),
        ("zhipuai", "ZHIPU_API_KEY", "zhipu", null),
        ("minimax", "MINIMAX_API_KEY", "minimax", "https://api.minimax.io/v1"),
        ("openrouter", "OPENROUTER_API_KEY", "openrouter", null),
        ("mistral", "MISTRAL_API_KEY", "mistral", null),
        ("groq", "GROQ_API_KEY", "groq", null),
        ("xai", "XAI_API_KEY", "grok", null),
    ];

    public bool Accepts(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    public ImportScan Scan() => Directory.Exists(Home) ? ScanHome(Home, null) : ImportScan.NotFound();

    public ImportScan ScanFile(string path)
    {
        // 选 .credentials.yaml 就扫它所在的配置目录；选某个档案的 cordis.patch.yml 就向上找配置目录，找不到只读这一个文件。
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Path.GetFileName(path).Equals(".credentials.yaml", StringComparison.OrdinalIgnoreCase))
            return ScanHome(dir, null);
        for (var up = dir; up is not null; up = Path.GetDirectoryName(up))
            if (File.Exists(Path.Combine(up, ".credentials.yaml")))
                return ScanHome(up, path);
        return ScanHome(dir, path);
    }

    private static ImportScan ScanHome(string home, string? onlyPatch)
    {
        try
        {
            var refs = ReadRefs(Path.Combine(home, ".credentials.yaml"));
            var dotenv = ImportKit.ReadDotenv(Path.Combine(home, ".env"));
            string? Lookup(string? name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (refs.TryGetValue(name, out var fromRefs)) return fromRefs;
                if (dotenv.TryGetValue(name, out var fromFile)) return fromFile;
                var fromProcess = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(fromProcess) ? null : fromProcess;
            }

            var patches = onlyPatch is not null
                ? [onlyPatch]
                : PatchFiles(home);
            var candidates = new List<ImportCandidate>();
            foreach (var patch in patches)
            {
                var root = new DeserializerBuilder().Build().Deserialize<object?>(File.ReadAllText(patch));
                foreach (var entry in Entries(root))
                    candidates.AddRange(FromEntry(entry, Lookup));
            }

            // 只填了 Key 没写提供商：按环境变量名认出来，已经由上面某个提供商用掉的不重复。
            foreach (var (route, env, preset, baseUrl) in Known)
            {
                if (!refs.TryGetValue(env, out var key)) continue;
                var p = ProviderPresets.ById(preset)!;
                var url = baseUrl ?? p.BaseUrl;
                if (route == "moonshotai-cn") continue;   // 同一个 Key 变量，只出一条
                if (candidates.Any(c => c.ApiKey == key && ProviderText.TrimUrl(c.BaseUrl) == ProviderText.TrimUrl(url))) continue;
                candidates.Add(ImportKit.Make(p.Name, "DeepSeek Harness", p.Protocol, url, key, null, presetId: preset,
                    note: $"来自 {env}"));
            }

            var unique = candidates
                .GroupBy(c => (ProviderText.TrimUrl(c.BaseUrl).ToLowerInvariant(), c.ApiKey, c.Model))
                .Select(g => g.First()).ToList();
            return new ImportScan(unique, unique.Count == 0
                ? "没有找到提供商或 API Key"
                : $"找到 {unique.Count(c => c.Skip.Length == 0)} 个提供商", true);
        }
        catch (Exception e) when (e is YamlException or IOException or UnauthorizedAccessException)
        {
            return ImportScan.Failed(e.Message);
        }
    }

    /// <summary>档案目录下的 cordis.patch.yml：profiles/*/ 里的，加上配置目录根下的。</summary>
    private static IEnumerable<string> PatchFiles(string home)
    {
        var root = Path.Combine(home, "cordis.patch.yml");
        if (File.Exists(root)) yield return root;
        var profiles = Path.Combine(home, "profiles");
        if (!Directory.Exists(profiles)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(profiles).Order())
        {
            var file = Path.Combine(dir, "cordis.patch.yml");
            if (File.Exists(file)) yield return file;
        }
    }

    private static Dictionary<string, string> ReadRefs(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        var root = new DeserializerBuilder().Build().Deserialize<object?>(File.ReadAllText(path));
        if (Get(Map(root), "refs") is { } refs && Map(refs) is { } refMap)
            foreach (var (k, v) in refMap)
                if (Str(v) is { } value) map[Str(k) ?? ""] = value;
        return map;
    }

    /// <summary>找出所有带 config 的插件条目：补丁文件通常是条目列表，但嵌套时也要能找到。</summary>
    private static IEnumerable<IDictionary<object, object>> Entries(object? node)
    {
        if (Map(node) is { } map)
        {
            if (map.ContainsKey("config") && (map.ContainsKey("id") || map.ContainsKey("name"))) yield return map;
            foreach (var child in map.Values)
                foreach (var e in Entries(child)) yield return e;
        }
        else if (node is IEnumerable<object> list)
        {
            foreach (var item in list)
                foreach (var e in Entries(item)) yield return e;
        }
    }

    private static IEnumerable<ImportCandidate> FromEntry(IDictionary<object, object> entry, Func<string?, string?> lookup)
    {
        var id = (Str(Get(entry, "id")) ?? "") + " " + (Str(Get(entry, "name")) ?? "");
        var config = Map(Get(entry, "config"));
        if (config is null) yield break;

        // 通用路线：pi-ai 把各家提供商挂在 providers 下，字典的键就是提供商 id。
        if (id.Contains("llm-pi-ai", StringComparison.Ordinal) && Map(Get(config, "providers")) is { } providers)
        {
            foreach (var (routeKey, profileObj) in providers)
            {
                var route = Str(routeKey) ?? "";
                var profile = Map(profileObj);
                if (profile is null) continue;
                var known = Known.FirstOrDefault(k => k.Route == route);
                var preset = known.Preset is null ? null : ProviderPresets.ById(known.Preset);

                var api = Str(Get(profile, "api"));
                var (protocol, skip) = api is null ? (preset?.Protocol ?? LlmProtocol.ChatCompletions, "") : CcSwitchSource.ProtocolOf(api);
                var baseUrl = Str(Get(profile, "baseURL")) ?? known.BaseUrl ?? preset?.BaseUrl;
                if (baseUrl is null) continue;   // 目录里的内置提供商又不认识：没法知道地址
                var env = Str(Get(profile, "apiKeyEnv")) ?? (known.Env is { Length: > 0 } ? known.Env : null);
                var key = lookup(env);
                var models = Models(Get(profile, "models"));
                yield return ImportKit.Make(Str(Get(profile, "displayName")) ?? preset?.Name ?? route, "DeepSeek Harness",
                    protocol, baseUrl, key, models.FirstOrDefault(), models, skip: skip,
                    presetId: preset?.Id ?? "", note: key is null && env is not null ? $"没有 API Key（{env} 未设置）" : "");
            }
        }
        // DeepSeek 自己的接口（不含账号登录那个，它没有 Key）。
        else if (id.Contains("llm-deepseek", StringComparison.Ordinal) && !id.Contains("account", StringComparison.Ordinal))
        {
            var env = Str(Get(config, "apiKeyEnv")) ?? "DEEPSEEK_API_KEY";
            var key = Str(Get(config, "apiKey")) ?? lookup(env);
            var p = ProviderPresets.ById("deepseek")!;
            var models = Models(Get(config, "models"));
            yield return ImportKit.Make(p.Name, "DeepSeek Harness", p.Protocol, Str(Get(config, "baseURL")) ?? p.BaseUrl,
                key, models.FirstOrDefault(), models, presetId: p.Id, note: key is null ? $"没有 API Key（{env} 未设置）" : "");
        }
    }

    /// <summary>models 可以是 [{id: x}] 也可以是 [x]。</summary>
    private static List<string> Models(object? node) =>
        node is IEnumerable<object> list
            ? list.Select(m => Str(Get(Map(m), "id")) ?? Str(m)).Where(m => m is not null).Select(m => m!).ToList()
            : [];

    private static IDictionary<object, object>? Map(object? o) => o as IDictionary<object, object>;

    private static object? Get(IDictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var v) ? v : null;

    private static string? Str(object? o) => o switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        IDictionary<object, object> or IEnumerable<object> => null,
        _ => Convert.ToString(o)?.Trim(),
    };
}
