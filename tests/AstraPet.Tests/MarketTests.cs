using System.Net;
using System.Security.Cryptography;
using System.Text;
using AstraPet.Core;
using AstraPet.Core.Market;
using AstraPet.Infrastructure.Market;

namespace AstraPet.Tests;

/// <summary>
/// 假的 Astra Hub：按「方法 路径」排好要回的响应（同一个地址可以排好几次，依次取，取完了重复最后一个），记下收到的请求。
/// 不走网络。
/// </summary>
internal sealed class FakeHubHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _last = new();

    public List<(string Route, string? Auth, string? Body, Uri Uri)> Requests { get; } = [];

    public FakeHubHandler On(string route, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        if (!_routes.TryGetValue(route, out var queue)) _routes[route] = queue = new();
        queue.Enqueue(respond);
        return this;
    }

    public FakeHubHandler On(string route, HttpStatusCode status, string json) =>
        On(route, _ => Json(status, json));

    public FakeHubHandler Ok(string route, string dataJson) =>
        On(route, HttpStatusCode.OK, $$"""{"success":true,"code":"ok","message":null,"data":{{dataJson}},"traceId":"t"}""");

    public FakeHubHandler Error(string route, string code, string message, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        On(route, status, $$"""{"success":false,"code":"{{code}}","message":"{{message}}","data":null,"traceId":"t"}""");

    public int Count(string route) => Requests.Count(r => r.Route == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var route = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((route, request.Headers.Authorization?.ToString(), body, request.RequestUri));
        if (_routes.TryGetValue(route, out var queue) && queue.TryDequeue(out var respond)) _last[route] = respond;
        else if (!_last.TryGetValue(route, out respond)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        return respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class MarketTests : IDisposable
{
    private static readonly Uri Base = new("https://hub.test/");
    private const string Token = "secret-token-123";
    private const string DownloadUrl = "https://hub.test/api/client/pets/shiba/download";

    private readonly string _root = PackMaker.TempDir();
    private readonly FakeHubHandler _hub = new();
    private readonly List<TimeSpan> _delays = [];

    private string DataDir => Path.Combine(_root, "data");
    private string UserDir => Path.Combine(_root, "data", "characters");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MarketService CreateService(bool loggedIn = true, string appVersion = "0.1.4")
    {
        var builtIn = Path.Combine(_root, "builtin");
        PackMaker.Make(builtIn, "xiaot", "小t", isDefault: true);
        var catalog = new PetCatalog(builtIn, UserDir, Path.Combine(_root, "cache"));
        var store = new HubSessionStore(DataDir);
        if (loggedIn) store.Save(new HubSession(Token, "小明", "u1", DateTimeOffset.Now));
        return new MarketService(new HttpHubClient(Base, _hub), store, catalog, "Astra Pet 测试 · macOS",
            (d, _) => { _delays.Add(d); return Task.CompletedTask; }, appVersion);
    }

    private static HubDeviceCode Code(int interval = 5, int expiresIn = 600) =>
        new("dev-1", "ABCD-EFGH", "https://hub.test/device", "https://hub.test/device?code=ABCD-EFGH", expiresIn, interval);

    // ───── 信封 ─────

    [Fact]
    public void Envelope_success_returns_data_and_failure_throws_server_code_and_message()
    {
        var data = HttpHubClient.ParseEnvelope(200, """{"success":true,"code":"ok","message":null,"data":{"a":1},"traceId":"x"}""");
        Assert.Equal(1, data.GetProperty("a").GetInt32());

        var e = Assert.Throws<HubException>(() => HttpHubClient.ParseEnvelope(403,
            """{"success":false,"code":"download.purchase_required","message":"这只宠物需要先购买","data":null,"traceId":"x"}"""));
        Assert.Equal("download.purchase_required", e.Code);
        Assert.Equal("这只宠物需要先购买", e.Message);
        Assert.Equal(403, e.Status);
    }

    [Fact]
    public void Envelope_non_json_and_401_are_reported()
    {
        Assert.Equal("net.bad_response", Assert.Throws<HubException>(() => HttpHubClient.ParseEnvelope(502, "<html>Bad Gateway</html>")).Code);
        Assert.IsType<HubUnauthorizedException>(Assert.ThrowsAny<HubException>(() => HttpHubClient.ParseEnvelope(401, "")));
    }

    [Fact]
    public async Task Device_code_request_sends_client_name_and_parses_fields()
    {
        _hub.Ok("POST /api/device/code", """{"deviceCode":"d","userCode":"ABCD-EFGH","verificationUri":"https://hub.test/device","verificationUriComplete":"https://hub.test/device?code=ABCD-EFGH","expiresIn":600,"interval":5}""");
        var code = await CreateService(loggedIn: false).StartLoginAsync(default);

        Assert.Equal("ABCD-EFGH", code.UserCode);
        Assert.Equal("https://hub.test/device?code=ABCD-EFGH", code.VerificationUriComplete);
        Assert.Equal((600, 5), (code.ExpiresIn, code.Interval));
        Assert.Contains("\"clientName\":\"Astra Pet 测试 · macOS\"", Unescape(_hub.Requests[0].Body));
    }

    private static string Unescape(string? body) => System.Text.RegularExpressions.Regex.Unescape(body ?? "");

    // ───── 登录轮询 ─────

    [Fact]
    public async Task Pending_then_approved_logs_in_and_saves_session()
    {
        _hub.Error("POST /api/device/token", "device.authorization_pending", "等待用户在浏览器里确认")
            .Error("POST /api/device/token", "device.authorization_pending", "等待用户在浏览器里确认")
            .Ok("POST /api/device/token", $$"""{"accessToken":"{{Token}}","tokenType":"Bearer","scope":"market:read","expiresIn":7776000}""")
            .Ok("GET /api/client/me", """{"id":"6f1c1b8e-0000-0000-0000-000000000001","nickname":"小明","avatarUrl":null}""");
        var service = CreateService(loggedIn: false);

        var session = await service.CompleteLoginAsync(Code(), default);

        Assert.Equal(3, _hub.Count("POST /api/device/token"));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], _delays);
        Assert.Equal("小明", session.Nickname);
        Assert.True(service.IsLoggedIn);
        Assert.Equal($"Bearer {Token}", _hub.Requests.Single(r => r.Route == "GET /api/client/me").Auth);
        Assert.Equal(Token, new HubSessionStore(DataDir).Load()?.AccessToken);
    }

    [Fact]
    public async Task Slow_down_adds_five_seconds_to_the_interval()
    {
        _hub.Error("POST /api/device/token", "device.authorization_pending", "等待")
            .Error("POST /api/device/token", "device.slow_down", "轮询太频繁，请放慢")
            .Error("POST /api/device/token", "device.authorization_pending", "等待")
            .Ok("POST /api/device/token", """{"accessToken":"t","tokenType":"Bearer","scope":"market:read","expiresIn":1}""");

        await CreateService(loggedIn: false).WaitForApprovalAsync(Code(), default);

        Assert.Equal(new[] { 5, 5, 10, 10 }, _delays.Select(d => (int)d.TotalSeconds));
    }

    [Theory]
    [InlineData("device.access_denied", "用户拒绝了这次登录")]
    [InlineData("device.expired_token", "设备码已过期，请重新发起登录")]
    [InlineData("device.invalid_code", "设备码无效或已经使用过")]
    public async Task Denied_expired_or_invalid_stops_polling_with_server_message(string code, string message)
    {
        _hub.Error("POST /api/device/token", "device.authorization_pending", "等待")
            .Error("POST /api/device/token", code, message);
        var service = CreateService(loggedIn: false);

        var e = await Assert.ThrowsAsync<HubException>(() => service.CompleteLoginAsync(Code(), default));

        Assert.Equal(code, e.Code);
        Assert.Equal(message, e.Message);
        Assert.Equal(2, _hub.Count("POST /api/device/token"));
        Assert.False(service.IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(DataDir, HubSessionStore.FileName)));
    }

    [Fact]
    public async Task Polling_gives_up_when_the_code_runs_out_of_time()
    {
        _hub.Error("POST /api/device/token", "device.authorization_pending", "等待");

        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService(loggedIn: false).WaitForApprovalAsync(Code(interval: 5, expiresIn: 12), default));

        Assert.Equal("device.expired_token", e.Code);
        Assert.Equal(3, _hub.Count("POST /api/device/token"));   // 5、10、15 秒各一次，第三次后已经超过 12 秒
    }

    [Fact]
    public async Task Cancelling_login_stops_polling()
    {
        _hub.Error("POST /api/device/token", "device.authorization_pending", "等待");
        using var cts = new CancellationTokenSource();
        var builtIn = Path.Combine(_root, "builtin");
        PackMaker.Make(builtIn, "xiaot", "小t", isDefault: true);
        var polls = 0;
        var service = new MarketService(new HttpHubClient(Base, _hub), new HubSessionStore(DataDir),
            new PetCatalog(builtIn, UserDir, Path.Combine(_root, "cache")), "t",
            (_, ct) => { if (++polls == 3) cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteLoginAsync(Code(), cts.Token));
        Assert.Equal(2, _hub.Count("POST /api/device/token"));
    }

    // ───── 列表 ─────

    private const string PetsJson = """
        {"items":[
          {"id":"a1","packId":"shiba","title":"柴柴","summary":"一只柴犬","authorName":"阿明","price":990,
           "coverUrl":"https://hub.test/media/c.webp","iconUrl":null,"rating":4.5,"ratingCount":12,"downloadCount":3400,
           "version":"1.0.0","fileSize":1234,"sha256":"ab","minAppVersion":null,"owned":false,"isAuthor":false,
           "canDownload":false,"needPurchase":true,"reasonCode":"download.purchase_required","reasonMessage":"需要先购买",
           "pageUrl":"https://hub.test/pets/shiba","downloadUrl":null},
          {"id":"a2","packId":"neko","title":"小猫","summary":"","authorName":"小红","price":0,"coverUrl":null,"iconUrl":null,
           "rating":null,"ratingCount":0,"downloadCount":5,"version":null,"fileSize":null,"sha256":null,"minAppVersion":"9.0.0",
           "owned":false,"isAuthor":false,"canDownload":true,"needPurchase":false,"reasonCode":null,"reasonMessage":null,
           "pageUrl":"https://hub.test/pets/neko","downloadUrl":"https://hub.test/api/client/pets/neko/download"},
          {"id":"a3","packId":"tora","title":"老虎","summary":"","authorName":"我","price":1500,"owned":true,"isAuthor":false,
           "canDownload":true,"needPurchase":false,"pageUrl":"https://hub.test/pets/tora","downloadUrl":"https://hub.test/api/client/pets/tora/download"}
        ],"nextCursor":"c2"}
        """;

    [Fact]
    public async Task List_is_parsed_with_price_text_and_cursor()
    {
        _hub.Ok("GET /api/client/pets", PetsJson);
        var service = CreateService();

        var page = await service.ListAsync(" 柴 犬 ", MarketSort.New, "c1", default);

        Assert.Equal("c2", page.NextCursor);
        Assert.Equal(["¥9.90", "免费", "已拥有"], page.Items.Select(p => p.PriceText));
        var shiba = page.Items[0];
        Assert.True(shiba.NeedPurchase);
        Assert.Equal("https://hub.test/pets/shiba", shiba.PageUrl);
        Assert.Equal(3400, shiba.DownloadCount);
        Assert.Equal("需要 Astra Pet 9.0.0 或更新的版本", service.VersionProblem(page.Items[1]));
        Assert.Null(service.VersionProblem(shiba));

        var request = _hub.Requests.Single();
        Assert.Equal($"Bearer {Token}", request.Auth);
        var query = request.Uri.Query;
        Assert.Contains("sort=new", query);
        Assert.Contains("cursor=c1", query);
        Assert.Contains("limit=24", query);
        Assert.Contains("q=" + Uri.EscapeDataString("柴 犬"), query);
    }

    [Theory]
    [InlineData(0, "免费")]
    [InlineData(990, "¥9.90")]
    [InlineData(1, "¥0.01")]
    [InlineData(12345, "¥123.45")]
    public void Price_in_cents_is_formatted(long cents, string text) => Assert.Equal(text, MarketPrice.Format(cents));

    [Fact]
    public async Task Unauthorized_clears_the_token_file_and_session()
    {
        _hub.On("GET /api/client/pets", HttpStatusCode.Unauthorized,
            """{"success":false,"code":"auth.unauthorized","message":"请先登录","data":null,"traceId":"t"}""");
        var service = CreateService();
        Assert.True(File.Exists(Path.Combine(DataDir, HubSessionStore.FileName)));

        var e = await Assert.ThrowsAsync<HubUnauthorizedException>(() => service.ListAsync(null, MarketSort.Hot, null, default));

        Assert.Equal("登录已过期，请重新登录", e.Message);
        Assert.False(service.IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(DataDir, HubSessionStore.FileName)));
    }

    // ───── 登录状态文件 ─────

    [Fact]
    public async Task Session_file_is_owner_only_and_removed_on_logout()
    {
        var service = CreateService();
        var file = Path.Combine(DataDir, HubSessionStore.FileName);
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Contains(Token, File.ReadAllText(file));

        _hub.Ok("POST /api/client/logout", "null");
        await service.LogoutAsync();

        Assert.Equal($"Bearer {Token}", _hub.Requests.Single(r => r.Route == "POST /api/client/logout").Auth);
        Assert.False(File.Exists(file));
        Assert.False(service.IsLoggedIn);
        Assert.Null(new HubSessionStore(DataDir).Load());
    }

    [Fact]
    public async Task Logout_still_deletes_local_token_when_server_is_down()
    {
        _hub.On("POST /api/client/logout", _ => throw new HttpRequestException("connection refused"));
        var service = CreateService();

        await service.LogoutAsync();

        Assert.False(File.Exists(Path.Combine(DataDir, HubSessionStore.FileName)));
    }

    // ───── 安装 ─────

    private static MarketPet Pet(string packId, string? sha, long? size, string? downloadUrl = DownloadUrl) => new(
        "id-" + packId, packId, "柴柴", "", "阿明", 0, null, null, null, 0, 0, "1.0.0", size, sha, null,
        Owned: false, IsAuthor: false, CanDownload: downloadUrl is not null, NeedPurchase: false, null, null,
        "https://hub.test/pets/" + packId, downloadUrl);

    private void ServeFile(string route, byte[] bytes, string? shaHeader = null) => _hub.On(route, _ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/octet-stream");
        response.Headers.Add("X-Pet-Version", "1.0.0");
        if (shaHeader is not null) response.Headers.Add("X-Pet-Sha256", shaHeader);
        return response;
    });

    private byte[] ValidPack(string id)
    {
        var path = PackMaker.Make(Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")), id, "柴柴");
        return File.ReadAllBytes(path);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string[] UserFiles() => Directory.Exists(UserDir)
        ? Directory.EnumerateFileSystemEntries(UserDir).Select(f => Path.GetFileName(f)).ToArray()
        : [];

    [Fact]
    public async Task Install_lands_in_user_directory_as_pack_id_and_shows_up_in_catalog()
    {
        var bytes = ValidPack("shiba");
        ServeFile("GET /api/client/pets/shiba/download", bytes, Sha(bytes));
        var service = CreateService();
        var pet = Pet("shiba", Sha(bytes), bytes.Length);
        Assert.Equal(MarketInstallState.NotInstalled, service.InstallStateOf(pet));

        var reports = new List<double>();
        var path = await service.InstallAsync(pet, new SyncProgress(reports.Add), default);

        Assert.Equal(Path.Combine(UserDir, "shiba.astrapet"), path);
        Assert.Equal(["shiba.astrapet"], UserFiles());   // 临时文件不留
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal($"Bearer {Token}", _hub.Requests.Single().Auth);
        Assert.Equal(1.0, reports.Last());
        Assert.Equal(MarketInstallState.Installed, service.InstallStateOf(pet));
        Assert.Equal(MarketInstallState.Outdated, service.InstallStateOf(pet with { Sha256 = new string('0', 64) }));

        var entry = new PetCatalog(Path.Combine(_root, "builtin"), UserDir, Path.Combine(_root, "cache")).Scan()
            .Single(p => p.Id == "shiba.astrapet");
        Assert.Null(entry.Problem);
        Assert.Equal("柴柴", entry.Name);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.1", MarketInstallState.Installed)]   // 同版本：哈希不同也不提示更新
    [InlineData("1.0.2", "1.0.1", MarketInstallState.Outdated)]    // 市场更新
    [InlineData("1.0.0", "1.0.1", MarketInstallState.Installed)]   // 本机比市场还新（自己导出的）
    [InlineData("1.0.10", "1.0.9", MarketInstallState.Outdated)]   // 按数值比，不是字符串
    public void Update_is_offered_only_when_market_version_is_newer(string marketVersion, string localVersion, MarketInstallState expected)
    {
        Directory.CreateDirectory(UserDir);
        var path = Path.Combine(UserDir, "shiba.astrapet");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("pet.json").Open()))
            w.Write($$"""{ "format": 2, "id": "shiba", "version": "{{localVersion}}", "actions": [] }""");
        var service = CreateService();

        var pet = Pet("shiba", new string('f', 64), 1) with { Version = marketVersion };

        Assert.Equal(expected, service.InstallStateOf(pet));
    }

    [Fact]
    public async Task Install_replaces_an_older_version()
    {
        Directory.CreateDirectory(UserDir);
        await File.WriteAllTextAsync(Path.Combine(UserDir, "shiba.astrapet"), "old");
        var bytes = ValidPack("shiba");
        ServeFile("GET /api/client/pets/shiba/download", bytes);
        var service = CreateService();

        await service.InstallAsync(Pet("shiba", Sha(bytes), bytes.Length), null, default);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(UserDir, "shiba.astrapet")));
    }

    [Fact]
    public async Task Sha_mismatch_is_rejected_and_nothing_is_installed()
    {
        var bytes = ValidPack("shiba");
        ServeFile("GET /api/client/pets/shiba/download", bytes);
        var service = CreateService();

        var e = await Assert.ThrowsAsync<HubException>(() =>
            service.InstallAsync(Pet("shiba", new string('a', 64), bytes.Length), null, default));

        Assert.Equal("install.sha_mismatch", e.Code);
        Assert.Empty(UserFiles());
    }

    [Fact]
    public async Task Size_mismatch_is_rejected()
    {
        var bytes = ValidPack("shiba");
        ServeFile("GET /api/client/pets/shiba/download", bytes);

        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService().InstallAsync(Pet("shiba", Sha(bytes), bytes.Length + 1), null, default));

        Assert.Equal("install.size_mismatch", e.Code);
        Assert.Empty(UserFiles());
    }

    [Fact]
    public async Task Pack_rejected_by_inspect_is_not_installed()
    {
        // 缺了必需的分类：PetPack.Inspect 报问题。
        var path = PackMaker.Make(Path.Combine(_root, "bad"), "shiba", "柴柴", categories: ["idle"]);
        var bytes = File.ReadAllBytes(path);
        ServeFile("GET /api/client/pets/shiba/download", bytes, Sha(bytes));

        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService().InstallAsync(Pet("shiba", Sha(bytes), bytes.Length), null, default));

        Assert.Equal("install.invalid_pack", e.Code);
        Assert.Contains("缺少", e.Message);
        Assert.Empty(UserFiles());
    }

    [Fact]
    public async Task Pack_with_newer_format_is_rejected()
    {
        var dir = Path.Combine(_root, "future");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "shiba.astrapet");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("pet.json").Open()))
            w.Write("""{"format": 99, "id": "shiba", "name": "柴柴"}""");
        var bytes = File.ReadAllBytes(path);
        ServeFile("GET /api/client/pets/shiba/download", bytes, Sha(bytes));

        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService().InstallAsync(Pet("shiba", Sha(bytes), bytes.Length), null, default));

        Assert.Equal("install.invalid_pack", e.Code);
        Assert.Contains("太新", e.Message);
    }

    [Fact]
    public async Task Built_in_pack_ids_and_unsafe_ids_are_refused_before_downloading()
    {
        var service = CreateService();

        Assert.Equal("install.builtin", (await Assert.ThrowsAsync<HubException>(() =>
            service.InstallAsync(Pet("xiaot", null, null, "https://hub.test/api/client/pets/xiaot/download"), null, default))).Code);
        Assert.Equal("install.bad_id", (await Assert.ThrowsAsync<HubException>(() =>
            service.InstallAsync(Pet("../evil", null, null), null, default))).Code);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task Download_url_on_another_host_is_refused_without_sending_the_token()
    {
        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService().InstallAsync(Pet("shiba", null, null, "https://evil.test/shiba.astrapet"), null, default));

        Assert.Equal("download.bad_url", e.Code);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task Purchase_required_on_download_is_reported()
    {
        _hub.Error("GET /api/client/pets/shiba/download", "download.purchase_required", "这只宠物需要先购买", HttpStatusCode.Forbidden);

        var e = await Assert.ThrowsAsync<HubException>(() =>
            CreateService().InstallAsync(Pet("shiba", null, null), null, default));

        Assert.Equal("download.purchase_required", e.Code);
        Assert.Equal("这只宠物需要先购买", e.Message);
        Assert.Empty(UserFiles());
    }

    [Fact]
    public async Task Unauthorized_download_logs_out()
    {
        _hub.On("GET /api/client/pets/shiba/download", HttpStatusCode.Unauthorized, "");
        var service = CreateService();

        await Assert.ThrowsAsync<HubUnauthorizedException>(() => service.InstallAsync(Pet("shiba", null, null), null, default));

        Assert.False(service.IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(DataDir, HubSessionStore.FileName)));
    }

    /// <summary>同步回调的 IProgress（Progress&lt;T&gt; 会抛到线程池上，测试里不好等）。</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
