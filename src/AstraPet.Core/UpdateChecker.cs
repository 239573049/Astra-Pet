namespace AstraPet.Core;

/// <summary>服务器上比当前版本新的一个版本，以及属于本机平台的下载文件。</summary>
public sealed record UpdateInfo(string Version, string Channel, string Notes, string DownloadUrl, string PageUrl, long Size, string Kind = "");

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
/// 只负责判断；HTTP 请求由 <see cref="IUpdateFeed"/> 做，下载见 UpdateDownloader，安装见 IUpdateInstaller。
/// 站点接口：GET {服务器}/api/pet-releases/{stable|preview}/latest.json?platform={darwin-arm64|…}。
/// </summary>
public sealed class UpdateChecker : Observable
{
    private readonly PetSettings _settings;
    private readonly IUpdateFeed _feed;
    private readonly string _current;
    private readonly string _platform;
    private readonly IReadOnlyList<string> _installerKinds;
    private UpdateState _state;
    private string _message = "";
    private UpdateInfo? _available;
    private DateTime? _checkedAt;

    /// <param name="settings">渠道、服务器地址。</param>
    /// <param name="platform">本机平台标识（darwin-arm64 等），和站点上文件的 platform 字段一致。</param>
    /// <param name="installerKinds">本机能用的安装包类型，按优先级（dmg、exe、deb、zip…）。</param>
    /// <param name="currentVersion">当前版本，默认是构建时写进去的；测试时传固定值。</param>
    public UpdateChecker(PetSettings settings, IUpdateFeed feed, string platform, IReadOnlyList<string> installerKinds,
        string? currentVersion = null)
    {
        _settings = settings;
        _feed = feed;
        _current = currentVersion ?? AppVersion.Current;
        _platform = platform;
        _installerKinds = installerKinds;
    }

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
            var url = new Uri(baseUri, $"/api/pet-releases/{channel}/latest.json?platform={Uri.EscapeDataString(_platform)}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var feed = await _feed.GetLatestAsync(url, timeout.Token);
            if (feed is null)
            {
                // 服务器上这个渠道还没有发布过版本：不算错误。
                MarkUpToDate();
                return null;
            }
            if (!SemVer.TryParse(feed.Version, out _))
            {
                Fail("服务器返回的版本信息无法识别");
                return null;
            }
            // 本机平台的安装包（macOS dmg、Windows exe、Linux deb）；0.1.1 及以前只发过 zip，也认；都没有就退到下载页。
            var file = PickInstaller(feed.Files);
            if (SemVer.Compare(feed.Version, _current) <= 0)
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
                file?.Size ?? 0,
                file?.Kind ?? "");
            Available = info;
            CheckedAt = DateTime.Now;
            State = UpdateState.Available;
            Message = $"发现新版本 {info.Version}（当前 {_current}）";
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

    private ReleaseFile? PickInstaller(IReadOnlyList<ReleaseFile> files)
    {
        var mine = files.Where(f => f.Platform == _platform).ToList();
        foreach (var kind in _installerKinds)
            if (mine.FirstOrDefault(f => f.Kind == kind) is { } found) return found;
        return null;
    }

    private void MarkUpToDate()
    {
        Available = null;
        CheckedAt = DateTime.Now;
        State = UpdateState.UpToDate;
        Message = $"已是最新版本（{_current}）";
    }

    private void Fail(string message)
    {
        State = UpdateState.Failed;
        Message = message;
        CheckedAt = DateTime.Now;
        PetLog.Write("更新检测：" + message);
    }
}
