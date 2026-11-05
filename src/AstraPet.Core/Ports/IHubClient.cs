using AstraPet.Core.Market;

namespace AstraPet.Core;

/// <summary>
/// 宠物市场（Astra Hub）的 HTTP 接口。实现在 Infrastructure（HttpHubClient），测试里换假的 HttpMessageHandler。
/// 服务器返回 success:false 时抛 <see cref="HubException"/>（Code、Message 照搬服务器的），401 抛 <see cref="HubUnauthorizedException"/>，
/// 网络不通、返回的不是 JSON 也包成 HubException（net.*），调用方只需要接这一种异常。
/// 令牌只经过参数传进来，实现里不记日志。
/// </summary>
public interface IHubClient
{
    /// <summary>POST /api/device/code。</summary>
    Task<HubDeviceCode> RequestDeviceCodeAsync(string clientName, CancellationToken ct);

    /// <summary>POST /api/device/token 一次。还没批准时抛 Code = device.authorization_pending 的 HubException。</summary>
    Task<HubToken> PollTokenAsync(string deviceCode, CancellationToken ct);

    /// <summary>GET /api/client/me。</summary>
    Task<HubUser> GetMeAsync(string token, CancellationToken ct);

    /// <summary>POST /api/client/logout，吊销这个令牌。</summary>
    Task LogoutAsync(string token, CancellationToken ct);

    /// <summary>GET /api/client/pets。</summary>
    Task<MarketPage> ListPetsAsync(string token, string? query, MarketSort sort, string? cursor, int limit, CancellationToken ct);

    /// <summary>
    /// GET downloadUrl（带令牌），把角色包写进 <paramref name="destination"/>。超过 <paramref name="maxBytes"/> 就中止。
    /// progress 取 0~1，大小未知时不报告。
    /// </summary>
    Task<HubDownload> DownloadAsync(string token, Uri url, Stream destination, long maxBytes, IProgress<double>? progress,
        CancellationToken ct);

    /// <summary>取封面图的原始字节（不解码）。失败返回 null，不抛异常。</summary>
    Task<byte[]?> GetImageAsync(Uri url, CancellationToken ct);
}
