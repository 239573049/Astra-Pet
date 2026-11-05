using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraPet.Core;

/// <summary>对包里一个动作的改动，没写的字段不改。不能改片段和步态。</summary>
public sealed class ActionOverride
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("roles")] public List<string>? Roles { get; set; }
    [JsonPropertyName("ambient")] public double? Ambient { get; set; }
    [JsonPropertyName("cooldown")] public double? Cooldown { get; set; }
    [JsonPropertyName("lines")] public List<string>? Lines { get; set; }
    [JsonPropertyName("mirror")] public bool? Mirror { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Name is null && Description is null && Roles is null && Ambient is null && Cooldown is null
                           && Lines is null && Mirror is null && Enabled is null;
}

/// <summary>用户新增的动作，片段在覆盖层目录里。</summary>
public sealed class AddedAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    /// <summary>相对覆盖层目录：clips/&lt;id&gt;/a.webm。</summary>
    [JsonPropertyName("clips")] public List<string> Clips { get; set; } = new();
    [JsonPropertyName("ambient")] public double Ambient { get; set; } = ActionOverlay.DefaultAddedAmbient;
    [JsonPropertyName("cooldown")] public double Cooldown { get; set; }
    [JsonPropertyName("lines")] public List<string> Lines { get; set; } = new();
    [JsonPropertyName("mirror")] public bool Mirror { get; set; } = true;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>
/// 用户覆盖层：<c>&lt;数据目录&gt;/pets/&lt;宠物 key&gt;/actions.user.json</c>。角色包只读，用户对动作的改动、新增的动作、
/// 只对这只宠物生效的触发规则都存这里，按包 id 关联，包升级后仍然有效。设计见 01-action-model.md §4。
/// </summary>
public sealed class ActionOverlay
{
    public const string FileName = "actions.user.json";
    public const double DefaultAddedAmbient = 10;

    private static readonly JsonSerializerOptions Options = new(OverlayJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("overrides")] public Dictionary<string, ActionOverride> Overrides { get; set; } = new();
    [JsonPropertyName("added")] public List<AddedAction> Added { get; set; } = new();
    [JsonPropertyName("triggers")] public List<TriggerRule> Triggers { get; set; } = new();

    /// <summary>读覆盖层；没有或坏了返回空的（坏文件留一份 .bad）。</summary>
    public static ActionOverlay Load(string directory)
    {
        var file = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize(File.ReadAllText(file), (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ActionOverlay>)Options.GetTypeInfo(typeof(ActionOverlay))) is { } overlay)
            {
                overlay.Overrides ??= new();
                overlay.Added ??= new();
                overlay.Triggers ??= new();
                return overlay;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            PetLog.Write($"读取 {file} 失败，忽略用户的动作设置: {e.Message}");
            try { File.Copy(file, file + ".bad", overwrite: true); } catch { }
        }
        return new ActionOverlay();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, FileName);
        var temp = file + ".tmp";
        foreach (var key in Overrides.Where(o => o.Value.IsEmpty).Select(o => o.Key).ToList())
            Overrides.Remove(key);
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options.GetTypeInfo(typeof(ActionOverlay))));
        File.Move(temp, file, overwrite: true);
    }

    public ActionOverride OverrideFor(string id)
    {
        if (!Overrides.TryGetValue(id, out var o))
            Overrides[id] = o = new ActionOverride();
        return o;
    }

    /// <summary>
    /// 把覆盖层合并到包里的动作上。孤儿覆盖项（包里已经没有这个动作）忽略并记日志；
    /// 合并后某个必需角色没有启用的动作时，撤销涉及这个角色的覆盖（D14：五类必需始终可用）。
    /// </summary>
    /// <param name="directory">覆盖层目录，用户新增动作的片段在这里；null 时忽略新增的动作。</param>
    public List<ActionDef> Apply(IReadOnlyList<ActionDef> pack, string? directory, IClipDecoder decoder, Action<string>? log = null)
    {
        var ids = pack.Select(a => a.Id).ToHashSet();
        foreach (var orphan in Overrides.Keys.Where(k => !ids.Contains(k) && Added.All(a => a.Id != k)))
            log?.Invoke($"覆盖层里的动作 {orphan} 在角色包里已经没有了，忽略");

        var result = pack.Select(a => Overrides.TryGetValue(a.Id, out var o) ? Merge(a, o) : a).ToList();

        var fullDir = directory is null ? null : Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var added in Added)
        {
            if (directory is null || fullDir is null) break;
            try
            {
                var id = added.Id;
                if (!ActionDef.IsValidId(id)) throw new FormatException($"id 不合法：「{id}」");
                if (result.Any(a => a.Id == id))
                {
                    var renamed = id.Length <= 27 ? id + "-user" : id[..27] + "-user";
                    log?.Invoke($"用户动作 {id} 和角色包里的重名，改叫 {renamed}");
                    id = renamed;
                }
                var roles = added.Roles.Where(ActionDef.IsValidId).Distinct().ToList();
                if (roles.Count == 0) throw new FormatException($"{id} 没有角色");
                var clips = new List<PetClip>();
                foreach (var rel in added.Clips)
                {
                    var full = Path.GetFullPath(Path.Combine(directory, rel));
                    if (!full.StartsWith(fullDir, StringComparison.Ordinal) || !File.Exists(full))
                        throw new FormatException($"{id} 的片段不存在：{rel}");
                    clips.Add(decoder.Probe(full, roles[0]));
                }
                if (clips.Count == 0) throw new FormatException($"{id} 没有片段");
                var action = new ActionDef
                {
                    Id = id,
                    Name = added.Name,
                    Description = added.Description.Length <= ActionManifest.MaxDescription ? added.Description : added.Description[..ActionManifest.MaxDescription],
                    Roles = roles,
                    Clips = clips,
                    Ambient = Math.Max(0, added.Ambient),
                    Cooldown = TimeSpan.FromSeconds(Math.Max(0, added.Cooldown)),
                    Lines = ActionManifest.CleanLines(added.Lines),
                    Mirror = added.Mirror,
                    Origin = ActionOrigin.User,
                    Enabled = added.Enabled,
                };
                if (Overrides.TryGetValue(added.Id, out var o)) action = Merge(action, o);
                result.Add(action);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or IOException)
            {
                log?.Invoke($"跳过用户动作：{e.Message}");
            }
        }

        // 必需角色缺了：把包里原本有这个角色的动作恢复成包里的定义。
        foreach (var role in Roles.Required)
        {
            if (result.Any(a => a.Enabled && a.HasRole(role))) continue;
            log?.Invoke($"用户设置让「{role}」没有可用的动作了，恢复默认");
            for (var i = 0; i < pack.Count; i++)
                if (pack[i].HasRole(role))
                    result[i] = result[i] with { Enabled = true, Roles = result[i].Roles.Union([role]).ToList() };
        }
        return result;
    }

    private static ActionDef Merge(ActionDef a, ActionOverride o) => a with
    {
        Name = o.Name ?? a.Name,
        Description = o.Description is { } d ? (d.Length <= ActionManifest.MaxDescription ? d : d[..ActionManifest.MaxDescription]) : a.Description,
        Roles = o.Roles?.Where(ActionDef.IsValidId).Distinct().ToList() is { Count: > 0 } roles ? roles : a.Roles,
        Ambient = o.Ambient is { } w ? Math.Max(0, w) : a.Ambient,
        Cooldown = o.Cooldown is { } c ? TimeSpan.FromSeconds(Math.Max(0, c)) : a.Cooldown,
        Lines = o.Lines is { } lines ? ActionManifest.CleanLines(lines) : a.Lines,
        Mirror = o.Mirror ?? a.Mirror,
        Enabled = o.Enabled ?? a.Enabled,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActionOverlay))]
internal sealed partial class OverlayJsonContext : JsonSerializerContext;
