using System.IO.Compression;
using System.Text;
using AstraPet.Core;

namespace AstraPet.Tests;

/// <summary>在临时目录里造角色包：和 tools/pack_pet.py 的布局一样，视频内容是空的（假解码器不读）。</summary>
internal static class PackMaker
{
    public static string Make(string dir, string id, string name, bool isDefault = false,
        IEnumerable<string>? categories = null, IEnumerable<(string Name, string Content)>? extra = null, string credits = "[]")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + PetPack.Extension);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("pet.json", $$"""{ "format": 1, "id": "{{id}}", "name": "{{name}}", "default": {{(isDefault ? "true" : "false")}}, "license": "CC BY-NC-SA 4.0", "licenseUrl": "https://example.test/l", "credits": {{credits}} }""");
        foreach (var c in categories ?? PetLibrary.Required) Add($"videos/{c}/{c}-1.webm", "");
        foreach (var (entry, content) in extra ?? []) Add(entry, content);
        return path;
    }

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "astra-pack-" + Guid.NewGuid().ToString("N"));
}

public class PetPackTests : IDisposable
{
    private readonly string _dir = PackMaker.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("pet.json", true)]
    [InlineData("preview.png", true)]
    [InlineData("LICENSE.txt", true)]
    [InlineData("videos/move_strides.json", true)]
    [InlineData("videos/click/开心挥手.webm", true)]
    [InlineData("../evil.webm", false)]
    [InlineData("videos/../../evil.webm", false)]
    [InlineData("videos/click/../../../evil.webm", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("videos\\click\\a.webm", false)]
    [InlineData("videos/click/run.exe", false)]
    [InlineData("videos/Click/a.webm", false)]
    [InlineData("other.txt", false)]
    public void Only_known_files_inside_the_pack_are_allowed(string name, bool allowed) =>
        Assert.Equal(allowed, PetPack.IsAllowed(name));

    [Fact]
    public void Inspect_reports_missing_categories_without_extracting()
    {
        var path = PackMaker.Make(_dir, "half", "半成品", categories: ["idle", "click"]);
        var (manifest, problem) = PetPack.Inspect(path);
        Assert.Equal("半成品", manifest?.Name);
        Assert.Contains("drag", problem);
    }

    [Fact]
    public void Inspect_rejects_non_zip_files()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "fake" + PetPack.Extension);
        File.WriteAllText(path, "not a zip");
        var (manifest, problem) = PetPack.Inspect(path);
        Assert.Null(manifest);
        Assert.StartsWith("不是有效的角色包", problem);
    }

    [Fact]
    public void Extract_reuses_cache_and_replaces_old_versions()
    {
        var cache = Path.Combine(_dir, "cache");
        var pack = PackMaker.Make(Path.Combine(_dir, "packs"), "whale", "鲸");

        var videos = PetPack.Extract(pack, cache);
        Assert.True(File.Exists(Path.Combine(videos, "idle", "idle-1.webm")));
        var marker = File.GetLastWriteTimeUtc(Path.Combine(videos, "..", ".complete"));
        Assert.Equal(videos, PetPack.Extract(pack, cache));   // 同一个包：不重新解压
        Assert.Equal(marker, File.GetLastWriteTimeUtc(Path.Combine(videos, "..", ".complete")));

        File.Delete(pack);
        PackMaker.Make(Path.Combine(_dir, "packs"), "whale", "鲸", extra: [("videos/text_clips.json", "{}")]);
        var updated = PetPack.Extract(pack, cache);
        Assert.NotEqual(videos, updated);                       // 内容变了：新目录
        Assert.False(Directory.Exists(videos));                 // 旧版本清掉
        Assert.True(File.Exists(Path.Combine(updated, "text_clips.json")));
    }

    [Fact]
    public void Extract_refuses_path_traversal_and_leaves_nothing_behind()
    {
        var cache = Path.Combine(_dir, "cache");
        var pack = PackMaker.Make(Path.Combine(_dir, "packs"), "evil", "坏包",
            extra: [("videos/../../escaped.webm", "x")]);
        Assert.Throws<InvalidDataException>(() => PetPack.Extract(pack, cache));
        Assert.False(File.Exists(Path.Combine(cache, "escaped.webm")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.webm")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(cache, "pets")));   // 临时目录也清掉了
    }
}

public class PetCatalogTests : IDisposable
{
    private readonly string _dir = PackMaker.TempDir();
    private string BuiltIn => Path.Combine(_dir, "builtin");
    private string User => Path.Combine(_dir, "user");
    private string Cache => Path.Combine(_dir, "cache");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private PetCatalog Create(string? defaultOverride = null)
    {
        PackMaker.Make(BuiltIn, "xiaot", "小t", isDefault: true);
        PackMaker.Make(BuiltIn, "dafeiyu", "大肥鱼", credits: """["形象原型：@上善无形"]""");
        return new PetCatalog(BuiltIn, User, Cache, defaultOverride);
    }

    [Fact]
    public void Default_pack_comes_first_with_empty_id_then_other_builtins_then_user_pets()
    {
        var catalog = Create();
        PackMaker.Make(User, "mine", "我的包");
        Directory.CreateDirectory(Path.Combine(User, "folder-pet", "videos", "idle"));   // 素材不全的文件夹

        var pets = catalog.Scan();
        Assert.Equal(["", "builtin:dafeiyu", "folder-pet", "mine.astrapet"], pets.Select(p => p.Id));
        Assert.Equal("小t", pets[0].Name);
        Assert.True(pets[0].IsDefault && pets[0].IsBuiltIn && pets[1].IsBuiltIn);
        Assert.False(pets[2].IsBuiltIn);
        Assert.NotNull(pets[2].Problem);
        Assert.Null(pets[3].Problem);
        Assert.Equal(["形象原型：@上善无形"], pets[1].Credits!.Lines);
        Assert.Equal("CC BY-NC-SA 4.0", pets[1].Credits!.License);
    }

    [Fact]
    public void Loads_the_selected_builtin_pack()
    {
        var catalog = Create();
        var settings = new PetSettings { PetId = "builtin:dafeiyu" };
        var library = catalog.LoadSelected(settings, new FakeDecoder());
        Assert.Equal("builtin:dafeiyu", settings.PetId);
        Assert.Equal(PetLibrary.Required.Count(), library.ClipCount);
    }

    [Fact]
    public void Missing_selection_falls_back_to_default_and_resets_setting()
    {
        var catalog = Create();
        var settings = new PetSettings { PetId = "deleted-pet" };
        catalog.LoadSelected(settings, new FakeDecoder());
        Assert.Equal("", settings.PetId);
    }

    [Fact]
    public void Dev_override_replaces_the_default_pet()
    {
        using var lib = new TempLibrary();
        var catalog = Create(defaultOverride: lib.Dir);
        var pets = catalog.Scan();
        Assert.Single(pets, p => p.IsDefault);
        Assert.Equal(lib.Dir, catalog.Resolve(pets[0]));
        Assert.Contains("开发素材", pets[0].Name);
    }

    [Fact]
    public void No_default_pack_is_a_clear_error()
    {
        var catalog = new PetCatalog(BuiltIn, User, Cache);
        var e = Assert.Throws<FileNotFoundException>(() => catalog.LoadSelected(new PetSettings(), new FakeDecoder()));
        Assert.Contains("默认角色", e.Message);
    }
}

/// <summary>仓库里提交的两个内置角色包：清单正确、素材齐全、大肥鱼的许可和署名在。</summary>
public class BuiltInPackTests
{
    private static string PetsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AstraPet.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Assets", "pets");
    }

    [Fact]
    public void Xiaot_is_the_default_and_complete()
    {
        var (manifest, problem) = PetPack.Inspect(Path.Combine(PetsDir(), "xiaot.astrapet"));
        Assert.Null(problem);
        Assert.True(manifest!.Default);
        Assert.Equal("小t", manifest.Name);
    }

    [Fact]
    public void Dafeiyu_is_complete_and_carries_its_license_and_credits()
    {
        var (manifest, problem) = PetPack.Inspect(Path.Combine(PetsDir(), "dafeiyu.astrapet"));
        Assert.Null(problem);
        Assert.False(manifest!.Default);
        Assert.Equal("大肥鱼", manifest.Name);
        Assert.Contains("CC BY-NC-SA 4.0", manifest.License);
        Assert.Contains(manifest.Credits, c => c.Contains("@上善无形"));
        Assert.Contains(manifest.Credits, c => c.Contains("@ZipZipPipe"));
        Assert.Contains(manifest.Credits, c => c.Contains("github.com/PC2005-cloud/dsh-pet"));
        Assert.Contains(manifest.Credits, c => c.Contains("不是 DeepSeek 官方形象"));
    }

    [Fact]
    public void Only_one_builtin_pack_is_default()
    {
        var defaults = Directory.EnumerateFiles(PetsDir(), "*" + PetPack.Extension)
            .Count(f => PetPack.Inspect(f).Manifest?.Default == true);
        Assert.Equal(1, defaults);
    }
}
