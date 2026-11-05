using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AstraPet.Core;

namespace AstraPet.Infrastructure.Update;

/// <summary>
/// 从更新服务器（Astra Hub）取发布信息：GET {服务器}/api/pet-releases/{渠道}/latest.json?platform=…（地址由 UpdateChecker 拼好）。
/// 404 表示这个渠道还没有发布过版本，返回 null。
/// </summary>
public sealed class HttpUpdateFeed : IUpdateFeed
{
    private static readonly HttpClient Http = CreateHttp();

    public async Task<ReleaseFeed?> GetLatestAsync(Uri url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var feed = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(ct), UpdateJsonContext.Default.LatestFeed, ct);
        return feed is null ? null : Map(feed);
    }

    /// <summary>站点返回的 JSON 换成 Core 的类型。单独拿出来方便测解析。</summary>
    internal static ReleaseFeed? Parse(string json) =>
        JsonSerializer.Deserialize(json, UpdateJsonContext.Default.LatestFeed) is { } feed ? Map(feed) : null;

    private static ReleaseFeed Map(LatestFeed feed) => new(
        feed.Version, feed.Channel, feed.Notes, feed.Page,
        (feed.Files ?? []).Select(f => new ReleaseFile(f.Platform, f.Kind, f.Url, f.Size)).ToList());

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AstraPet", AppVersion.Current));
        return http;
    }
}

/// <summary>/api/pet-releases/{channel}/latest.json 的返回。只取用得到的字段。</summary>
internal sealed class LatestFeed
{
    public string? Version { get; set; }
    public string? Channel { get; set; }
    public string? Notes { get; set; }
    public string? Page { get; set; }
    public List<LatestFile>? Files { get; set; }
}

internal sealed class LatestFile
{
    public string? Platform { get; set; }
    public string? Kind { get; set; }
    public string? Url { get; set; }
    public long Size { get; set; }
}

/// <summary>源生成的 JSON 元数据，NativeAOT 下不能用反射式序列化。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LatestFeed))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
