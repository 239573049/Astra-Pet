using System.Collections.ObjectModel;
using System.Globalization;
using AstraPet.Core;
using AstraPet.Core.Market;
using AstraPet.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TKCode.Arc;

namespace AstraPet.ControlPanel;

/// <summary>市场列表里的一张卡片。按钮上显示什么由 <see cref="MarketPet"/> 的权益和本机安装状态一起决定。</summary>
public sealed class MarketPetItem(MarketPet pet, string? versionProblem) : Observable
{
    private MarketInstallState _state;
    private Bitmap? _cover;
    private bool _busy;
    private double _progress;
    private string _error = "";

    public MarketPet Pet { get; } = pet;
    public string Title => Pet.Title;
    public string AuthorText => Pet.AuthorName.Length > 0 ? "作者 " + Pet.AuthorName : "";
    public string Summary => Pet.Summary;
    public bool HasSummary => Pet.Summary.Length > 0;
    public string PriceText => Pet.PriceText;

    /// <summary>「★ 4.8（12）· 1.2 千次下载 · v1.0.0」。</summary>
    public string StatsText => string.Join(" · ", new[]
    {
        Pet.Rating is { } r && Pet.RatingCount > 0 ? $"★ {r.ToString("0.0", CultureInfo.InvariantCulture)}（{Pet.RatingCount}）" : "暂无评分",
        $"{Count(Pet.DownloadCount)} 次下载",
        Pet.Version is { Length: > 0 } v ? "v" + v : "",
    }.Where(s => s.Length > 0));

    public Bitmap? Cover { get => _cover; set { if (Set(ref _cover, value)) Raise(nameof(HasCover)); } }
    public bool HasCover => _cover is not null;

    public MarketInstallState State { get => _state; set { if (Set(ref _state, value)) RaiseAction(); } }

    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RaiseAction(); } }

    /// <summary>下载进度 0~100。</summary>
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(ActionText)); } }

    /// <summary>这只宠物上次操作的错误，或者不能下载的原因。</summary>
    public string Note => _error.Length > 0 ? _error
        : Pet.NeedPurchase ? ""
        : !Pet.CanDownload ? Pet.ReasonMessage ?? "暂时不能下载"
        : versionProblem ?? "";
    public bool HasNote => Note.Length > 0;
    public string Error { get => _error; set { if (Set(ref _error, value)) { Raise(nameof(Note)); Raise(nameof(HasNote)); } } }

    public string ActionText => _busy ? $"下载中 {(int)_progress}%"
        : Pet.NeedPurchase ? $"购买 {MarketPrice.Format(Pet.Price)}"
        : !Pet.CanDownload ? "不可下载"
        : versionProblem is not null ? "需要新版本"
        : _state switch
        {
            MarketInstallState.Installed => "已安装",
            MarketInstallState.Outdated => "更新",
            _ => "下载",
        };

    public bool ActionEnabled => !_busy && (Pet.NeedPurchase
        || (Pet.CanDownload && versionProblem is null && _state != MarketInstallState.Installed));

    /// <summary>购买、下载、更新用主色按钮；已安装、不可下载用普通按钮。</summary>
    public bool ActionIsPrimary => ActionEnabled;
    public bool ActionIsSecondary => !ActionEnabled;

    /// <summary>装好了（不管是不是最新版）就能直接换上。</summary>
    public bool CanUse => !_busy && _state != MarketInstallState.NotInstalled;

    private void RaiseAction()
    {
        foreach (var name in new[] { nameof(ActionText), nameof(ActionEnabled), nameof(ActionIsPrimary), nameof(ActionIsSecondary), nameof(CanUse) })
            Raise(name);
    }

    private static string Count(long n) => n switch
    {
        >= 10_000 => (n / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 万",
        >= 1_000 => (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 千",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// 控制台「宠物市场」页和左上角账号按钮共用的状态：未登录 → 设备码登录中 → 已登录（列表、搜索、下载安装）。
/// 登录 / 退出只在左上角账号面板里做，市场页未登录时只给引导。
/// 业务逻辑在 Core 的 <see cref="MarketService"/>；这里管界面状态、封面图和「装好后换上」。
/// 市场地址是构建时写死的（<see cref="MarketHub"/>），界面上哪里都不显示。
/// </summary>
public sealed class MarketViewModel : Observable, IDisposable
{
    private readonly MarketService _service;
    private readonly IHubClient _client;
    private readonly IShell _shell;
    private readonly Action _refreshPets;
    private readonly Func<string, string?> _usePet;
    private readonly Func<bool> _isShown;
    private CancellationTokenSource? _loginCts;
    private CancellationTokenSource? _listCts;
    private HubDeviceCode? _code;
    private string _message = "";
    private string _query = "";
    private string _sort = "hot";
    private string? _cursor;
    private bool _loading;
    private bool _loaded;
    private bool _disposed;
    private int _installing;
    private ImageSource? _avatar;
    private string? _avatarUrl;

    /// <param name="refreshPets">重新扫描角色目录（装好后让新角色出现在「角色」页）。</param>
    /// <param name="usePet">按角色列表里的 id（文件名）切过去；失败返回原因。</param>
    /// <param name="isShown">市场页现在是不是真的给用户看着（控制台预热时会把每页都切一遍，那时不发请求）。</param>
    public MarketViewModel(MarketService service, IHubClient client, IShell shell, Action refreshPets,
        Func<string, string?> usePet, Func<bool> isShown)
    {
        _service = service;
        _client = client;
        _shell = shell;
        _refreshPets = refreshPets;
        _usePet = usePet;
        _isShown = isShown;
        if (_service.IsLoggedIn)
        {
            LoadAvatar();
            _ = RefreshProfileAsync();
        }
    }

    // ───── 状态 ─────

    public bool IsLoggingIn => _loginCts is not null;
    public bool IsLoggedIn => !IsLoggingIn && _service.IsLoggedIn;
    public bool IsLoggedOut => !IsLoggingIn && !_service.IsLoggedIn;

    public string Nickname => _service.Session?.Nickname ?? "";

    /// <summary>控制台左上角账号按钮的两行字：未登录是「Astra Pet / 社区版本」，登录后是用户昵称。</summary>
    public string AccountTitle => IsLoggedIn && Nickname.Length > 0 ? Nickname : "Astra Pet";
    public string AccountSubtitle => IsLoggingIn ? "等待浏览器确认…" : IsLoggedIn ? (Email.Length > 0 ? Email : "已登录") : "社区版本";
    public string AccountTip => IsLoggedIn ? "账号" : "登录";

    public string Email => _service.Session?.Email ?? "";
    public bool HasEmail => IsLoggedIn && Email.Length > 0;

    /// <summary>用户头像，没有或还没下好是 null，头像控件显示昵称首字母。</summary>
    public ImageSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }

    /// <summary>设备码 ABCD-EFGH，给用户和网页上的核对。只显示码，不显示网址。</summary>
    public string UserCode => _code?.UserCode ?? "";

    /// <summary>出错或提示（登录过期、网络不通、服务器的 message）。</summary>
    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;

    public ObservableCollection<MarketPetItem> Items { get; } = [];

    public string Query { get => _query; set => Set(ref _query, value); }

    /// <summary>hot | new | downloads | rating。换了就重新拉。</summary>
    public string Sort
    {
        get => _sort;
        set
        {
            if (!Set(ref _sort, value)) return;
            if (IsLoggedIn) _ = RefreshAsync();
        }
    }

    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) RaiseList(); } }
    public bool CanRefresh => !_loading;
    public bool HasMore => _cursor is not null && !_loading;
    public bool IsEmpty => _loaded && !_loading && Items.Count == 0;
    public string EmptyText => _query.Trim().Length > 0 ? "没有找到相关的宠物，换个关键词试试。" : "市场里还没有宠物。";

    /// <summary>正在登录或下载。这时控制台不释放。</summary>
    public bool Busy => _loginCts is not null || _installing > 0;

    private void RaiseStage()
    {
        foreach (var name in new[] { nameof(IsLoggingIn), nameof(IsLoggedIn), nameof(IsLoggedOut), nameof(Nickname), nameof(UserCode),
                     nameof(AccountTitle), nameof(AccountSubtitle), nameof(AccountTip), nameof(Email), nameof(HasEmail) })
            Raise(name);
        LoadAvatar();
    }

    // ───── 账号资料 ─────

    /// <summary>打开控制台时补一次资料（邮箱、头像、改过的昵称）。没网就算了，下次再说。</summary>
    private async Task RefreshProfileAsync()
    {
        try
        {
            if (await _service.RefreshProfileAsync(CancellationToken.None)) RaiseStage();
        }
        catch (HubUnauthorizedException)
        {
            ResetList();
            RaiseStage();
        }
        catch (Exception e)
        {
            PetLog.Write($"刷新市场账号资料失败: {e.Message}");
        }
    }

    /// <summary>头像地址变了就重新下（走和封面一样的缓存），退出登录就清掉。</summary>
    private void LoadAvatar()
    {
        var url = _service.IsLoggedIn ? _service.Session?.AvatarUrl : null;
        if (url == _avatarUrl) return;
        _avatarUrl = url;
        Avatar = null;
        if (url is null) return;
        _ = Load();

        async Task Load()
        {
            var bitmap = await MarketImages.LoadAsync(_client, url, CancellationToken.None);
            if (bitmap is not null && !_disposed && _avatarUrl == url) Avatar = ImageSource.FromBitmap(bitmap);
        }
    }

    private void RaiseList()
    {
        foreach (var name in new[] { nameof(CanRefresh), nameof(HasMore), nameof(IsEmpty), nameof(EmptyText) })
            Raise(name);
    }

    /// <summary>切到市场页时调。已登录、还没拉过列表就拉一次；稍等一下再确认页面真的在显示（预热时不拉）。</summary>
    public void Activate()
    {
        if (_loaded || _loading || !IsLoggedIn) return;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_disposed && !_loaded && !_loading && IsLoggedIn && _isShown()) _ = RefreshAsync();
        }, TimeSpan.FromMilliseconds(400));
    }

    // ───── 登录 ─────

    public async Task LoginAsync()
    {
        if (_loginCts is not null) return;
        var cts = _loginCts = new CancellationTokenSource();
        Message = "";
        RaiseStage();
        try
        {
            _code = await _service.StartLoginAsync(cts.Token);
            RaiseStage();
            OpenVerification();
            await _service.CompleteLoginAsync(_code, cts.Token);
            Finish();
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Finish();
        }
        catch (HubException e)
        {
            Finish();
            Message = e.Message;
        }
        catch (Exception e)
        {
            PetLog.Write($"市场登录失败: {e.Message}");
            Finish();
            Message = "登录失败：" + e.Message;
        }

        void Finish()
        {
            if (_loginCts == cts) _loginCts = null;
            cts.Dispose();
            _code = null;
            RaiseStage();
        }
    }

    public void ReopenBrowser() => OpenVerification();

    public void CancelLogin() => _loginCts?.Cancel();

    private void OpenVerification()
    {
        if (_code is { } code)
            _shell.OpenUrl(code.VerificationUriComplete.Length > 0 ? code.VerificationUriComplete : code.VerificationUri);
    }

    public async Task LogoutAsync()
    {
        _listCts?.Cancel();
        await _service.LogoutAsync();
        ResetList();
        Message = "";
        RaiseStage();
    }

    // ───── 列表 ─────

    public Task RefreshAsync()
    {
        ResetList();
        return LoadPageAsync();
    }

    public Task LoadMoreAsync() => _cursor is null || _loading ? Task.CompletedTask : LoadPageAsync();

    private void ResetList()
    {
        _listCts?.Cancel();
        _listCts = null;
        _cursor = null;
        _loaded = false;
        // 正在下载的卡片留着会和新列表对不上；下载本身不受影响，装完照常刷新角色列表。
        Items.Clear();
        _loading = false;
        Raise(nameof(IsLoading));
        RaiseList();
    }

    private async Task LoadPageAsync()
    {
        if (!_service.IsLoggedIn) return;
        var cts = _listCts = new CancellationTokenSource();
        IsLoading = true;
        Message = "";
        try
        {
            var page = await _service.ListAsync(_query, ParseSort(_sort), _cursor, cts.Token);
            if (cts.IsCancellationRequested) return;
            var items = page.Items.Select(p => new MarketPetItem(p, _service.VersionProblem(p))).ToList();
            foreach (var item in items) Items.Add(item);
            _cursor = page.NextCursor;
            _loaded = true;
            _ = FillAsync(items, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (HubException e)
        {
            if (!cts.IsCancellationRequested) Fail(e);
        }
        catch (Exception e)
        {
            PetLog.Write($"市场列表加载失败: {e.Message}");
            if (!cts.IsCancellationRequested) Message = "加载失败：" + e.Message;
        }
        finally
        {
            if (_listCts == cts)
            {
                IsLoading = false;
                RaiseList();
            }
        }
    }

    /// <summary>本机安装状态（要算文件哈希）和封面图都在后台做，好了再填回卡片。</summary>
    private async Task FillAsync(List<MarketPetItem> items, CancellationToken ct)
    {
        try
        {
            var states = await Task.Run(() => items.Select(i => _service.InstallStateOf(i.Pet)).ToList(), ct);
            for (var i = 0; i < items.Count; i++) items[i].State = states[i];
        }
        catch (OperationCanceledException) { return; }
        await Task.WhenAll(items.Select(async item =>
        {
            if (item.Pet.CoverUrl is { } url && await MarketImages.LoadAsync(_client, url, ct) is { } bitmap)
                item.Cover = bitmap;
        }));
    }

    private static MarketSort ParseSort(string sort) => sort switch
    {
        "new" => MarketSort.New,
        "downloads" => MarketSort.Downloads,
        "rating" => MarketSort.Rating,
        _ => MarketSort.Hot,
    };

    /// <summary>401：本地令牌已经被 MarketService 清掉了，回到未登录；其他错误原样显示服务器的 message。</summary>
    private void Fail(HubException e)
    {
        if (e is HubUnauthorizedException)
        {
            ResetList();
            RaiseStage();
            Message = HubUnauthorizedException.DefaultMessage;
            return;
        }
        Message = e.Message;
    }

    // ───── 购买、下载、使用 ─────

    /// <summary>卡片上的主按钮：要买就去浏览器，能下就下载安装（或更新）。</summary>
    public async Task ActAsync(MarketPetItem item)
    {
        if (!item.ActionEnabled) return;
        if (item.Pet.NeedPurchase)
        {
            if (item.Pet.PageUrl.Length > 0) _shell.OpenUrl(item.Pet.PageUrl);
            return;
        }

        item.Busy = true;
        _installing++;
        item.Progress = 0;
        item.Error = "";
        try
        {
            var progress = new Progress<double>(p => item.Progress = p * 100);
            await Task.Run(() => _service.InstallAsync(item.Pet, progress, CancellationToken.None));
            item.State = MarketInstallState.Installed;
            _refreshPets();
        }
        catch (HubException e)
        {
            PetLog.Write($"从市场安装「{item.Pet.Title}」失败: {e.Code} {e.Message}");
            if (e is HubUnauthorizedException) Fail(e);
            else item.Error = e.Message;
        }
        catch (Exception e)
        {
            PetLog.Write($"从市场安装「{item.Pet.Title}」失败: {e}");
            item.Error = "安装失败：" + e.Message;
        }
        finally
        {
            item.Busy = false;
            _installing--;
        }
    }

    /// <summary>换上这只（走控制台「角色」页同一条切换路径）。</summary>
    public void Use(MarketPetItem item)
    {
        item.Error = _usePet(Path.GetFileName(_service.InstalledPath(item.Pet))) ?? "";
    }

    public void Dispose()
    {
        _disposed = true;
        _listCts?.Cancel();
        _loginCts?.Cancel();
    }
}

/// <summary>
/// 封面图的内存缓存：按地址缓存解码好的位图，控制台关了重开不用再下。Hub 给的是 webp，Avalonia 的 Skia 能解；
/// 解不了（或下载失败）返回 null，卡片显示占位图。只缩到卡片需要的宽度，最多留一百来张。
/// </summary>
internal static class MarketImages
{
    private const int MaxEntries = 120;
    private const int DecodeWidth = 480;
    private static readonly Dictionary<string, Task<Bitmap?>> Cache = new(StringComparer.Ordinal);

    public static Task<Bitmap?> LoadAsync(IHubClient client, string url, CancellationToken ct)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var cached)) return cached;
            if (Cache.Count >= MaxEntries) Cache.Clear();   // 简单粗暴：满了就整个清掉，正在显示的位图由卡片自己引用着
            var task = Fetch(client, url);
            Cache[url] = task;
            return ct.IsCancellationRequested ? Task.FromResult<Bitmap?>(null) : task;
        }
    }

    private static async Task<Bitmap?> Fetch(IHubClient client, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var bytes = await client.GetImageAsync(uri, CancellationToken.None);
        if (bytes is null)
        {
            lock (Cache) Cache.Remove(url);   // 网络失败下次再试
            return null;
        }
        return await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, DecodeWidth);
            }
            catch (Exception e)
            {
                PetLog.Write($"市场封面解码失败（{bytes.Length} 字节）: {e.Message}");
                return (Bitmap?)null;
            }
        });
    }
}
