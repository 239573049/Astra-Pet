using AstraPet.Core;
using AstraPet.Infrastructure.Update;

namespace AstraPet.Tests;

/// <summary>返回固定发布信息的假源；记下请求过的地址。</summary>
internal sealed class FakeFeed(ReleaseFeed? feed, Exception? error = null) : IUpdateFeed
{
    public List<Uri> Requested { get; } = new();

    public Task<ReleaseFeed?> GetLatestAsync(Uri url, CancellationToken ct)
    {
        Requested.Add(url);
        return error is null ? Task.FromResult(feed) : Task.FromException<ReleaseFeed?>(error);
    }
}

public class UpdateCheckerTests
{
    private static readonly ReleaseFile[] Files =
    [
        new("darwin-arm64", "zip", "/files/AstraPet-0.2.0-darwin-arm64.zip", 100),
        new("darwin-arm64", "dmg", "/files/AstraPet-0.2.0-darwin-arm64.dmg", 200),
        new("win32-x64", "exe", "/files/AstraPet-0.2.0-win32-x64.exe", 300),
    ];

    private static (UpdateChecker Checker, FakeFeed Feed, PetSettings Settings) Create(
        ReleaseFeed? feed, string current = "0.1.3", Exception? error = null, string server = "https://example.test")
    {
        var settings = new PetSettings { UpdateServer = server };
        var fake = new FakeFeed(feed, error);
        return (new UpdateChecker(settings, fake, "darwin-arm64", ["dmg", "zip"], current), fake, settings);
    }

    [Fact]
    public async Task Newer_version_picks_preferred_installer_for_this_platform()
    {
        var (checker, feed, _) = Create(new ReleaseFeed("0.2.0", "stable", "修了很多", null, Files));
        var info = await checker.CheckAsync();

        Assert.NotNull(info);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal("dmg", info.Kind);                                          // dmg 优先于 zip
        Assert.Equal("https://example.test/files/AstraPet-0.2.0-darwin-arm64.dmg", info.DownloadUrl);
        Assert.Equal("https://example.test/pet", info.PageUrl);                  // 没给下载页时用默认的
        Assert.Equal("https://example.test/api/pet-releases/stable/latest.json?platform=darwin-arm64",
            Assert.Single(feed.Requested).ToString());
    }

    [Fact]
    public async Task Preview_channel_is_requested_when_enabled()
    {
        var (checker, feed, settings) = Create(null);
        settings.UpdateChannel = "preview";
        await checker.CheckAsync();
        Assert.Contains("/pet-releases/preview/", Assert.Single(feed.Requested).ToString());
    }

    [Theory]
    [InlineData("0.1.3")]
    [InlineData("0.1.2")]
    [InlineData("0.1.3-preview.4")]
    public async Task Same_or_older_version_is_up_to_date(string latest)
    {
        var (checker, _, _) = Create(new ReleaseFeed(latest, "stable", "", null, Files));
        Assert.Null(await checker.CheckAsync());
        Assert.Equal(UpdateState.UpToDate, checker.State);
        Assert.False(checker.HasUpdate);
    }

    [Fact]
    public async Task Channel_without_releases_is_up_to_date()
    {
        var (checker, _, _) = Create(feed: null);   // 站点返回 404
        Assert.Null(await checker.CheckAsync());
        Assert.Equal(UpdateState.UpToDate, checker.State);
    }

    [Fact]
    public async Task No_installer_for_this_platform_falls_back_to_download_page()
    {
        var (checker, _, _) = Create(new ReleaseFeed("0.2.0", "stable", "", "/pet/download", [Files[2]]));
        var info = await checker.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal("", info.DownloadUrl);
        Assert.Equal("https://example.test/pet/download", info.PageUrl);
    }

    [Fact]
    public async Task Bad_server_fails_without_requesting()
    {
        var (checker, feed, _) = Create(null, server: "ftp://example.test");
        Assert.Null(await checker.CheckAsync());
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Contains("需要 http:// 或 https://", checker.Message);
        Assert.Empty(feed.Requested);
    }

    [Fact]
    public async Task Network_error_is_reported_not_thrown()
    {
        var (checker, _, _) = Create(null, error: new HttpRequestException("连不上"));
        Assert.Null(await checker.CheckAsync());
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Contains("连不上", checker.Message);
    }

    [Fact]
    public async Task Garbage_version_is_reported()
    {
        var (checker, _, _) = Create(new ReleaseFeed("latest", "stable", "", null, []));
        Assert.Null(await checker.CheckAsync());
        Assert.Equal(UpdateState.Failed, checker.State);
    }

    [Fact]
    public void Feed_json_is_parsed_case_insensitively()
    {
        var feed = HttpUpdateFeed.Parse("""
            { "Version": "0.2.0", "channel": "stable", "notes": "n",
              "files": [ { "platform": "linux-x64", "kind": "deb", "url": "/f.deb", "size": 42 } ] }
            """);
        Assert.NotNull(feed);
        Assert.Equal("0.2.0", feed.Version);
        var file = Assert.Single(feed.Files);
        Assert.Equal(new ReleaseFile("linux-x64", "deb", "/f.deb", 42), file);
    }
}

public class ModelCatalogTests
{
    [Fact]
    public void Embedded_snapshot_is_loaded()
    {
        // 资源从 Core 挪到了 Infrastructure：找不到时会悄悄退化成空目录，这里兜住。
        // 数据目录指向一个空的临时目录，保证读的是内嵌快照而不是本机缓存。
        SettingsStore.UseDirectory(Path.Combine(Path.GetTempPath(), "astra-catalog-" + Guid.NewGuid().ToString("N")));
        var catalog = AstraPet.Ai.ModelCatalog.Current;
        Assert.False(catalog.FromCache);
        Assert.True(catalog.ModelCount > 100, catalog.StatusText);
    }
}
