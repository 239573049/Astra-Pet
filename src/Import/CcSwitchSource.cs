using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;
using Microsoft.Data.Sqlite;
using Tomlyn;
using Tomlyn.Model;

namespace AstraPet.Import;

/// <summary>
/// CC Switch（farion1231/cc-switch）：提供商存在 ~/.cc-switch/cc-switch.db 的 providers 表，
/// 一行一个，app_type 标明是给哪个工具配的（claude / codex / gemini / opencode / openclaw / hermes / pi…），
/// settings_config 是各工具自己的配置片段（JSON），meta 里的 apiFormat 说明实际协议。
/// 这里把每一行还原成 Astra 的「地址 + Key + 模型 + 协议」。库用只读方式打开一份临时拷贝，不碰原文件。
/// </summary>
public sealed class CcSwitchSource : IImportSource
{
    public string Id => "ccswitch";
    public string Name => "CC Switch";
    public string Where => ImportKit.Pretty(DefaultPath);

    private static string DefaultPath
    {
        get
        {
            // CC Switch 允许在设置里换配置目录，但默认就是用户目录下的 .cc-switch。
            var home = ImportKit.Home;
            return Path.Combine(home, ".cc-switch", "cc-switch.db");
        }
    }

    public bool Accepts(string path) =>
        path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);

    public ImportScan Scan() => File.Exists(DefaultPath) ? ScanFile(DefaultPath) : ImportScan.NotFound();

    public ImportScan ScanFile(string path)
    {
        try { return ReadDatabase(path); }
        catch (Exception e) { return ImportScan.Failed(e.Message); }
    }

    private static ImportScan ReadDatabase(string path)
    {
        // CC Switch 开着时库处于 WAL 模式，最新的改动还在 -wal 里，所以连同 -wal / -shm 一起拷到临时目录再读。
        var temp = Path.Combine(Path.GetTempPath(), "astra-ccs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var copy = Path.Combine(temp, "cc-switch.db");
            File.Copy(path, copy);
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(path + ext)) File.Copy(path + ext, copy + ext);

            var rows = new List<Row>();
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = copy, Pooling = false }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, app_type, name, settings_config, meta, is_current FROM providers " +
                                  "ORDER BY app_type, COALESCE(sort_index, 0), COALESCE(created_at, 0)";
                try
                {
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        rows.Add(new Row(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.IsDBNull(4) ? "" : reader.GetString(4), !reader.IsDBNull(5) && reader.GetInt64(5) != 0));
                }
                catch (SqliteException e) when (e.Message.Contains("no such", StringComparison.OrdinalIgnoreCase))
                {
                    return ImportScan.Failed("不是 CC Switch 的数据库，或版本太旧（需要 v3.8 以上）");
                }
            }

            var candidates = new List<ImportCandidate>();
            foreach (var row in rows)
            {
                try
                {
                    if (Parse(row) is { } candidate) candidates.Add(candidate);
                }
                catch (Exception e) when (e is JsonException or TomlException or InvalidOperationException)
                {
                    // 单条配置坏了不影响其他条。
                }
            }
            var usable = candidates.Count(c => c.Skip.Length == 0);
            var status = candidates.Count == 0 ? "没有可导入的提供商"
                : usable == candidates.Count ? $"找到 {usable} 个提供商"
                : $"找到 {usable} 个可导入，{candidates.Count - usable} 个暂不支持";
            return new ImportScan(candidates, status, true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, true); } catch { /* 临时目录删不掉不影响结果 */ }
        }
    }

    private sealed record Row(string App, string Name, string Settings, string Meta, bool IsCurrent);

    private static ImportCandidate? Parse(Row row)
    {
        var settings = JsonNode.Parse(row.Settings) as JsonObject;
        if (settings is null) return null;
        var meta = string.IsNullOrWhiteSpace(row.Meta) ? null : JsonNode.Parse(row.Meta) as JsonObject;
        var apiFormat = ImportKit.Str(meta, "apiFormat");
        var source = $"CC Switch · {AppLabel(row.App)}";
        var note = row.IsCurrent ? $"{AppLabel(row.App)} 当前在用" : "";

        switch (row.App)
        {
            case "claude":
            case "claude-desktop":
                return ClaudeCodeSource.FromEnv(settings["env"] as JsonObject, row.Name, source, apiFormat, note);

            case "codex":
            {
                var config = ImportKit.Str(settings, "config");
                var authKey = ImportKit.Str(settings["auth"], "OPENAI_API_KEY");
                IReadOnlyList<ImportCandidate> found = config is null
                    ? []
                    : CodexSource.FromToml(config, authKey, _ => null, source, row.Name, apiFormat);
                if (found.Count == 0)
                    return authKey is null
                        ? null
                        : ImportKit.Make(row.Name, source, LlmProtocol.Responses, "https://api.openai.com/v1",
                            authKey, null, note: note);
                // 一行对应一个提供商：取正在用的那个。
                var pick = found.FirstOrDefault(c => c.Note.Contains("当前使用")) ?? found[0];
                return ImportKit.Make(row.Name, source, pick.Protocol, pick.BaseUrl, pick.ApiKey, pick.Model,
                    pick.Models, note, pick.Skip, pick.PresetId);
            }

            case "gemini":
            {
                var env = settings["env"] as JsonObject;
                var key = ImportKit.Str(env, "GEMINI_API_KEY");
                var baseUrl = ImportKit.Str(env, "GOOGLE_GEMINI_BASE_URL") ?? ImportKit.Str(env, "GEMINI_BASE_URL");
                var model = ImportKit.Str(env, "GEMINI_MODEL");
                if (key is null && baseUrl is null) return null;
                // Gemini 原生接口 Astra 不支持，官方地址可以换成它的 OpenAI 兼容端点；中转站的原生接口就没办法了。
                if (baseUrl is null || baseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                    return ImportKit.Make(row.Name, source, LlmProtocol.ChatCompletions,
                        "https://generativelanguage.googleapis.com/v1beta/openai", key, model, note: note, presetId: "gemini");
                return ImportKit.Make(row.Name, source, LlmProtocol.ChatCompletions, baseUrl, key, model, note: note,
                    skip: "Gemini 原生接口，Astra 暂不支持");
            }

            case "opencode":
            {
                var options = settings["options"] as JsonObject;
                var baseUrl = ImportKit.Str(options, "baseURL");
                var npm = ImportKit.Str(settings, "npm") ?? "";
                var models = (settings["models"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
                if (baseUrl is null) return null;
                var (protocol, skip) = ProtocolOf(npm.Contains("anthropic") ? "anthropic"
                    : npm.Contains("google") ? "google"
                    : npm.EndsWith("/openai") ? "responses"
                    : "chat");
                return ImportKit.Make(row.Name, source, protocol, baseUrl, ImportKit.Str(options, "apiKey"),
                    models.FirstOrDefault(), models, note, skip);
            }

            case "openclaw":
            case "hermes":
            case "pi":
            {
                // 三个工具的字段名大同小异：baseUrl / base_url、apiKey / api_key、api / api_mode、models[].id。
                var baseUrl = ImportKit.Str(settings, "baseUrl") ?? ImportKit.Str(settings, "base_url");
                if (baseUrl is null) return null;
                var key = ImportKit.Str(settings, "apiKey") ?? ImportKit.Str(settings, "api_key");
                var api = ImportKit.Str(settings, "api") ?? ImportKit.Str(settings, "api_mode") ?? "chat";
                var models = (settings["models"] as JsonArray)?
                    .Select(m => ImportKit.Str(m, "id") ?? (m is JsonValue v && v.TryGetValue<string>(out var s) ? s : null))
                    .Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToList() ?? [];
                var (protocol, skip) = ProtocolOf(api);
                return ImportKit.Make(row.Name, source, protocol, baseUrl, key, models.FirstOrDefault(), models, note, skip);
            }

            default:
                return null;   // grokbuild、mcode 等 Astra 用不上
        }
    }

    /// <summary>把各工具里五花八门的协议写法归到 Astra 的三种；Gemini 原生等不支持的返回原因。</summary>
    internal static (LlmProtocol Protocol, string Skip) ProtocolOf(string api)
    {
        var a = api.ToLowerInvariant();
        if (a.Contains("anthropic")) return (LlmProtocol.Anthropic, "");
        if (a.Contains("google") || a.Contains("gemini") || a.Contains("bedrock") || a.Contains("vertex"))
            return (LlmProtocol.ChatCompletions, "协议暂不支持");
        if (a.Contains("responses")) return (LlmProtocol.Responses, "");
        return (LlmProtocol.ChatCompletions, "");
    }

    private static string AppLabel(string app) => app switch
    {
        "claude" => "Claude Code",
        "claude-desktop" => "Claude Desktop",
        "codex" => "Codex",
        "gemini" => "Gemini CLI",
        "opencode" => "OpenCode",
        "openclaw" => "OpenClaw",
        "hermes" => "Hermes",
        "pi" => "Pi",
        _ => app,
    };
}
