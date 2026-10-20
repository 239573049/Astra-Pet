using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraPet.Core;

/// <summary>服务器上比当前版本新的一个版本，以及属于本机平台的下载文件。</summary>
public sealed record UpdateInfo(string Version, string Channel, string Notes, string DownloadUrl, string PageUrl, long Size);

/// <summary>一次检查的结果，给控制台显示。</summary>
public enum UpdateState
{
    /// <summary>还没检查过。</summary>
    Unknown,
    Checking,
    UpToDate,
    Available,
    /// <summary>没配更新服务器，或者网络、服务器出错。</summary>
    Failed,
}

/// <summary>
/// 更新检测：向 LocalGate 站点问「这个渠道、这个平台的最新版本」，比当前版本新就记下来。
/// 只检测和提醒，不自动替换程序：下载链接给用户，由浏览器下载。
/// 站点接口：GET {服务器}/api/pet-releases/{stable|preview}/latest.json?platform={darwin-arm64|…}。
/// </summary>
public sealed class UpdateChecker : Observable
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly PetSettings _settings;
    private UpdateState _state;
    private string _message = "";
    private UpdateInfo? _available;
    private DateTime? _checkedAt;

    public UpdateChecker(PetSettings settings) => _settings = settings;

    public UpdateState State { get => _state; private set => Set(ref _state, value); }
    /// <summary>给人看的一句话：「已是最新版本」「发现新版本 0.2.0」「检查失败：…」。</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }
    /// <summary>有新版本时不为空。</summary>
    public UpdateInfo? Available { get => _available; private set { if (Set(ref _available, value)) Raise(nameof(HasUpdate)); } }
    public bool HasUpdate => Available is not null;
    public DateTime? CheckedAt { get => _checkedAt; private set => Set(ref _checkedAt, value); }

    /// <summary>实际使用的服务器：设置里填了就用设置的，否则用构建时写进去的。空表示没配。</summary>
    public string Server => (string.IsNullOrWhiteSpace(_settings.UpdateServer) ? AppVersion.DefaultServer : _settings.UpdateServer)
        .Trim().TrimEnd('/');

    /// <summary>
    /// 检查一次。出错不抛异常，结果写进 <see cref="State"/> 和 <see cref="Message"/>；
    /// 返回值是这次发现的新版本（没有新版本或失败时为 null）。
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (State == UpdateState.Checking) return null;
        var server = Server;
        if (server.Length == 0)
        {
            Fail("还没有配置更新服务器");
            return null;
        }
        if (!Uri.TryCreate(server, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http"))
        {
            Fail("更新服务器地址不对，需要 http:// 或 https:// 开头");
            return null;
        }

        State = UpdateState.Checking;
        Message = "正在检查更新…";
        try
        {
            var channel = _settings.UpdateChannel == "preview" ? "preview" : "stable";
            var url = new Uri(baseUri, $"/api/pet-releases/{channel}/latest.json?platform={Uri.EscapeDataString(AppVersion.Platform)}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await Http.GetAsync(url, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 服务器上这个渠道还没有发布过版本：不算错误。
                MarkUpToDate();
                return null;
            }
            response.EnsureSuccessStatusCode();
            var feed = await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), UpdateJsonContext.Default.LatestFeed, timeout.Token);

            if (feed is null || !SemVer.TryParse(feed.Version, out _))
            {
                Fail("服务器返回的版本信息无法识别");
                return null;
            }
            // 优先本机平台的压缩包；没有就退到下载页。
            var file = feed.Files?.FirstOrDefault(f => f.Platform == AppVersion.Platform && f.Kind == "zip");
            if (SemVer.Compare(feed.Version, AppVersion.Current) <= 0)
            {
                MarkUpToDate();
                return null;
            }

            var info = new UpdateInfo(
                feed.Version!,
                feed.Channel ?? channel,
                feed.Notes ?? "",
                file?.Url is { Length: > 0 } rel ? new Uri(baseUri, rel).ToString() : "",
                new Uri(baseUri, string.IsNullOrEmpty(feed.Page) ? "/pet" : feed.Page).ToString(),
                file?.Size ?? 0);
            Available = info;
            CheckedAt = DateTime.Now;
            State = UpdateState.Available;
            Message = $"发现新版本 {info.Version}（当前 {AppVersion.Current}）";
            PetLog.Write(Message);
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            State = UpdateState.Unknown;
            Message = "";
            return null;
        }
        catch (Exception e)
        {
            Fail($"检查失败：{(e is OperationCanceledException ? "连接超时" : e.Message)}");
            return null;
        }
    }

    private void MarkUpToDate()
    {
        Available = null;
        CheckedAt = DateTime.Now;
        State = UpdateState.UpToDate;
        Message = $"已是最新版本（{AppVersion.Current}）";
    }

    private void Fail(string message)
    {
        State = UpdateState.Failed;
        Message = message;
        CheckedAt = DateTime.Now;
        PetLog.Write("更新检测：" + message);
    }

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
