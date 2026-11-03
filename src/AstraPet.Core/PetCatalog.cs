using System.Text.Json;

namespace AstraPet.Core;

/// <summary>角色的作者、许可和署名，控制台里显示。来自角色包的 pet.json。</summary>
public sealed record PetCredits(string Author, string License, string LicenseUrl, string Homepage, IReadOnlyList<string> Lines);

/// <summary>
/// 一个可选的角色。<see cref="Id"/> 存进设置里：默认的内置角色是空串（老版本的设置也是空串，升级后仍是它），
/// 其他内置角色是 builtin:&lt;id&gt;，用户角色是它在角色目录下的文件夹名或角色包文件名。
/// <see cref="Location"/> 是素材目录或角色包文件。<see cref="Problem"/> 不为空说明素材不全，不能用，内容是给用户看的原因。
/// </summary>
public sealed record PetEntry(string Id, string Name, string Location, bool IsPack, bool IsBuiltIn, string? Problem,
    PetCredits? Credits = null)
{
    public const string BuiltInPrefix = "builtin:";

    public bool IsDefault => Id.Length == 0;

    public override string ToString() => Name;
}

/// <summary>
/// 角色目录：内置的角色包（程序的 Assets/pets/*.astrapet，带 default 标记的是默认角色），
/// 加上用户的角色目录（数据目录下的 characters）里的文件夹和角色包。
/// 文件夹的布局和仓库里的 characters/ 一样：&lt;角色&gt;/videos/{idle,click,drag,move,turn}/*.webm
/// （没有 videos 子文件夹时，分类文件夹直接放在角色文件夹里也行），显示名取 character.json 的 name，没有就用文件夹名。
/// 扫描只看目录结构和清单，不起 ffmpeg 探测，所以很快；真正加载时才会发现的问题（尺寸不对、没有 alpha）在切换时报。
/// </summary>
public sealed class PetCatalog
{
    private readonly string _builtInPacks;
    private readonly string _cacheDirectory;
    private readonly string? _defaultOverride;

    /// <param name="builtInPacks">内置角色包所在目录。</param>
    /// <param name="userDirectory">用户的角色目录，不存在也没关系。</param>
    /// <param name="cacheDirectory">角色包解压到这里。</param>
    /// <param name="defaultOverride">开发用：用这个素材目录代替默认角色（环境变量 ASTRA_PET_VIDEOS）。</param>
    public PetCatalog(string builtInPacks, string userDirectory, string cacheDirectory, string? defaultOverride = null)
    {
        _builtInPacks = builtInPacks;
        UserDirectory = userDirectory;
        _cacheDirectory = cacheDirectory;
        _defaultOverride = defaultOverride;
    }

    public string UserDirectory { get; }

    /// <summary>默认角色被 ASTRA_PET_VIDEOS 换掉时的素材目录，没换为 null。开机自启要把它带过去。</summary>
    public string? DefaultOverride => _defaultOverride;

    /// <summary>默认角色排第一，然后是其他内置角色，最后是用户的，各自按名字排。</summary>
    public List<PetEntry> Scan()
    {
        var builtIn = new List<PetEntry>();
        if (Directory.Exists(_builtInPacks))
            foreach (var file in Directory.EnumerateFiles(_builtInPacks, "*" + PetPack.Extension))
            {
                var (manifest, problem) = PetPack.Inspect(file);
                if (manifest is null)
                {
                    PetLog.Write($"内置角色包损坏，跳过: {file}: {problem}");
                    continue;
                }
                var id = manifest.Default ? "" : PetEntry.BuiltInPrefix + manifest.Id;
                builtIn.Add(new PetEntry(id, manifest.Name, file, IsPack: true, IsBuiltIn: true, problem, CreditsOf(manifest)));
            }

        if (_defaultOverride is not null)
        {
            var name = builtIn.FirstOrDefault(p => p.IsDefault)?.Name ?? "默认角色";
            builtIn.RemoveAll(p => p.IsDefault);
            builtIn.Add(new PetEntry("", name + "（开发素材）", _defaultOverride, IsPack: false, IsBuiltIn: true,
                CheckFolder(_defaultOverride)));
        }

        var pets = builtIn.OrderBy(p => p.IsDefault ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        pets.AddRange(ScanUser());
        return pets;
    }

    /// <summary>角色的素材目录。角色包第一次用到时解压到缓存目录（几十毫秒到一两秒），放到后台线程上调。</summary>
    public string Resolve(PetEntry pet) => pet.IsPack ? PetPack.Extract(pet.Location, _cacheDirectory) : pet.Location;

    /// <summary>
    /// 启动时加载设置里选的角色。它被删了、素材坏了就退回默认角色，并把设置改回默认，
    /// 免得每次启动都失败一遍；默认角色也加载不了时直接抛异常。
    /// </summary>
    public PetLibrary LoadSelected(PetSettings settings, IClipDecoder decoder)
    {
        var all = Scan();
        if (settings.PetId.Length > 0)
        {
            try
            {
                var entry = all.FirstOrDefault(p => p.Id == settings.PetId)
                            ?? throw new DirectoryNotFoundException("找不到这个角色");
                return Load(entry, decoder);
            }
            catch (Exception e)
            {
                PetLog.Write($"角色「{settings.PetId}」加载失败，改用默认角色: {e.Message}");
                settings.PetId = "";
            }
        }
        var fallback = all.FirstOrDefault(p => p.IsDefault)
                       ?? throw new FileNotFoundException($"找不到默认角色：{_builtInPacks} 里没有带 default 标记的角色包");
        return Load(fallback, decoder);
    }

    /// <summary>解压（如果是角色包）并探测素材。素材不全时抛异常。</summary>
    public PetLibrary Load(PetEntry pet, IClipDecoder decoder)
    {
        if (pet.Problem is { } problem) throw new InvalidOperationException(problem);
        var videos = Resolve(pet);
        PetLog.Write($"角色: {pet.Name} ({videos})");
        return PetLibrary.Load(videos, decoder, PetLog.Write);
    }

    private IEnumerable<PetEntry> ScanUser()
    {
        if (!Directory.Exists(UserDirectory)) yield break;
        var entries = new List<PetEntry>();
        foreach (var dir in Directory.EnumerateDirectories(UserDirectory))
        {
            var id = Path.GetFileName(dir);
            if (id.StartsWith('.')) continue;
            var videos = Directory.Exists(Path.Combine(dir, "videos")) ? Path.Combine(dir, "videos") : dir;
            entries.Add(new PetEntry(id, ReadName(dir) ?? id, videos, IsPack: false, IsBuiltIn: false, CheckFolder(videos)));
        }
        foreach (var file in Directory.EnumerateFiles(UserDirectory, "*" + PetPack.Extension))
        {
            var (manifest, problem) = PetPack.Inspect(file);
            entries.Add(new PetEntry(Path.GetFileName(file), manifest?.Name ?? Path.GetFileNameWithoutExtension(file), file,
                IsPack: true, IsBuiltIn: false, problem, manifest is null ? null : CreditsOf(manifest)));
        }
        foreach (var entry in entries.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
            yield return entry;
    }

    private static PetCredits CreditsOf(PetPackManifest m) => new(m.Author, m.License, m.LicenseUrl, m.Homepage, m.Credits);

    /// <summary>必需的分类里有没有 webm。全有返回 null，否则返回缺哪些。</summary>
    private static string? CheckFolder(string videosDir)
    {
        var missing = PetLibrary.Required.Where(c => !HasClips(Path.Combine(videosDir, c))).ToList();
        return missing.Count == 0 ? null : $"缺少 {string.Join("、", missing)} 的 webm";
    }

    private static bool HasClips(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.webm").Any();

    private static string? ReadName(string petDir)
    {
        var file = Path.Combine(petDir, "character.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("name", out var name)
                   && name.ValueKind == JsonValueKind.String
                   && name.GetString() is { Length: > 0 } s
                ? s.Trim()
                : null;
        }
        catch (Exception e)
        {
            PetLog.Write($"读取 {file} 失败: {e.Message}");
            return null;
        }
    }
}
