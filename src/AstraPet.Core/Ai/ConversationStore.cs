using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>智能体对话的快照：最近几轮（含工具调用和结果）、较早对话的摘要、最后一次聊天的时间。</summary>
public sealed record ConversationSnapshot(
    IReadOnlyList<IReadOnlyList<ChatMessage>> Turns,
    string? Summary,
    DateTimeOffset? LastAt)
{
    public static readonly ConversationSnapshot Empty = new([], null, null);
    public bool IsEmpty => Turns.Count == 0 && string.IsNullOrWhiteSpace(Summary);
}

/// <summary>
/// 把对话存成 JSON 文件，重启后接着聊。每只宠物一个文件（conversations/宠物 id.json），换宠物不串台。
/// 手写 JsonNode 读写：消息的几种块是多态的，AOT 下不走反射序列化。读不出来（损坏、版本不对）就当没有。
/// </summary>
public static class ConversationStore
{
    public const int Version = 1;

    public static string PathFor(string dataDirectory, string petId)
    {
        var safe = string.Concat((petId.Length == 0 ? "default" : petId).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return Path.Combine(dataDirectory, "conversations", safe + ".json");
    }

    public static ConversationSnapshot? Load(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PetLog.Write($"读取聊天记录失败: {e.Message}");
            return null;
        }
    }

    /// <summary>空对话直接删掉文件。先写临时文件再替换，写到一半退出不会弄坏旧文件。</summary>
    public static void Save(string path, ConversationSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(snapshot).ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PetLog.Write($"保存聊天记录失败: {e.Message}");
        }
    }

    /// <summary>删掉所有宠物的聊天记录（关掉「保存聊天记录」时）。</summary>
    public static void DeleteAll(string dataDirectory)
    {
        try
        {
            var dir = Path.Combine(dataDirectory, "conversations");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PetLog.Write($"删除聊天记录失败: {e.Message}");
        }
    }

    public static JsonObject ToJson(ConversationSnapshot snapshot)
    {
        var turns = new JsonArray();
        foreach (var turn in snapshot.Turns)
        {
            var messages = new JsonArray();
            foreach (var m in turn)
            {
                var parts = new JsonArray();
                foreach (var part in m.Parts)
                    parts.Add((JsonNode?)(part switch
                    {
                        TextPart t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
                        ToolCallPart c => new JsonObject { ["type"] = "call", ["id"] = c.CallId, ["name"] = c.Name, ["args"] = c.ArgumentsJson },
                        ToolResultPart r => new JsonObject { ["type"] = "result", ["id"] = r.CallId, ["content"] = r.Content, ["error"] = r.IsError },
                        _ => null,
                    }));
                messages.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["parts"] = parts });
            }
            turns.Add((JsonNode)messages);
        }
        var o = new JsonObject { ["version"] = Version, ["turns"] = turns };
        if (snapshot.Summary is { Length: > 0 } s) o["summary"] = s;
        if (snapshot.LastAt is { } at) o["lastAt"] = at.ToString("O");
        return o;
    }

    public static ConversationSnapshot? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || Int(o["version"]) != Version) return null;
            var turns = new List<IReadOnlyList<ChatMessage>>();
            foreach (var t in o["turns"] as JsonArray ?? [])
            {
                var messages = new List<ChatMessage>();
                foreach (var m in t as JsonArray ?? [])
                {
                    if (Str(m?["role"]) is not ("user" or "assistant") || m?["parts"] is not JsonArray partsJson) continue;
                    var parts = new List<ChatPart>();
                    foreach (var p in partsJson)
                    {
                        ChatPart? part = Str(p?["type"]) switch
                        {
                            "text" => new TextPart(Str(p?["text"]) ?? ""),
                            "call" => new ToolCallPart(Str(p?["id"]) ?? "", Str(p?["name"]) ?? "", Str(p?["args"]) ?? "{}"),
                            "result" => new ToolResultPart(Str(p?["id"]) ?? "", Str(p?["content"]) ?? "",
                                p?["error"] is JsonValue e && e.TryGetValue<bool>(out var err) && err),
                            _ => null,
                        };
                        if (part is not null) parts.Add(part);
                    }
                    if (parts.Count > 0) messages.Add(new ChatMessage(Str(m!["role"])!, parts));
                }
                // 一轮总是从用户那句开始；残缺的轮次丢掉，免得发给接口时报格式错。
                if (messages.Count > 0 && messages[0].Role == "user") turns.Add(messages);
            }
            var lastAt = Str(o["lastAt"]) is { } a && DateTimeOffset.TryParse(a, out var d) ? d : (DateTimeOffset?)null;
            return new ConversationSnapshot(turns, Str(o["summary"]), lastAt);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
