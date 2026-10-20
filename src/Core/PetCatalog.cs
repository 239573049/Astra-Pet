using System.Text.Json;

namespace AstraPet.Core;

/// <summary>
/// 一个可选的角色。<see cref="Id"/> 是它在角色目录下的文件夹名，存进设置里；内置角色的 Id 是空串。
/// <see cref="Problem"/> 不为空说明素材不全，不能用，内容是给用户看的原因。
/// </summary>
public sealed record PetEntry(string Id, string Name, string VideosDir, string? Problem)
{
    public bool IsBuiltIn => Id.Length == 0;

    public override string ToString() => Name;
}

/// <summary>
/// 角色目录：数据目录下的 characters 文件夹，每个子文件夹是一个角色，和仓库里的 characters/ 同样的布局：
/// <c>&lt;角色&gt;/videos/{idle,click,drag,move,turn}/*.webm</c>（没有 videos 子文件夹时，分类文件夹直接放在角色文件夹里也行）。
/// 显示名取 character.json 的 name，没有就用文件夹名。这里只看目录结构，不起 ffmpeg 探测，
/// 所以扫描很快；真正加载时才会发现的问题（尺寸不对、没有 alpha）在切换时报。
/// </summary>
public static class PetCatalog
{
    public const string BuiltInName = "默认角色";

    public static string Directory { get; } = Path.Combine(SettingsStore.Directory, "characters");

    /// <summary>内置角色排第一，后面是角色目录下的文件夹，按名字排。目录不存在就只有内置的。</summary>
    public static List<PetEntry> Scan(string builtInVideosDir)
    {
        var pets = new List<PetEntry> { new("", BuiltInName, builtInVideosDir, null) };
        if (!System.IO.Directory.Exists(Directory)) return pets;

        foreach (var dir in System.IO.Directory.EnumerateDirectories(Directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(dir);
            if (id.StartsWith('.')) continue;
            var videos = System.IO.Directory.Exists(Path.Combine(dir, "videos")) ? Path.Combine(dir, "videos") : dir;
            pets.Add(new PetEntry(id, ReadName(dir) ?? id, videos, Check(videos)));
        }
        return pets;
    }

    /// <summary>
    /// 启动时加载设置里选的角色。它被删了、素材坏了就退回内置角色，并把设置改回内置，
    /// 免得每次启动都失败一遍；内置角色加载失败直接抛异常。
    /// </summary>
    public static PetLibrary LoadSelected(PetSettings settings, string builtInVideosDir, string ffmpeg)
    {
        if (settings.PetId.Length > 0)
        {
            try
            {
                var entry = Scan(builtInVideosDir).FirstOrDefault(p => p.Id == settings.PetId)
                            ?? throw new DirectoryNotFoundException($"{Directory} 下没有这个文件夹");
                if (entry.Problem is { } problem) throw new InvalidOperationException(problem);
                PetLog.Write($"角色: {entry.Name} ({entry.VideosDir})");
                return PetLibrary.Load(entry.VideosDir, ffmpeg, PetLog.Write);
            }
            catch (Exception e)
            {
                PetLog.Write($"角色「{settings.PetId}」加载失败，改用{BuiltInName}: {e.Message}");
                settings.PetId = "";
            }
        }
        PetLog.Write($"角色: {BuiltInName} ({builtInVideosDir})");
        return PetLibrary.Load(builtInVideosDir, ffmpeg, PetLog.Write);
    }

    /// <summary>必需的分类里有没有 webm。全有返回 null，否则返回缺哪些。</summary>
    private static string? Check(string videosDir)
    {
        var missing = PetLibrary.Required.Where(c => !HasClips(Path.Combine(videosDir, c))).ToList();
        return missing.Count == 0 ? null : $"缺少 {string.Join("、", missing)} 的 webm";
    }

    private static bool HasClips(string dir) =>
        System.IO.Directory.Exists(dir) && System.IO.Directory.EnumerateFiles(dir, "*.webm").Any();

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
