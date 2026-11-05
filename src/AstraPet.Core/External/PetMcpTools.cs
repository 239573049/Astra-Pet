using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstraPet.Core.External;

/// <summary>把 MCP 工具调用转成对本地 HTTP 接口的一次请求：方法、路径、请求体 → 状态码、响应体。</summary>
public delegate Task<(int Status, JsonNode? Body)> ExternalTransport(string method, string path, JsonObject? body, CancellationToken ct);

/// <summary>一个 MCP 工具的定义（名字、描述、参数的 JSON Schema）。</summary>
public sealed record McpToolSpec(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// <c>AstraPet mcp</c> 暴露的工具（03 §2.2），和 MCP SDK 无关：协议、stdio 由官方 SDK（ModelContextProtocol.Core）处理，
/// 这里只定义工具、把调用转发给正在运行的宠物的 HTTP 接口。
/// </summary>
public sealed class PetMcpTools(ExternalTransport transport, string? unavailableHint = null)
{
    public const string Instructions =
        "控制用户桌面上的宠物 Astra Pet：做动作、说一句话。用来在任务开始、完成、出错时给用户一个轻量的提示，不要频繁调用。";

    /// <summary>不转义中文，模型读起来省 token。</summary>
    private static readonly JsonSerializerOptions Readable =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<McpToolSpec> Tools { get; } =
    [
        Tool("pet_status", "查看桌面宠物的状态：当前角色、正在播的动作、是否暂停或省电。", new JsonObject()),
        Tool("get_pet_status", "查看桌面宠物的饱腹、精力、心情、亲密度和姿态（只读）。", new JsonObject()),
        Tool("pet_list_actions", "列出桌面宠物能做的动作（id、名字、描述、角色）。", new JsonObject()),
        Tool("pet_play_action", "让桌面宠物做一个动作。给 id（先用 pet_list_actions 查），或者给角色 role，比如 celebrate、happy、sad、think、greet。",
            new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "动作 id" },
                ["role"] = new JsonObject { ["type"] = "string", ["description"] = "角色，程序挑一个合适的动作" },
                ["loopSeconds"] = new JsonObject { ["type"] = "number", ["description"] = "循环播放多少秒（可选，最多 600）" },
            }),
        Tool("pet_say", "让桌面宠物头顶冒一句话（最多 200 字）。", new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要说的话" },
                ["speak"] = new JsonObject { ["type"] = "boolean", ["description"] = "同时念出来（用户开了朗读时）" },
            }, required: "text"),
    ];

    private static McpToolSpec Tool(string name, string description, JsonObject properties, string? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is not null) schema["required"] = new JsonArray(required);
        return new McpToolSpec(name, description, schema);
    }

    /// <summary>执行一个工具，返回给模型看的文字和是否出错。</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject? args, CancellationToken ct = default)
    {
        args ??= new JsonObject();
        (string Method, string Path, JsonObject? Body)? route = name switch
        {
            "pet_status" or "get_pet_status" => ("GET", "/v1/status", null),
            "pet_list_actions" => ("GET", "/v1/actions", null),
            "pet_play_action" => ("POST", "/v1/actions/play", Pick(args, "id", "role", "loopSeconds")),
            "pet_say" => ("POST", "/v1/say", Pick(args, "text", "speak")),
            _ => null,
        };
        if (route is not { } r) return ("没有这个工具：" + name, true);

        int status;
        JsonNode? body;
        try
        {
            (status, body) = await transport(r.Method, r.Path, r.Body, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (unavailableHint ?? "连不上桌面宠物：" + e.Message, true);
        }

        if (status is >= 200 and < 300) return (body?.ToJsonString(Readable) ?? "{}", false);
        var error = body?["error"] is JsonValue ev && ev.TryGetValue<string>(out var msg) ? msg : $"HTTP {status}";
        return (error, true);
    }

    /// <summary>只转发认识的参数。</summary>
    private static JsonObject Pick(JsonObject args, params string[] keys)
    {
        var o = new JsonObject();
        foreach (var k in keys)
            if (args[k] is { } v) o[k] = v.DeepClone();
        return o;
    }
}
