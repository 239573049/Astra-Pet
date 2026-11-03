using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;
using Tomlyn;
using Tomlyn.Model;

namespace AstraPet.Import;

/// <summary>
/// Codex CLI：~/.codex/config.toml（可用 CODEX_HOME 换位置）里的 [model_providers.*] 是自定义提供商，
/// 顶层 model_provider / model 指明正在用哪个；Key 来自 auth.json 的 OPENAI_API_KEY、
/// 提供商里的 experimental_bearer_token，或者 env_key 指向的环境变量（也看 ~/.codex/.env）。
/// 只用 ChatGPT 账号登录的没有 API Key，导入不了。
/// </summary>
public sealed class CodexSource : IImportSource
{
    public string Id => "codex";
    public string Name => "Codex";
    public string Where => ImportKit.Pretty(Path.Combine(CodexHome, "config.toml"));

    private static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom ? custom : Path.Combine(ImportKit.Home, ".codex");

    public bool Accepts(string path) =>
        path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("auth.json", StringComparison.OrdinalIgnoreCase);

    public ImportScan Scan()
    {
        var config = Path.Combine(CodexHome, "config.toml");
        return File.Exists(config) || File.Exists(Path.Combine(CodexHome, "auth.json"))
            ? ScanFile(config)
            : ImportScan.NotFound();
    }

    public ImportScan ScanFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? CodexHome;
            var configPath = Path.GetFileName(path).Equals("auth.json", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(dir, "config.toml")
                : path;
            var toml = File.Exists(configPath) ? File.ReadAllText(configPath) : "";

            string? authKey = null;
            var chatgptLogin = false;
            var authPath = Path.Combine(dir, "auth.json");
            if (File.Exists(authPath) && JsonNode.Parse(File.ReadAllText(authPath)) is JsonObject auth)
            {
                authKey = ImportKit.Str(auth, "OPENAI_API_KEY");
                chatgptLogin = authKey is null && auth["tokens"] is JsonObject;
            }

            var dotenv = ImportKit.ReadDotenv(Path.Combine(dir, ".env"));
            string? Env(string name)
            {
                if (dotenv.TryGetValue(name, out var fromFile)) return fromFile;
                var fromProcess = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(fromProcess) ? null : fromProcess;
            }

            var found = FromToml(toml, authKey, Env, "Codex", null, null);
            if (found.Count > 0)
                return new ImportScan(found, $"找到 {found.Count(c => c.Skip.Length == 0)} 个提供商", true);
            return new ImportScan([], chatgptLogin
                ? "只用 ChatGPT 账号登录，没有 API Key 可导入"
                : "没有自定义提供商，也没有 API Key", true);
        }
        catch (Exception e) when (e is TomlException or JsonException or IOException)
        {
            return ImportScan.Failed(e.Message);
        }
    }

    /// <summary>
    /// 从 config.toml 文本里读出提供商。<paramref name="authKey"/> 是 auth.json 里的 API Key，
    /// 只给正在用的、或声明了 requires_openai_auth 的提供商。
    /// </summary>
    internal static IReadOnlyList<ImportCandidate> FromToml(string toml, string? authKey, Func<string, string?> env,
        string source, string? nameOverride, string? apiFormat)
    {
        var doc = string.IsNullOrWhiteSpace(toml) ? new TomlTable() : TomlSerializer.Deserialize<TomlTable>(toml)!;
        var activeId = S(doc, "model_provider");
        var topModel = S(doc, "model");
        var providers = doc.TryGetValue("model_providers", out var mp) ? mp as TomlTable : null;
        var profiles = doc.TryGetValue("profiles", out var pf) ? pf as TomlTable : null;

        var result = new List<ImportCandidate>();
        foreach (var (id, value) in providers is null ? [] : providers.ToList())
        {
            if (value is not TomlTable table || S(table, "base_url") is not { } baseUrl) continue;
            var isActive = id == activeId;
            var model = isActive
                ? topModel
                : profiles?.Values.OfType<TomlTable>().FirstOrDefault(p => S(p, "model_provider") == id) is { } profile
                    ? S(profile, "model")
                    : null;

            var envName = S(table, "env_key");
            var key = S(table, "experimental_bearer_token")
                      ?? (envName is null ? null : env(envName))
                      ?? (isActive || table.TryGetValue("requires_openai_auth", out var ra) && ra is true ? authKey : null);
            var notes = new List<string>();
            if (isActive) notes.Add("当前使用");
            if (key is null && envName is not null) notes.Add($"没有 API Key（环境变量 {envName} 未设置）");
            else if (key is null) notes.Add("没有 API Key");

            result.Add(ImportKit.Make(nameOverride ?? S(table, "name") ?? id, source, ProtocolOf(S(table, "wire_api"), apiFormat),
                baseUrl, key, model, note: string.Join(" · ", notes)));
        }

        // 没有自定义提供商、但填了 OpenAI 的 API Key：就是直连 OpenAI（也可能用 openai_base_url 指向别处）。
        var builtin = activeId is null or "openai" && providers?.ContainsKey("openai") != true;
        if (builtin && authKey is not null && result.Count == 0)
            result.Add(ImportKit.Make(nameOverride ?? "OpenAI", source, ProtocolOf(null, apiFormat),
                S(doc, "openai_base_url") ?? "https://api.openai.com/v1", authKey, topModel, note: "当前使用"));
        return result;
    }

    /// <summary>wire_api 是 chat 才走 Chat Completions，其余（responses）走 Responses。CC Switch 的 apiFormat 优先。</summary>
    private static LlmProtocol ProtocolOf(string? wireApi, string? apiFormat) => apiFormat switch
    {
        "anthropic" => LlmProtocol.Anthropic,
        "openai_chat" => LlmProtocol.ChatCompletions,
        "openai_responses" => LlmProtocol.Responses,
        _ => string.Equals(wireApi, "chat", StringComparison.OrdinalIgnoreCase)
            ? LlmProtocol.ChatCompletions
            : LlmProtocol.Responses,
    };

    private static string? S(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

/// <summary>
/// Claude Code：~/.claude/settings.json（可用 CLAUDE_CONFIG_DIR 换位置）的 env 里写着
/// ANTHROPIC_BASE_URL / ANTHROPIC_AUTH_TOKEN（或 ANTHROPIC_API_KEY）/ ANTHROPIC_MODEL，
/// 用中转站或国产兼容接口的人都是这么配的。直接用 Claude 账号登录的没有 Key，导入不了。
/// </summary>
public sealed class ClaudeCodeSource : IImportSource
{
    public string Id => "claude";
    public string Name => "Claude Code";
    public string Where => ImportKit.Pretty(DefaultPath);

    private static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom ? custom : Path.Combine(ImportKit.Home, ".claude"),
        "settings.json");

    public bool Accepts(string path) =>
        Path.GetFileName(path).StartsWith("settings", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public ImportScan Scan() => File.Exists(DefaultPath) ? ScanFile(DefaultPath) : ImportScan.NotFound();

    public ImportScan ScanFile(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
                return ImportScan.Failed("不是 JSON 对象");

            var candidate = FromEnv(root["env"] as JsonObject, null, "Claude Code", null, "当前使用",
                fallbackModel: ImportKit.Str(root, "model"), helper: ImportKit.Str(root, "apiKeyHelper"));
            return candidate is null
                ? new ImportScan([], "没有配置接口地址或 API Key（可能是账号登录）", true)
                : new ImportScan([candidate], "找到 1 个提供商", true);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return ImportScan.Failed(e.Message);
        }
    }

    private static readonly HashSet<string> ClaudeAliases = ["sonnet", "opus", "haiku", "opusplan", "default", "best"];

    /// <summary>去掉 [1m] 之类的上下文后缀；剩下的是别名就丢掉（返回 null）。</summary>
    private static string? CleanClaudeModel(string? model)
    {
        if (model is null) return null;
        var bracket = model.IndexOf('[');
        var name = (bracket > 0 ? model[..bracket] : model).Trim();
        return name.Length == 0 || ClaudeAliases.Contains(name.ToLowerInvariant()) ? null : name;
    }

    /// <summary>
    /// 从 Claude 风格的 env 读一个提供商，CC Switch 的 claude 条目也走这里。
    /// 没有 Key 也没有自定义地址说明走的是账号登录，返回 null。
    /// </summary>
    internal static ImportCandidate? FromEnv(JsonObject? env, string? name, string source, string? apiFormat, string note,
        string? fallbackModel = null, string? helper = null)
    {
        var key = ImportKit.Str(env, "ANTHROPIC_AUTH_TOKEN") ?? ImportKit.Str(env, "ANTHROPIC_API_KEY");
        var baseUrl = ImportKit.Str(env, "ANTHROPIC_BASE_URL");
        if (key is null && baseUrl is null && helper is null) return null;

        // Claude Code 里的 model 可以写别名（sonnet、opus…）或带 [1m] 后缀，这些不是 API 能认的模型名。
        var models = new[]
            {
                ImportKit.Str(env, "ANTHROPIC_MODEL"),
                ImportKit.Str(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"), ImportKit.Str(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"),
                ImportKit.Str(env, "ANTHROPIC_DEFAULT_HAIKU_MODEL"), ImportKit.Str(env, "ANTHROPIC_SMALL_FAST_MODEL"),
                fallbackModel,
            }
            .Select(CleanClaudeModel).Where(m => m is not null).Select(m => m!).Distinct().ToList();

        // CC Switch 里非 Anthropic 协议的提供商靠它的本地代理转换，meta.apiFormat 记着真实协议。
        var (protocol, skip) = apiFormat switch
        {
            "openai_chat" => (LlmProtocol.ChatCompletions, ""),
            "openai_responses" => (LlmProtocol.Responses, ""),
            "gemini_native" => (LlmProtocol.ChatCompletions, "Gemini 原生接口，Astra 暂不支持"),
            _ => (LlmProtocol.Anthropic, ""),
        };
        if (key is null && helper is not null) note = string.Join(" · ", new[] { note, "Key 由 apiKeyHelper 提供，需要手动填" }.Where(n => n.Length > 0));
        baseUrl ??= "https://api.anthropic.com";
        // 没给名字时：认得出是哪家就用那家的名字，认不出（比如本机网关）就叫 Claude Code。
        name ??= ProviderPresets.MatchUrl(baseUrl) is null ? "Claude Code" : null;
        return ImportKit.Make(name, source, protocol, baseUrl, key, models.FirstOrDefault(), models, note, skip);
    }
}
