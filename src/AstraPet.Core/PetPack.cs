using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AstraPet.Core;

/// <summary>角色包的清单（包里的 pet.json）。credits 是署名，一行一条，控制台里原样显示。</summary>
public sealed record PetPackManifest(
    int Format,
    string Id,
    string Name,
    bool Default,
    string Author,
    string License,
    string LicenseUrl,
    string Homepage,
    IReadOnlyList<string> Credits);

/// <summary>
/// 角色包（.astrapet）：一个 zip，里面是 pet.json、videos/&lt;分类&gt;/*.webm、可选的步幅和镜像配置、预览图、许可说明。
/// 由 tools/pack_pet.py 生成。ffmpeg 要读真实文件，所以第一次用到时解压到缓存目录，按包内容的哈希分版本，之后直接复用。
/// 包可能来自用户（拷进角色目录的），解压时检查路径和文件类型，防止写到缓存目录外面。
/// </summary>
public static class PetPack
{
    public const string Extension = ".astrapet";
    public const int SupportedFormat = 2;

    /// <summary>解压后总大小的上限，防止压缩炸弹（07 §4）。内置的包只有几 MB。</summary>
    internal const long MaxExtractedBytes = 1024L * 1024 * 1024;
    internal const long MaxWebmBytes = 64L * 1024 * 1024;
    internal const long MaxPngBytes = 16L * 1024 * 1024;
    internal const long MaxOtherBytes = 1024L * 1024;

    private const string CompleteMarker = ".complete";

    /// <summary>读清单并检查必需的分类在不在，不解压。返回的 problem 不为 null 时这个包不能用，内容是给人看的原因。</summary>
    public static (PetPackManifest? Manifest, string? Problem) Inspect(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = ReadManifest(zip);
            if (manifest.Format > SupportedFormat)
                return (manifest, $"角色包格式 {manifest.Format} 太新，请升级 Astra Pet");
            CheckEntries(zip, manifest.Format);
            var categories = manifest.Format >= 2
                ? ManifestRoles(zip)
                : zip.Entries
                    .Select(e => e.FullName.Split('/'))
                    .Where(p => p is ["videos", _, var file] && file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1])
                    .ToHashSet();
            var missing = PetLibrary.Required.Where(c => !categories.Contains(c)).ToList();
            return (manifest, missing.Count == 0 ? null : $"缺少 {string.Join("、", missing)} 的 webm");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, $"不是有效的角色包：{e.Message}");
        }
    }

    /// <summary>格式 2：pet.json 里所有动作的角色（片段是否真的在包里，解压和加载时检查）。</summary>
    private static HashSet<string> ManifestRoles(ZipArchive zip)
    {
        using var stream = zip.GetEntry("pet.json")!.Open();
        using var doc = JsonDocument.Parse(stream);
        var roles = new HashSet<string>();
        if (doc.RootElement.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            foreach (var action in actions.EnumerateArray())
                if (action.ValueKind == JsonValueKind.Object)
                    roles.UnionWith(ActionManifest.Strings(action, "roles"));
        return roles;
    }

    /// <summary>
    /// 解压到 <paramref name="cacheRoot"/>/pets/&lt;id&gt;-&lt;哈希&gt;/，返回其中的 videos 目录。已经解压过（同一个包内容）就直接返回。
    /// 先解到临时目录、全部成功再改名，中途失败或程序被杀不会留下半个目录。顺带清掉同一个角色的旧版本。
    /// </summary>
    public static string Extract(string path, string cacheRoot)
    {
        var hash = HashOf(path);
        PetPackManifest manifest;
        using (var zip = ZipFile.OpenRead(path)) manifest = ReadManifest(zip);
        var (id, format) = (manifest.Id, manifest.Format);

        var root = Path.Combine(cacheRoot, "pets");
        var target = Path.Combine(root, $"{id}-{hash}");
        // 格式 1 返回 videos 目录；格式 2 返回根目录（pet.json 和 clips/ 在这里）。
        var videos = format >= 2 ? target : Path.Combine(target, "videos");
        if (File.Exists(Path.Combine(target, CompleteMarker))) return videos;

        Directory.CreateDirectory(root);
        var temp = Path.Combine(root, $".{id}-{Guid.NewGuid():N}");
        try
        {
            ExtractSafely(path, temp);
            File.WriteAllText(Path.Combine(temp, CompleteMarker), "");
            if (Directory.Exists(target)) Directory.Delete(target, true);   // 上次解到一半的残留
            Directory.Move(temp, target);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        foreach (var old in Directory.EnumerateDirectories(root, $"{id}-*"))
            if (!string.Equals(old, target, StringComparison.Ordinal))
                TryDelete(old);
        PetLog.Write($"角色包已解压: {Path.GetFileName(path)} → {target}");
        return videos;
    }

    private static PetPackManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("pet.json") ?? throw new InvalidDataException("包里没有 pet.json");
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        string Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

        var id = Str("id");
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            throw new InvalidDataException($"pet.json 的 id 不合法：「{id}」（只能是小写字母、数字和短横线）");
        var credits = root.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        return new PetPackManifest(
            root.TryGetProperty("format", out var f) && f.TryGetInt32(out var format) ? format : 1,
            id,
            Str("name") is { Length: > 0 } name ? name : id,
            root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True,
            Str("author"), Str("license"), Str("licenseUrl"), Str("homepage"),
            credits);
    }

    /// <summary>只放行认识的文件，路径必须落在目标目录里。</summary>
    private static void ExtractSafely(string path, string destination)
    {
        Directory.CreateDirectory(destination);
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(path);
        CheckEntries(zip, ReadManifest(zip).Format);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;   // 目录条目
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(full, StringComparison.Ordinal))
                throw new InvalidDataException($"角色包里的路径不合法：{entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, overwrite: false);
        }
    }

    /// <summary>
    /// 07 §4 里不用解压就能查的：文件白名单、单个文件和总大小上限；格式 2 的包里不能有没被动作引用的 webm（防夹带）。
    /// 引用了但包里没有的片段在加载时报（<see cref="ActionManifest"/>）。
    /// </summary>
    private static void CheckEntries(ZipArchive zip, int format)
    {
        var referenced = format >= 2 ? ReferencedClips(zip) : null;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) continue;
            if (!IsAllowed(name))
                throw new InvalidDataException($"角色包里有不认识的文件：{name}");
            var limit = name.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? MaxWebmBytes
                : name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MaxPngBytes
                : MaxOtherBytes;
            if (entry.Length > limit)
                throw new InvalidDataException($"角色包里的文件太大：{name}（上限 {limit / 1024 / 1024} MB）");
            total += entry.Length;
            if (total > MaxExtractedBytes)
                throw new InvalidDataException("角色包解压后太大（上限 1 GB）");
            if (referenced is not null && name.StartsWith("clips/", StringComparison.Ordinal) && !referenced.Contains(name))
                throw new InvalidDataException($"角色包里有没被任何动作引用的片段：{name}");
        }
    }

    private static HashSet<string> ReferencedClips(ZipArchive zip)
    {
        using var stream = zip.GetEntry("pet.json")!.Open();
        using var doc = JsonDocument.Parse(stream);
        var clips = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            foreach (var action in actions.EnumerateArray())
                if (action.ValueKind == JsonValueKind.Object)
                {
                    clips.UnionWith(ActionManifest.Strings(action, "clips"));
                    // 分段动作的 enter / loop / exit 也算引用。
                    if (action.TryGetProperty("phases", out var phases) && phases.ValueKind == JsonValueKind.Object)
                        foreach (var phase in phases.EnumerateObject())
                            if (phase.Value.ValueKind == JsonValueKind.String) clips.Add(phase.Value.GetString()!);
                }
        return clips;
    }

    /// <summary>
    /// pet.json、preview.png、icon.png、LICENSE.txt；格式 1 的 videos/*.json、videos/&lt;分类&gt;/*.webm；
    /// 格式 2 的 clips/&lt;动作 id&gt;/*.webm、source/ 下的 png 和 studio.json。
    /// </summary>
    internal static bool IsAllowed(string name)
    {
        if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".."))
            return false;
        return name.Split('/') switch
        {
            ["pet.json"] or ["preview.png"] or ["icon.png"] or ["LICENSE.txt"] => true,
            ["clips", var action, var file] => ActionDef.IsValidId(action)
                                               && file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase),
            ["source", "studio.json"] => true,
            ["source", var file] => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            ["source", "keyframes", var file] => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            ["videos", var file] => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
            ["videos", var category, var file] => category.All(char.IsAsciiLetterLower)
                                                  && file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
