using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AstraPet.Core;
using AstraPet.Core.External;
using AstraPet.Import;

namespace AstraPet.External;

/// <summary>绑定状态：没绑 / 绑好了 / 绑了但程序路径或事件对不上（要重写）/ 配置文件读不懂（不敢写）。</summary>
public enum AgentBindState { NotBound, Bound, Stale, Broken }

/// <summary>识别结果：装没装、hook 配置写在哪。</summary>
public sealed record AgentDetection(bool Installed, string ConfigPath);

/// <summary>
/// 一个用 JSON hooks 配置的编程智能体（design/agent-hooks §5）：Claude Code 写在 settings.json 的 hooks 字段，
/// Codex 写在 hooks.json，两家格式相同：<c>{"hooks": {"事件": [{"hooks": [{"type":"command","command":…}]}]}}</c>。
/// 只追加、只删除自己的条目（命令是 <c>"&lt;程序路径&gt;" hook &lt;agent&gt; &lt;event&gt;</c>），别的工具的 hook 原样保留。
/// </summary>
public sealed partial class HookJsonAgent
{
    /// <summary>hook 自己的超时（秒），转发命令 1 秒内就会退出，这是第二道保险。</summary>
    public const int HookTimeoutSeconds = 5;
    public const string BackupSuffix = ".astrapet.bak";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<string> _home;
    private readonly string _file;
    private readonly string _command;

    /// <param name="home">配置目录（每次现取：环境变量可能变，测试指向临时目录）。</param>
    /// <param name="file">hook 配置文件名。</param>
    /// <param name="command">命令行里的命令名，用来在 PATH 里找。</param>
    /// <param name="events">智能体的事件名 → 我们的事件名。</param>
    public HookJsonAgent(string id, Func<string> home, string file, string command,
        IReadOnlyList<(string Native, string Ours)> events, string? afterBindHint = null)
    {
        Id = id;
        _home = home;
        _file = file;
        _command = command;
        Events = events;
        AfterBindHint = afterBindHint;
    }

    public string Id { get; }
    public string Name => AgentKinds.NameOf(Id);
    public IReadOnlyList<(string Native, string Ours)> Events { get; }
    /// <summary>绑定后要告诉用户的话（Codex：去确认信任）。</summary>
    public string? AfterBindHint { get; }
    public string ConfigPath => Path.Combine(_home(), _file);

    /// <summary>配置目录存在或命令在 PATH 里就算装了。只看存不存在，不运行它。</summary>
    public AgentDetection Detect() =>
        new(Directory.Exists(_home()) || OnPath(_command), ConfigPath);

    public static string CommandFor(string exePath, string agent, string ev) => $"\"{exePath}\" hook {agent} {ev}";

    [GeneratedRegex("^\"(?<path>[^\"]+)\" hook (?<agent>[a-z]+) (?<event>[a-z-]+)$")]
    private static partial Regex OurCommand();

    public AgentBindState State(string exePath)
    {
        string text;
        try
        {
            if (!File.Exists(ConfigPath)) return AgentBindState.NotBound;
            text = File.ReadAllText(ConfigPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return AgentBindState.Broken; }
        if (Parse(text) is not { } root) return AgentBindState.Broken;

        var found = Ours(root).ToList();
        if (found.Count == 0) return AgentBindState.NotBound;
        var expected = Events.Select(e => (e.Native, CommandFor(exePath, Id, e.Ours))).ToHashSet();
        var actual = found.Select(f => (f.Native, f.Command)).ToHashSet();
        return expected.SetEquals(actual) ? AgentBindState.Bound : AgentBindState.Stale;
    }

    /// <summary>写入（或修正）我们的 hook。配置文件读不懂时抛 <see cref="InvalidDataException"/>，不写。</summary>
    public void Bind(string exePath) => Edit(root =>
    {
        RemoveOurs(root);
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var (native, ours) in Events)
        {
            if (hooks[native] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[native] = groups;
            }
            groups.Add((JsonNode)new JsonObject
            {
                ["hooks"] = new JsonArray((JsonNode)new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = CommandFor(exePath, Id, ours),
                    ["timeout"] = HookTimeoutSeconds,
                }),
            });
        }
    }, backup: true);

    /// <summary>只删我们的条目；删空的事件键、空的 hooks 字段一起删掉。没有配置文件时什么也不做。</summary>
    public void Unbind()
    {
        if (!File.Exists(ConfigPath)) return;
        Edit(RemoveOurs, backup: false);
    }

    private IEnumerable<(string Native, string Command)> Ours(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) yield break;
        foreach (var (native, node) in hooks)
        {
            if (node is not JsonArray groups) continue;
            foreach (var group in groups)
                if (group?["hooks"] is JsonArray list)
                    foreach (var h in list)
                        if (IsOurs(h, out var command)) yield return (native, command);
        }
    }

    private bool IsOurs(JsonNode? hook, out string command)
    {
        command = "";
        if (hook?["command"] is not JsonValue v || !v.TryGetValue<string>(out var c)) return false;
        var m = OurCommand().Match(c);
        if (!m.Success || m.Groups["agent"].Value != Id) return false;
        command = c;
        return true;
    }

    private void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var native in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[native] is not JsonArray groups) continue;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray list) continue;
                var removed = false;
                for (var j = list.Count - 1; j >= 0; j--)
                    if (IsOurs(list[j], out _)) { list.RemoveAt(j); removed = true; }
                if (removed && list.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) hooks.Remove(native);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    /// <summary>
    /// 读 → 改 → 原子替换。写之前再读一次，内容变了（智能体正好在写自己的设置）就从头来，最多 3 次。
    /// 第一次改之前备份原文件（已有备份就不覆盖，保留最早的原样）。
    /// </summary>
    private void Edit(Action<JsonObject> change, bool backup)
    {
        var path = ConfigPath;
        for (var attempt = 0; ; attempt++)
        {
            var before = File.Exists(path) ? File.ReadAllText(path) : null;
            var root = before is null ? new JsonObject() : Parse(before)
                ?? throw new InvalidDataException($"{ImportKit.Pretty(path)} 不是合法的 JSON 对象，请先修好再绑定");
            change(root);
            var after = root.ToJsonString(Pretty) + Environment.NewLine;
            if (after == before) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var now = File.Exists(path) ? File.ReadAllText(path) : null;
            if (now != before)
            {
                if (attempt >= 2) throw new IOException($"{ImportKit.Pretty(path)} 一直在被改动，稍后再试");
                continue;
            }
            if (backup && before is not null && !File.Exists(path + BackupSuffix))
                File.WriteAllText(path + BackupSuffix, before);
            var temp = path + ".astrapet.tmp";
            File.WriteAllText(temp, after, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            PetLog.Write($"{Name} 的 hook 配置已更新：{ImportKit.Pretty(path)}");
            return;
        }
    }

    private static JsonObject? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private static bool OnPath(string command)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
            : [command];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                if (names.Any(n => File.Exists(Path.Combine(dir, n)))) return true;
            }
            catch (ArgumentException) { }
        }
        return false;
    }
}

/// <summary>支持的智能体（design/agent-hooks §1.1、§3.1）。DeepSeek Harness 的 hook 机制还没查清，暂不在列。</summary>
public static class AgentIntegrations
{
    private static string EnvOr(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } custom ? custom : Path.Combine(ImportKit.Home, fallback);

    public static HookJsonAgent Claude { get; } = new(AgentKinds.Claude,
        () => EnvOr("CLAUDE_CONFIG_DIR", ".claude"), "settings.json", "claude",
        [
            ("UserPromptSubmit", AgentHookEvents.TurnStart),
            ("Stop", AgentHookEvents.TurnEnd),
            ("StopFailure", AgentHookEvents.TurnFailed),
            ("PermissionRequest", AgentHookEvents.Waiting),
            ("SessionEnd", AgentHookEvents.SessionEnd),
        ],
        "已经打开的 Claude Code 会话不会重新读 hook，新开一个会话就生效。");

    public static HookJsonAgent Codex { get; } = new(AgentKinds.Codex,
        () => EnvOr("CODEX_HOME", ".codex"), "hooks.json", "codex",
        [
            ("UserPromptSubmit", AgentHookEvents.TurnStart),
            ("Stop", AgentHookEvents.TurnEnd),
            ("PermissionRequest", AgentHookEvents.Waiting),
            ("Interrupt", AgentHookEvents.Interrupted),
        ],
        "Codex 要你确认信任新的 hook 才会执行：下次打开 Codex 时按提示确认。");

    public static IReadOnlyList<HookJsonAgent> All { get; } = [Claude, Codex];

    /// <summary>程序路径变了（更新、移动）时把已绑定的条目改到新路径。启动时调一次，出错只记日志。</summary>
    public static void RepairStale(string exePath)
    {
        foreach (var agent in All)
        {
            try
            {
                if (agent.State(exePath) == AgentBindState.Stale)
                {
                    agent.Bind(exePath);
                    PetLog.Write($"{agent.Name} 的 hook 路径过期，已改到 {exePath}");
                }
            }
            catch (Exception e) { PetLog.Write($"修正 {agent.Name} 的 hook 失败: {e.Message}"); }
        }
    }

    /// <summary>全部解除（卸载时、控制台「全部解除绑定」）。</summary>
    public static void UnbindAll()
    {
        foreach (var agent in All)
        {
            try { agent.Unbind(); }
            catch (Exception e) { PetLog.Write($"解除 {agent.Name} 的 hook 失败: {e.Message}"); }
        }
    }
}

/// <summary>
/// <c>AstraPet hook &lt;agent&gt; &lt;event&gt;</c>：智能体的 hook 调它，它把一次事件转给正在运行的宠物（design/agent-hooks §3）。
/// 只取 stdin JSON 里的 session_id、cwd，不取 prompt 和工具参数。无论成败都返回 0、不往 stdout 写东西，
/// 宠物没开、端口不通都不能卡住或打断智能体。<c>AstraPet hook --unbind-all</c> 解除全部绑定（卸载用）。
/// </summary>
public static class AgentHookCommand
{
    private const int MaxStdin = 64 * 1024;
    private static readonly TimeSpan StdinWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(1);

    public static int Run(string settingsFile, string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[1] == "--unbind-all")
            {
                AgentIntegrations.UnbindAll();
                return 0;
            }
            if (args.Length < 3 || !AgentKinds.IsKnown(args[1]) || !AgentHookEvents.All.Contains(args[2])) return 0;
            var (enabled, port, token) = ReadSettings(settingsFile);
            var input = ReadStdin();
            if (!enabled || token.Length == 0) return 0;
            var body = BuildBody(args[1], args[2], input);
            SendAsync(port, token, body).GetAwaiter().GetResult();
        }
        catch (Exception e) { PetLog.Write($"转发 hook 失败: {e.Message}"); }
        return 0;
    }

    /// <summary>从 hook 的 stdin JSON 里取会话和项目目录名，组成发给宠物的请求体。</summary>
    public static JsonObject BuildBody(string agent, string ev, string input)
    {
        string? session = null, cwd = null;
        try
        {
            if (input.Length > 0 && JsonNode.Parse(input) is JsonObject o)
            {
                session = o["session_id"] is JsonValue s && s.TryGetValue<string>(out var sv) ? sv : null;
                cwd = o["cwd"] is JsonValue c && c.TryGetValue<string>(out var cv) ? cv : null;
            }
        }
        catch (JsonException) { }
        cwd ??= Environment.CurrentDirectory;
        var project = Path.GetFileName(cwd.TrimEnd('/', '\\'));
        var body = new JsonObject { ["agent"] = agent, ["event"] = ev, ["session"] = session ?? "default" };
        if (project.Length > 0) body["project"] = project;
        return body;
    }

    private static string ReadStdin()
    {
        if (!Console.IsInputRedirected) return "";
        var read = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[MaxStdin];
            var length = 0;
            int n;
            while (length < buffer.Length && (n = stdin.Read(buffer, length, buffer.Length - length)) > 0) length += n;
            return length >= buffer.Length ? "" : Encoding.UTF8.GetString(buffer, 0, length);
        });
        return read.Wait(StdinWait) ? read.Result : "";
    }

    private static async Task SendAsync(int port, string token, JsonObject body)
    {
        using var http = new HttpClient { Timeout = SendTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/agent-hooks")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
    }

    /// <summary>只读设置文件里的三个字段，不走 SettingsStore（不迁移、不回写）。</summary>
    private static (bool Enabled, int Port, string Token) ReadSettings(string settingsFile)
    {
        try
        {
            if (File.Exists(settingsFile) && JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject o)
            {
                var enabled = o[nameof(PetSettings.ExternalApi)] is JsonValue e && e.TryGetValue<bool>(out var b) && b;
                var (port, token) = McpCommand.ReadEndpoint(settingsFile);
                return (enabled, port, token);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return (false, 0, "");
    }
}
