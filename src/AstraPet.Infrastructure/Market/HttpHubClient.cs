using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AstraPet.Core;
using AstraPet.Core.Market;

namespace AstraPet.Infrastructure.Market;

/// <summary>
/// 宠物市场（Astra Hub）的 HTTP 客户端。所有 JSON 响应都是信封：{ success, code, message, data, traceId }；
/// success:false 时把 code / message（已经是给用户看的中文）原样抛成 <see cref="HubException"/>，401 抛 <see cref="HubUnauthorizedException"/>。
/// 全程序共用一个 HttpClient（User-Agent: AstraPet/版本）；JSON 全用源生成，NativeAOT 下不走反射。令牌不写日志。
/// </summary>
public sealed class HttpHubClient : IHubClient
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private const long MaxImageBytes = 8L * 1024 * 1024;

    private static readonly Lazy<HttpClient> SharedHttp = new(() => CreateHttp(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    }));

    private readonly HttpClient _http;
    private readonly Uri _base;

    /// <param name="baseUri">Hub 的根地址，生产环境是 <see cref="MarketHub.BaseUri"/>。</param>
    /// <param name="handler">测试用：换成假的 HttpMessageHandler，不走网络。</param>
    public HttpHubClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        _base = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        _http = handler is null ? SharedHttp.Value : CreateHttp(handler);
    }

    private static HttpClient CreateHttp(HttpMessageHandler handler)
    {
        // 下载可能很久，整体不设超时；API 请求各自用 ApiTimeout。
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AstraPet", AppVersion.Current));
        return http;
    }

    // ───── 登录 ─────

    public async Task<HubDeviceCode> RequestDeviceCodeAsync(string clientName, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "api/device/code", null,
            JsonContent.Create(new DeviceCodeRequest { ClientName = clientName }, HubJsonContext.Default.DeviceCodeRequest), ct);
        var dto = Read(data, HubJsonContext.Default.DeviceCodeDto);
        if (dto.DeviceCode is not { Length: > 0 } device || dto.UserCode is not { Length: > 0 } user)
            throw BadResponse();
        return new HubDeviceCode(device, user, dto.VerificationUri ?? "", dto.VerificationUriComplete ?? dto.VerificationUri ?? "",
            dto.ExpiresIn > 0 ? dto.ExpiresIn : 600, dto.Interval > 0 ? dto.Interval : 5);
    }

    public async Task<HubToken> PollTokenAsync(string deviceCode, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "api/device/token", null,
            JsonContent.Create(new DeviceTokenRequest { DeviceCode = deviceCode }, HubJsonContext.Default.DeviceTokenRequest), ct);
        var dto = Read(data, HubJsonContext.Default.TokenDto);
        if (dto.AccessToken is not { Length: > 0 } token) throw BadResponse();
        return new HubToken(token, dto.TokenType ?? "Bearer", dto.Scope ?? "", dto.ExpiresIn);
    }

    public async Task<HubUser> GetMeAsync(string token, CancellationToken ct)
    {
        var dto = Read(await SendAsync(HttpMethod.Get, "api/client/me", token, null, ct), HubJsonContext.Default.MeDto);
        return new HubUser(dto.Id ?? "", dto.Nickname is { Length: > 0 } n ? n : "已登录用户", dto.AvatarUrl,
            dto.Email is { Length: > 0 } email ? email : null);
    }

    public async Task LogoutAsync(string token, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "api/client/logout", token, null, ct);

    // ───── 列表 ─────

    public async Task<MarketPage> ListPetsAsync(string token, string? query, MarketSort sort, string? cursor, int limit,
        CancellationToken ct)
    {
        var path = "api/client/pets?q=" + Uri.EscapeDataString(query ?? "")
                   + "&sort=" + sort.ToString().ToLowerInvariant()
                   + "&cursor=" + Uri.EscapeDataString(cursor ?? "")
                   + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        var dto = Read(await SendAsync(HttpMethod.Get, path, token, null, ct), HubJsonContext.Default.PetsPageDto);
        var items = (dto.Items ?? []).Where(p => p.PackId is { Length: > 0 }).Select(Map).ToList();
        return new MarketPage(items, dto.NextCursor is { Length: > 0 } next ? next : null);
    }

    private static MarketPet Map(PetDto p) => new(
        p.Id ?? p.PackId!, p.PackId!, p.Title is { Length: > 0 } t ? t : p.PackId!, p.Summary ?? "", p.AuthorName ?? "",
        Math.Max(0, p.Price), p.CoverUrl, p.IconUrl, p.Rating, p.RatingCount, p.DownloadCount, p.Version, p.FileSize,
        p.Sha256, p.MinAppVersion, p.Owned, p.IsAuthor, p.CanDownload, p.NeedPurchase, p.ReasonCode, p.ReasonMessage,
        p.PageUrl ?? "", p.DownloadUrl);

    // ───── 下载 ─────

    public async Task<HubDownload> DownloadAsync(string token, Uri url, Stream destination, long maxBytes,
        IProgress<double>? progress, CancellationToken ct)
    {
        // 令牌只发给 Hub 自己：下载地址换了主机就不带令牌去请求。
        if (!SameOrigin(url)) throw new HubException("download.bad_url", "下载地址不在宠物市场上，已拒绝");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || IsJson(response))
        {
            // 出错时是 JSON 信封（download.purchase_required 之类）。
            await ReadEnvelopeAsync(response, ct);
            throw BadResponse((int)response.StatusCode);
        }

        var total = response.Content.Headers.ContentLength;
        if (total > maxBytes) throw TooLarge(maxBytes);
        long done = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                done += n;
                if (done > maxBytes) throw TooLarge(maxBytes);
                await destination.WriteAsync(buffer.AsMemory(0, n), ct);
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total.Value));
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException && e is not FileNotFoundException)
        {
            throw new HubException("net.interrupted", "下载中断了，请重试", 0, e);
        }
        if (total is > 0 && done != total) throw new HubException("net.interrupted", $"下载不完整（{done}/{total} 字节），请重试");
        await destination.FlushAsync(ct);
        return new HubDownload(done, Header(response, "X-Pet-Version"), Header(response, "X-Pet-Sha256"));
    }

    public async Task<byte[]?> GetImageAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme is not ("https" or "http")) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return null;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int n;
            while ((n = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + n > MaxImageBytes) return null;
                memory.Write(buffer, 0, n);
            }
            return memory.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // ───── 公共 ─────

    private bool SameOrigin(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == _base.Scheme && url.Port == _base.Port
        && string.Equals(url.Host, _base.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>发一次 API 请求，解开信封返回 data（可能是 Null）。</summary>
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string? token, HttpContent? content,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            return await ReadEnvelopeAsync(response, timeout.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new HubException("net.timeout", "连接宠物市场超时，请稍后再试", 0, e);
        }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        try { return await _http.SendAsync(request, option, ct); }
        catch (HttpRequestException e)
        {
            throw new HubException("net.unreachable", "连不上宠物市场，请检查网络", 0, e);
        }
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new HubUnauthorizedException();
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (HttpRequestException e) { throw new HubException("net.interrupted", "和宠物市场的连接中断了，请重试", status, e); }
        return ParseEnvelope(status, body);
    }

    /// <summary>
    /// 解信封：success:true 返回 data；false 抛 HubException(code, message)。
    /// 不是 JSON（网关的 502 页面之类）按 HTTP 状态报错。单独拿出来方便测。
    /// </summary>
    internal static JsonElement ParseEnvelope(int status, string body)
    {
        if (status == 401) throw new HubUnauthorizedException();
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize(body, HubJsonContext.Default.Envelope); }
        catch (JsonException) { envelope = null; }
        if (envelope is null) throw BadResponse(status);
        if (envelope.Success && status is >= 200 and < 300) return envelope.Data;
        var code = envelope.Code is { Length: > 0 } c ? c : "server.error";
        var message = envelope.Message is { Length: > 0 } m ? m : $"宠物市场出错了（{code}，HTTP {status}）";
        throw new HubException(code, message, status);
    }

    private static T Read<T>(JsonElement data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (data.ValueKind != JsonValueKind.Object) throw BadResponse();
        try { return data.Deserialize(type) ?? throw BadResponse(); }
        catch (JsonException e) { throw new HubException("net.bad_response", "宠物市场返回的数据无法识别", 0, e); }
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } type
        && type.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } v ? v.Trim() : null;

    private static HubException BadResponse(int status = 0) =>
        new("net.bad_response", status > 0 ? $"宠物市场返回了无法识别的内容（HTTP {status}）" : "宠物市场返回的数据无法识别", status);

    private static HubException TooLarge(long max) =>
        new("download.too_large", $"角色包太大（上限 {max / 1024 / 1024} MB），已停止下载");
}

// ───── 线上的 JSON（camelCase）。只取用得到的字段，都可空：服务器少给一个字段不至于整个解析失败。 ─────

internal sealed class Envelope
{
    public bool Success { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public JsonElement Data { get; set; }
    public string? TraceId { get; set; }
}

internal sealed class DeviceCodeRequest { public string ClientName { get; set; } = ""; }

internal sealed class DeviceTokenRequest { public string DeviceCode { get; set; } = ""; }

internal sealed class DeviceCodeDto
{
    public string? DeviceCode { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public string? VerificationUriComplete { get; set; }
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

internal sealed class TokenDto
{
    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public string? Scope { get; set; }
    public int ExpiresIn { get; set; }
}

internal sealed class MeDto
{
    public string? Id { get; set; }
    public string? Nickname { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Email { get; set; }
}

internal sealed class PetsPageDto
{
    public List<PetDto>? Items { get; set; }
    public string? NextCursor { get; set; }
}

internal sealed class PetDto
{
    public string? Id { get; set; }
    public string? PackId { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? AuthorName { get; set; }
    public long Price { get; set; }
    public string? CoverUrl { get; set; }
    public string? IconUrl { get; set; }
    public double? Rating { get; set; }
    public int RatingCount { get; set; }
    public long DownloadCount { get; set; }
    public string? Version { get; set; }
    public long? FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string? MinAppVersion { get; set; }
    public bool Owned { get; set; }
    public bool IsAuthor { get; set; }
    public bool CanDownload { get; set; }
    public bool NeedPurchase { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonMessage { get; set; }
    public string? PageUrl { get; set; }
    public string? DownloadUrl { get; set; }
}

/// <summary>源生成的 JSON 元数据，NativeAOT 下不能用反射式序列化。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(DeviceTokenRequest))]
[JsonSerializable(typeof(DeviceCodeDto))]
[JsonSerializable(typeof(TokenDto))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(PetsPageDto))]
internal sealed partial class HubJsonContext : JsonSerializerContext;
