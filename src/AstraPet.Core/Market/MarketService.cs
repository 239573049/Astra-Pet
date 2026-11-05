using System.Security.Cryptography;

namespace AstraPet.Core.Market;

/// <summary>本机有没有装这只宠物，装的是不是市场上的这一版（按 SHA-256 比）。</summary>
public enum MarketInstallState { NotInstalled, Installed, Outdated }

/// <summary>
/// 宠物市场的业务逻辑，不碰界面：设备码登录（轮询、slow_down、过期）、登录状态的保存和清除、列表、下载安装。
/// HTTP 由 <see cref="IHubClient"/> 做；任何一次带令牌的请求遇到 401 都会清掉本地令牌再把
/// <see cref="HubUnauthorizedException"/> 抛给调用方，调用方据此回到未登录状态。
/// 安装：先下到角色目录里的隐藏临时文件（同一个卷，最后一步改名是原子的），校验大小和 SHA-256，
/// 用 <see cref="PetPack.Inspect"/> 检查（含格式版本），通过后改名成 {packId}.astrapet，覆盖旧版本。
/// </summary>
public sealed class MarketService
{
    /// <summary>角色包下载的上限，和 PetPack 解压后的上限一致。</summary>
    public const long MaxPackBytes = PetPack.MaxExtractedBytes;

    private readonly IHubClient _client;
    private readonly HubSessionStore _store;
    private readonly PetCatalog _catalog;
    private readonly string _clientName;
    private readonly string _appVersion;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, (long Length, DateTime Modified, string Sha)> _hashes = new(StringComparer.Ordinal);

    /// <param name="clientName">登录时报给服务器的设备名，比如「Astra Pet 0.1.4 · macOS」，用户在网页的设备列表里看得到。</param>
    /// <param name="delay">轮询的等待，测试里换成不真等的。</param>
    /// <param name="appVersion">当前版本（判断 minAppVersion），默认是构建时写进去的。</param>
    public MarketService(IHubClient client, HubSessionStore store, PetCatalog catalog, string clientName,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string? appVersion = null)
    {
        _client = client;
        _store = store;
        _catalog = catalog;
        _clientName = clientName;
        _appVersion = appVersion ?? AppVersion.Current;
        _delay = delay ?? Task.Delay;
        Session = store.Load();
    }

    /// <summary>当前登录状态，null 是未登录。</summary>
    public HubSession? Session { get; private set; }

    public bool IsLoggedIn => Session is not null;

    // ───── 登录 ─────

    /// <summary>第一步：要一个设备码。拿到后打开 VerificationUriComplete，界面上显示 UserCode。</summary>
    public Task<HubDeviceCode> StartLoginAsync(CancellationToken ct) => _client.RequestDeviceCodeAsync(_clientName, ct);

    /// <summary>第二步：轮询到用户在网页上批准，取昵称，存下登录状态。用户拒绝、码过期时抛 HubException（Message 给人看）。</summary>
    public async Task<HubSession> CompleteLoginAsync(HubDeviceCode code, CancellationToken ct)
    {
        var token = await WaitForApprovalAsync(code, ct);
        var me = await _client.GetMeAsync(token.AccessToken, ct);
        var session = new HubSession(token.AccessToken, me.Nickname, me.Id, DateTimeOffset.Now, me.Email, me.AvatarUrl);
        _store.Save(session);
        Session = session;
        PetLog.Write($"已登录宠物市场: {me.Nickname}");
        return session;
    }

    /// <summary>
    /// 按 interval 轮询 /api/device/token：authorization_pending 接着等；slow_down 把间隔加 5 秒；网络抖动接着等；
    /// 其他错误（expired_token、access_denied、invalid_code…）停下来抛出去。累计等满 expiresIn 也停。
    /// </summary>
    internal async Task<HubToken> WaitForApprovalAsync(HubDeviceCode code, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        var remaining = TimeSpan.FromSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 600);
        while (true)
        {
            if (remaining <= TimeSpan.Zero) throw new HubException("device.expired_token", "登录码已过期，请重新登录");
            await _delay(interval, ct);
            remaining -= interval;
            try
            {
                return await _client.PollTokenAsync(code.DeviceCode, ct);
            }
            catch (HubException e) when (e.Code == "device.authorization_pending") { }
            catch (HubException e) when (e.Code == "device.slow_down") { interval += TimeSpan.FromSeconds(5); }
            catch (HubException e) when (e.Code.StartsWith("net.", StringComparison.Ordinal))
            {
                PetLog.Write($"市场登录轮询出错，继续等待: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 重新取一次昵称、邮箱和头像（用户可能在网页上改过，旧的登录文件里也没有邮箱头像）。有变化就存下来并返回 true。
    /// 401 时登录状态被清掉、抛 <see cref="HubUnauthorizedException"/>；其他错误原样抛出，调用方忽略即可。
    /// </summary>
    public async Task<bool> RefreshProfileAsync(CancellationToken ct)
    {
        if (Session is not { } before) return false;
        var me = await Authed(token => _client.GetMeAsync(token, ct));
        if (Session is not { } current || current.AccessToken != before.AccessToken) return false;   // 期间退出或换了账号
        var updated = current with { Nickname = me.Nickname, Email = me.Email, AvatarUrl = me.AvatarUrl };
        if (updated == current) return false;
        _store.Save(updated);
        Session = updated;
        return true;
    }

    /// <summary>退出：先让服务器吊销令牌（失败也无所谓），再删本地文件。</summary>
    public async Task LogoutAsync()
    {
        if (Session is { } session)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _client.LogoutAsync(session.AccessToken, timeout.Token);
            }
            catch (Exception e) when (e is HubException or OperationCanceledException)
            {
                PetLog.Write($"市场退出登录时服务器没响应，只删本地令牌: {e.Message}");
            }
        }
        ClearSession();
        PetLog.Write("已退出宠物市场");
    }

    private void ClearSession()
    {
        Session = null;
        _store.Delete();
    }

    private string RequireToken() =>
        Session?.AccessToken ?? throw new HubUnauthorizedException("还没有登录");

    /// <summary>带令牌的请求：401 时清掉登录状态再抛。</summary>
    private async Task<T> Authed<T>(Func<string, Task<T>> call)
    {
        try { return await call(RequireToken()); }
        catch (HubUnauthorizedException)
        {
            ClearSession();
            throw;
        }
    }

    // ───── 列表 ─────

    public Task<MarketPage> ListAsync(string? query, MarketSort sort, string? cursor, CancellationToken ct, int limit = 24) =>
        Authed(token => _client.ListPetsAsync(token, string.IsNullOrWhiteSpace(query) ? null : query.Trim(), sort, cursor, limit, ct));

    /// <summary>需要更新版的程序才能用时，返回给人看的原因；否则 null。</summary>
    public string? VersionProblem(MarketPet pet) =>
        pet.MinAppVersion is { Length: > 0 } min && SemVer.TryParse(min, out _) && SemVer.Compare(min, _appVersion) > 0
            ? $"需要 Astra Pet {min} 或更新的版本"
            : null;

    /// <summary>装好后的文件：角色目录下的 {packId}.astrapet。它在角色列表里的 id 就是这个文件名。</summary>
    public string InstalledPath(MarketPet pet) => Path.Combine(_catalog.UserDirectory, pet.PackId + PetPack.Extension);

    /// <summary>
    /// 本机装的和市场上的比：两边版本号都读得出来时，只有市场版本更新才算「有更新」（自己导出的、比市场还新的不算）；
    /// 任何一边没有版本号时才退回比 SHA-256（会读整个文件算哈希，按大小和修改时间缓存）。放后台线程上调。
    /// </summary>
    public MarketInstallState InstallStateOf(MarketPet pet)
    {
        if (!IsSafeId(pet.PackId)) return MarketInstallState.NotInstalled;
        var path = InstalledPath(pet);
        if (!File.Exists(path)) return MarketInstallState.NotInstalled;
        if (pet.Version is { Length: > 0 } market && PetPack.ReadVersion(path) is { } local
            && SemVer.TryParse(market, out _) && SemVer.TryParse(local, out _))
            return SemVer.Compare(market, local) > 0 ? MarketInstallState.Outdated : MarketInstallState.Installed;
        if (pet.Sha256 is not { Length: > 0 } expected) return MarketInstallState.Installed;
        try
        {
            return string.Equals(HashOf(path), expected, StringComparison.OrdinalIgnoreCase)
                ? MarketInstallState.Installed
                : MarketInstallState.Outdated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return MarketInstallState.Outdated;
        }
    }

    // ───── 安装 ─────

    /// <summary>
    /// 下载、校验、检查、装进角色目录，返回装好的文件路径。任何一步不对都抛 HubException（Message 给人看），
    /// 临时文件会删掉，原来装着的版本不受影响。
    /// </summary>
    public async Task<string> InstallAsync(MarketPet pet, IProgress<double>? progress, CancellationToken ct)
    {
        if (!IsSafeId(pet.PackId))
            throw new HubException("install.bad_id", $"这个角色的 id 不合法：「{pet.PackId}」");
        if (pet.NeedPurchase || !pet.CanDownload || pet.DownloadUrl is not { Length: > 0 } link)
            throw new HubException("download.purchase_required", pet.ReasonMessage ?? "需要先在网页上购买");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            throw new HubException("install.bad_url", "下载地址不对");
        if (VersionProblem(pet) is { } tooOld) throw new HubException("install.app_too_old", tooOld);
        var builtIn = _catalog.BuiltInPackIds();
        if (builtIn.Contains(pet.PackId))
            throw new HubException("install.builtin", $"「{pet.Title}」和内置角色同名，不能安装");

        Directory.CreateDirectory(_catalog.UserDirectory);
        // 点开头：角色目录扫描只认 *.astrapet，不会把下到一半的文件当成角色。
        var part = Path.Combine(_catalog.UserDirectory, $".{pet.PackId}-{Guid.NewGuid():N}.part");
        try
        {
            HubDownload download;
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                download = await Authed(token => _client.DownloadAsync(token, url, file, MaxPackBytes, progress, ct));

            Verify(pet, part, download);

            var (manifest, problem) = PetPack.Inspect(part);
            if (problem is not null || manifest is null)
                throw new HubException("install.invalid_pack", $"「{pet.Title}」的角色包不能用：{problem ?? "读不出清单"}");
            if (builtIn.Contains(manifest.Id))
                throw new HubException("install.builtin", $"「{pet.Title}」和内置角色同名（{manifest.Id}），不能安装");

            var target = InstalledPath(pet);
            File.Move(part, target, overwrite: true);
            PetLog.Write($"已从市场安装角色: {pet.Title} → {target}" + (download.Version is { } v ? $"（{v}）" : ""));
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HubException("install.io", $"保存角色包失败：{e.Message}", 0, e);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 哈希以下载响应头 X-Pet-Sha256 为准（列表拿到之后作者可能刚发了新版），没有就用列表里的；
    /// 大小只在下的就是列表上那一版时和 fileSize 比。两样都没有（服务器没给）就只记一笔日志。
    /// </summary>
    private void Verify(MarketPet pet, string part, HubDownload download)
    {
        var expected = download.Sha256 is { Length: > 0 } header ? header : pet.Sha256;
        var sameAsListed = download.Sha256 is not { Length: > 0 } h || string.Equals(h, pet.Sha256, StringComparison.OrdinalIgnoreCase);
        if (sameAsListed && pet.FileSize is > 0 and var size && size != download.Bytes)
            throw new HubException("install.size_mismatch", $"下载的文件大小不对（{download.Bytes}/{size} 字节），已丢弃，请重试");
        if (expected is not { Length: > 0 })
        {
            PetLog.Write($"市场没有给「{pet.Title}」的 SHA-256，跳过哈希校验");
            return;
        }
        var actual = ComputeSha(part);
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HubException("install.sha_mismatch", "下载的文件校验不通过（SHA-256 不一致），已丢弃，请重试");
    }

    private string HashOf(string path)
    {
        var info = new FileInfo(path);
        lock (_hashes)
            if (_hashes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached.Sha;
        var sha = ComputeSha(path);
        lock (_hashes) _hashes[path] = (info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    private static string ComputeSha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>packId 要拿来当文件名：和 pet.json 的 id 同一套规则（小写字母、数字、短横线）。</summary>
    internal static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
