namespace AstraPet.Core;

/// <summary>站点上某个渠道的最新版本（latest.json）。只取用得到的字段。</summary>
public sealed record ReleaseFeed(string? Version, string? Channel, string? Notes, string? Page, IReadOnlyList<ReleaseFile> Files);

/// <summary>一个安装包。<see cref="Url"/> 可能是相对站点根的路径。</summary>
public sealed record ReleaseFile(string? Platform, string? Kind, string? Url, long Size);

/// <summary>
/// 取发布信息。实现在 Infrastructure（HTTP GET + JSON）；测试里用假的。
/// 这个渠道还没发布过版本（404）时返回 null；网络错误、服务器错误、返回的不是 JSON 时抛异常。
/// </summary>
public interface IUpdateFeed
{
    Task<ReleaseFeed?> GetLatestAsync(Uri url, CancellationToken ct);
}
