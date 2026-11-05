using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 图片生成的公共部分：要几张就并行发几次单张请求（各家对 n 的支持不一，Seedream 根本没有），
/// 部分失败时成功的照样返回。返回 url 的结果当场下载。
/// </summary>
public abstract class ImageClientBase(HttpClient? http) : IImageGenerator
{
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(600);
    protected readonly HttpClient Http = http ?? MediaHttp.Shared;

    public async Task<ImageResult> GenerateAsync(MediaProvider provider, ImageJob job, CancellationToken ct)
    {
        MediaHttp.Check(provider);
        var runs = Enumerable.Range(0, Math.Max(1, job.Count)).Select(async _ =>
        {
            try { return (Images: await GenerateOneAsync(provider, job, ct), Error: (string?)null); }
            catch (GenerationException e) { return (Images: (IReadOnlyList<byte[]>)[], Error: e.Message); }
        }).ToList();
        var results = await Task.WhenAll(runs);
        ct.ThrowIfCancellationRequested();
        var images = results.SelectMany(r => r.Images).ToList();
        var errors = results.Where(r => r.Error is not null).Select(r => r.Error!).Distinct().ToList();
        if (images.Count == 0) throw new GenerationException(errors.FirstOrDefault() ?? "没有返回图片");
        return new ImageResult(images, errors);
    }

    protected abstract Task<IReadOnlyList<byte[]>> GenerateOneAsync(MediaProvider provider, ImageJob job, CancellationToken ct);

    public abstract Task<string> TestAsync(MediaProvider provider, CancellationToken ct);

    /// <summary>OpenAI 和方舟的返回都是 { data: [ { b64_json } | { url } ] }。</summary>
    protected async Task<IReadOnlyList<byte[]>> ReadImagesAsync(JsonNode json, CancellationToken ct)
    {
        if (json["data"] is not JsonArray data || data.Count == 0)
            throw new GenerationException($"没有返回图片：{MediaHttp.Truncate(json.ToJsonString())}");
        var images = new List<byte[]>();
        foreach (var item in data)
        {
            if (item?["b64_json"]?.GetValue<string>() is { Length: > 0 } b64)
                images.Add(Convert.FromBase64String(b64));
            else if (item?["url"]?.GetValue<string>() is { Length: > 0 } url)
            {
                try { images.Add(await Http.GetByteArrayAsync(url, ct)); }
                catch (HttpRequestException e) { throw new GenerationException($"下载生成的图片失败：{e.Message}"); }
            }
            else if (item?["error"] is JsonNode error)
                throw new GenerationException($"生成失败：{error["message"] ?? error}");
        }
        return images.Count > 0 ? images : throw new GenerationException("返回里没有图片数据");
    }
}

/// <summary>
/// OpenAI 兼容图片接口（05 §3.2）：有参考图走 /images/edits（multipart，image[] 可多张），没有走 /images/generations。
/// 用法照 tools/petgen/make_figure.py。
/// </summary>
public sealed class OpenAIImagesClient(HttpClient? http = null) : ImageClientBase(http)
{
    public static JsonObject GenerationsBody(MediaProvider provider, ImageJob job)
    {
        var body = new JsonObject
        {
            ["model"] = provider.Model.Trim(), ["prompt"] = job.Prompt, ["size"] = job.Size, ["n"] = 1,
            ["output_format"] = "png",
        };
        if (job.Quality is { Length: > 0 } q) body["quality"] = q;
        MediaHttp.MergeExtra(body, provider);
        return body;
    }

    public static MultipartFormDataContent EditsBody(MediaProvider provider, ImageJob job)
    {
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in GenerationsBody(provider, job))
            if (value is not null) form.Add(new StringContent(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString()), key);
        for (var i = 0; i < job.References.Count; i++)
        {
            var image = new ByteArrayContent(job.References[i]);
            var mime = MediaHttp.MimeOf(job.References[i]);
            image.Headers.ContentType = new MediaTypeHeaderValue(mime);
            form.Add(image, "image[]", $"ref-{i + 1}.{mime[6..]}");
        }
        return form;
    }

    protected override async Task<IReadOnlyList<byte[]>> GenerateOneAsync(MediaProvider provider, ImageJob job, CancellationToken ct)
    {
        using var message = job.References.Count > 0
            ? MediaHttp.Request(HttpMethod.Post, provider, "images/edits", EditsBody(provider, job))
            : MediaHttp.Request(HttpMethod.Post, provider, "images/generations", MediaHttp.Json(GenerationsBody(provider, job)));
        var (status, body) = await MediaHttp.SendAsync(Http, message, Timeout, ct);
        return await ReadImagesAsync(MediaHttp.EnsureJson(status, body), ct);
    }

    /// <summary>GET /models：大多数 OpenAI 兼容接口都有，不计费。</summary>
    public override async Task<string> TestAsync(MediaProvider provider, CancellationToken ct)
    {
        MediaHttp.Check(provider);
        using var message = MediaHttp.Request(HttpMethod.Get, provider, "models");
        var (status, body) = await MediaHttp.SendAsync(Http, message, TimeSpan.FromSeconds(20), ct);
        return MediaHttp.Verdict(status, body, "地址和密钥都没问题（没有生成图片，不花钱）");
    }
}

/// <summary>火山方舟 Seedream（05 §3.2）：/images/generations，参考图用 data URL 放进 image 数组。</summary>
public sealed class ArkImagesClient(HttpClient? http = null) : ImageClientBase(http)
{
    public static JsonObject Body(MediaProvider provider, ImageJob job)
    {
        var body = new JsonObject
        {
            ["model"] = provider.Model.Trim(), ["prompt"] = job.Prompt, ["size"] = job.Size,
            ["response_format"] = "b64_json", ["watermark"] = false,
        };
        if (job.References.Count > 0)
            body["image"] = new JsonArray(job.References.Select(r => (JsonNode)JsonValue.Create(MediaHttp.DataUrl(r))).ToArray());
        MediaHttp.MergeExtra(body, provider);
        return body;
    }

    protected override async Task<IReadOnlyList<byte[]>> GenerateOneAsync(MediaProvider provider, ImageJob job, CancellationToken ct)
    {
        using var message = MediaHttp.Request(HttpMethod.Post, provider, "images/generations", MediaHttp.Json(Body(provider, job)));
        var (status, body) = await MediaHttp.SendAsync(Http, message, Timeout, ct);
        return await ReadImagesAsync(MediaHttp.EnsureJson(status, body), ct);
    }

    /// <summary>发一个缺参数的请求：鉴权先于参数校验，401 说明密钥不对，400 说明鉴权过了。不计费。</summary>
    public override async Task<string> TestAsync(MediaProvider provider, CancellationToken ct)
    {
        MediaHttp.Check(provider);
        using var message = MediaHttp.Request(HttpMethod.Post, provider, "images/generations", MediaHttp.Json(new JsonObject()));
        var (status, body) = await MediaHttp.SendAsync(Http, message, TimeSpan.FromSeconds(20), ct);
        return MediaHttp.Verdict(status, body, "地址和密钥都没问题（没有生成图片，不花钱）");
    }
}

/// <summary>按协议挑客户端。</summary>
public static class MediaClients
{
    public static IImageGenerator Image(MediaProtocol protocol, HttpClient? http = null) => protocol switch
    {
        MediaProtocol.ArkImages => new ArkImagesClient(http),
        MediaProtocol.OpenAIImages => new OpenAIImagesClient(http),
        _ => throw new GenerationException("这个接口不是图片协议"),
    };

    public static IVideoGenerator Video(MediaProtocol protocol, HttpClient? http = null) => protocol switch
    {
        MediaProtocol.ArkVideoTasks => new ArkVideoTaskClient(http),
        _ => throw new GenerationException("这个接口不是视频协议"),
    };

    public static Task<string> TestAsync(MediaProvider provider, CancellationToken ct) => provider.Kind == MediaKind.Video
        ? Video(provider.Protocol).TestAsync(provider, ct)
        : Image(provider.Protocol).TestAsync(provider, ct);
}
