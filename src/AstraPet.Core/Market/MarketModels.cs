using System.Globalization;

namespace AstraPet.Core.Market;

/// <summary>
/// 宠物市场（Astra Hub）的地址。构建时写死（Directory.Build.props 的 HubBaseUrl，经 GenerateBuildInfo 进 BuildInfo），
/// 界面上不显示、设置里不能改、也没有环境变量覆盖。开发时连本地的 Hub：dotnet build -p:HubBaseUrl=http://localhost:5000。
/// </summary>
public static class MarketHub
{
    public static Uri BaseUri { get; } = new(BuildInfo.HubBaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
}

/// <summary>POST /api/device/code 的结果：拿 <see cref="DeviceCode"/> 轮询，<see cref="UserCode"/> 给用户核对。</summary>
public sealed record HubDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

/// <summary>登录成功拿到的访问令牌。</summary>
public sealed record HubToken(string AccessToken, string TokenType, string Scope, int ExpiresIn);

/// <summary>GET /api/client/me。</summary>
public sealed record HubUser(string Id, string Nickname, string? AvatarUrl);

/// <summary>本机保存的登录状态（hub-session.json）。</summary>
public sealed record HubSession(string AccessToken, string Nickname, string UserId, DateTimeOffset LoggedInAt);

/// <summary>市场列表里的一只宠物（ClientMarketPet）。价格单位是分（人民币），0 是免费。</summary>
public sealed record MarketPet(
    string Id,
    string PackId,
    string Title,
    string Summary,
    string AuthorName,
    long Price,
    string? CoverUrl,
    string? IconUrl,
    double? Rating,
    int RatingCount,
    long DownloadCount,
    string? Version,
    long? FileSize,
    string? Sha256,
    string? MinAppVersion,
    bool Owned,
    bool IsAuthor,
    bool CanDownload,
    bool NeedPurchase,
    string? ReasonCode,
    string? ReasonMessage,
    string PageUrl,
    string? DownloadUrl)
{
    /// <summary>列表上的价格：已拥有（含自己是作者）/ 免费 / ¥x.xx。</summary>
    public string PriceText => Owned || IsAuthor ? "已拥有" : MarketPrice.Format(Price);
}

/// <summary>一页列表。<see cref="NextCursor"/> 不为空时还有下一页。</summary>
public sealed record MarketPage(IReadOnlyList<MarketPet> Items, string? NextCursor);

/// <summary>排序方式，对应 sort 参数。</summary>
public enum MarketSort { Hot, New, Downloads, Rating }

/// <summary>下载接口返回的附加信息（响应头 X-Pet-Version、X-Pet-Sha256）。</summary>
public sealed record HubDownload(long Bytes, string? Version, string? Sha256);

public static class MarketPrice
{
    /// <summary>分 → 「免费」或「¥9.90」。</summary>
    public static string Format(long cents) => cents <= 0
        ? "免费"
        : "¥" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// Hub 返回 success:false，或者请求没成功。<see cref="Code"/> 是服务器的错误码（device.slow_down 之类），
/// 本地的问题用 net.* / install.*；<see cref="Exception.Message"/> 已经是给用户看的中文。
/// </summary>
public class HubException(string code, string message, int status = 0, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;

    /// <summary>HTTP 状态码，本地错误是 0。</summary>
    public int Status { get; } = status;
}

/// <summary>401：令牌缺失、被吊销或过期。收到后要清掉本地令牌、回到未登录。</summary>
public sealed class HubUnauthorizedException(string message = HubUnauthorizedException.DefaultMessage)
    : HubException("auth.unauthorized", message, 401)
{
    public const string DefaultMessage = "登录已过期，请重新登录";
}
