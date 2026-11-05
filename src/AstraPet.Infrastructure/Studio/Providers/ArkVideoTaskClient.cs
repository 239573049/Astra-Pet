using System.Text.Json.Nodes;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 火山方舟视频任务协议（05 §2.1）：POST contents/generations/tasks 提交，GET …/{id} 查询，DELETE …/{id} 取消排队中的任务。
/// 地址、密钥、模型可配，任何实现了这个协议的服务都能接。首尾帧用 data URL 传，不要求用户有图床。
/// </summary>
public sealed class ArkVideoTaskClient(HttpClient? http = null) : IVideoGenerator
{
    private const string Tasks = "contents/generations/tasks";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http = http ?? MediaHttp.Shared;

    /// <summary>请求体（测试和脱敏摘要用）。</summary>
    public static JsonObject Body(MediaProvider provider, VideoJob job)
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = job.Prompt },
            new JsonObject
            {
                ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = MediaHttp.DataUrl(job.FirstFrame) },
                ["role"] = "first_frame",
            });
        if (job.LastFrame is { } last)
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = MediaHttp.DataUrl(last) },
                ["role"] = "last_frame",
            });
        var body = new JsonObject
        {
            ["model"] = provider.Model.Trim(),
            ["content"] = content,
            ["ratio"] = job.Ratio,
            ["resolution"] = job.Resolution,
            ["duration"] = job.DurationSeconds,
            ["generate_audio"] = false,
            ["watermark"] = false,
        };
        MediaHttp.MergeExtra(body, provider);
        return body;
    }

    public async Task<string> SubmitAsync(MediaProvider provider, VideoJob job, CancellationToken ct)
    {
        MediaHttp.Check(provider);
        using var message = MediaHttp.Request(HttpMethod.Post, provider, Tasks, MediaHttp.Json(Body(provider, job)));
        var (status, body) = await MediaHttp.SendAsync(_http, message, Timeout, ct);
        var json = MediaHttp.EnsureJson(status, body);
        return json["id"]?.GetValue<string>() is { Length: > 0 } id
            ? id
            : throw new GenerationException($"提交成功但没有返回任务 id：{MediaHttp.Truncate(body)}");
    }

    public async Task<VideoTaskStatus> GetAsync(MediaProvider provider, string taskId, CancellationToken ct)
    {
        using var message = MediaHttp.Request(HttpMethod.Get, provider, $"{Tasks}/{Uri.EscapeDataString(taskId)}");
        var (status, body) = await MediaHttp.SendAsync(_http, message, Timeout, ct);
        return Parse(taskId, MediaHttp.EnsureJson(status, body));
    }

    public static VideoTaskStatus Parse(string taskId, JsonNode json)
    {
        var state = (json["status"]?.GetValue<string>() ?? "") switch
        {
            "queued" => VideoTaskState.Queued,
            "running" => VideoTaskState.Running,
            "succeeded" => VideoTaskState.Succeeded,
            "failed" => VideoTaskState.Failed,
            "cancelled" => VideoTaskState.Cancelled,
            "expired" => VideoTaskState.Expired,
            var other => throw new GenerationException($"不认识的任务状态：「{other}」"),
        };
        var url = json["content"]?["video_url"]?.GetValue<string>();
        long? tokens = json["usage"]?["completion_tokens"] is JsonValue t && t.TryGetValue<long>(out var n) ? n : null;
        string? error = json["error"] switch
        {
            JsonObject e => $"{e["code"]}：{e["message"]}".Trim('：'),
            JsonValue v => v.ToString(),
            _ => null,
        };
        if (state == VideoTaskState.Succeeded && string.IsNullOrEmpty(url))
            return new VideoTaskStatus(taskId, VideoTaskState.Failed, null, tokens, "任务成功但没有返回视频地址");
        return new VideoTaskStatus(taskId, state, url, tokens, error is { Length: > 0 } ? error : null);
    }

    public async Task CancelAsync(MediaProvider provider, string taskId, CancellationToken ct)
    {
        using var message = MediaHttp.Request(HttpMethod.Delete, provider, $"{Tasks}/{Uri.EscapeDataString(taskId)}");
        var (status, body) = await MediaHttp.SendAsync(_http, message, Timeout, ct);
        MediaHttp.EnsureJson(status, body.Length == 0 ? "{}" : body);
    }

    public async Task<string> TestAsync(MediaProvider provider, CancellationToken ct)
    {
        MediaHttp.Check(provider);
        using var message = MediaHttp.Request(HttpMethod.Get, provider, $"{Tasks}/cgt-astra-pet-connection-test");
        var (status, body) = await MediaHttp.SendAsync(_http, message, TimeSpan.FromSeconds(20), ct);
        return MediaHttp.Verdict(status, body, "地址和密钥都没问题（没有提交任务，不花钱）");
    }
}
