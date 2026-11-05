using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Infrastructure.Studio;

/// <summary>包里的一个动作：一个片段（工作台每个动作只采用一个 take）。</summary>
public sealed record PackAction(
    string Id, string Name, string Description, IReadOnlyList<string> Roles, string ClipFile,
    IReadOnlyList<string> Lines, bool Mirror, double? Stride = null, IReadOnlyList<double>? Curve = null)
{
    /// <summary>姿态（design/pet-life/04 §3.2）；地面不写。</summary>
    public string Stance { get; init; } = Stances.Ground;
    /// <summary>过渡动作的终点姿态。</summary>
    public string? To { get; init; }
    /// <summary>贴墙的接触线（画布坐标），写成 anchor.contactX。</summary>
    public double? ContactX { get; init; }
    /// <summary>步态沿 Y 轴（攀爬）。</summary>
    public bool Vertical { get; init; }
}

/// <summary>要写进 .astrapet 的全部内容（07 §1、§2）。</summary>
public sealed record PackSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string License { get; init; } = "";
    public string LicenseUrl { get; init; } = "";
    public string Homepage { get; init; } = "";
    public IReadOnlyList<string> Credits { get; init; } = [];
    public string MinAppVersion { get; init; } = "";
    public JsonObject? Persona { get; init; }
    /// <summary>自定义提示词（Markdown 全文）；非空时写成包根目录的 <see cref="PromptFile"/>，pet.json 的 prompt 指向它。</summary>
    public string? PromptMarkdown { get; init; }
    /// <summary>档案（design/pet-life/01）：性格、身体、物理、口味。</summary>
    public JsonObject? Profile { get; init; }
    public required IReadOnlyList<PackAction> Actions { get; init; }
    public byte[]? Preview { get; init; }
    public byte[]? Icon { get; init; }
    public string? LicenseText { get; init; }
    /// <summary>source/ 下的文件（相对 source/ 的路径 → 内容），可选。</summary>
    public IReadOnlyDictionary<string, byte[]> Sources { get; init; } = new Dictionary<string, byte[]>();
}

/// <summary>
/// 写格式 2 的角色包，规则同 tools/pack_pet.py：webm 和 png 不压缩，json 用 deflate；条目按名字排序、时间戳固定，
/// 同样的输入打出逐字节相同的包。先写 .part 再改名。
/// </summary>
public static class PetPackWriter
{
    public const string PromptFile = "prompt.md";

    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样写，方便人看
    };

    public static JsonObject Manifest(PackSpec spec)
    {
        var actions = new JsonArray();
        foreach (var a in spec.Actions)
        {
            var action = new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["description"] = a.Description,
                ["roles"] = new JsonArray(a.Roles.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
                ["clips"] = new JsonArray((JsonNode)JsonValue.Create(ClipEntry(a))),
            };
            if (!a.Mirror) action["mirror"] = false;
            if (a.To is { } to) action["transition"] = new JsonObject { ["from"] = a.Stance, ["to"] = to };
            else if (a.Stance != Stances.Ground) action["stance"] = a.Stance;
            if (a.ContactX is { } contact) action["anchor"] = new JsonObject { ["contactX"] = Math.Round(contact, 1) };
            if (a.Roles.Count == 0) action.Remove("roles");
            if (a.Lines.Count > 0) action["lines"] = new JsonArray(a.Lines.Select(l => (JsonNode)JsonValue.Create(l)).ToArray());
            if (a.Stride is { } stride)
            {
                var gait = new JsonObject { ["stride"] = Math.Round(stride, 1) };
                if (a.Curve is { Count: > 0 } curve)
                    gait["curve"] = new JsonArray(curve.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 4))).ToArray());
                if (a.Vertical) gait["axis"] = "y";
                action["gait"] = gait;
            }
            actions.Add((JsonNode)action);
        }
        var manifest = new JsonObject
        {
            ["format"] = 2,
            ["id"] = spec.Id,
            ["name"] = spec.Name,
            ["version"] = spec.Version,
            ["author"] = spec.Author,
            ["license"] = spec.License,
            ["licenseUrl"] = spec.LicenseUrl,
            ["homepage"] = spec.Homepage,
            ["credits"] = new JsonArray(spec.Credits.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["render"] = new JsonObject
            {
                ["width"] = PetClip.Width, ["height"] = PetClip.Height, ["bodyHeight"] = PetClip.BodyHeight, ["feetY"] = PetClip.FeetY,
            },
        };
        if (spec.MinAppVersion.Length > 0) manifest["minAppVersion"] = spec.MinAppVersion;
        if (spec.Persona is { } persona) manifest["persona"] = persona.DeepClone();
        if (spec.PromptMarkdown is { Length: > 0 }) manifest["prompt"] = PromptFile;
        if (spec.Profile is { } profile) manifest["profile"] = profile.DeepClone();
        manifest["actions"] = actions;
        return manifest;
    }

    private static string ClipEntry(PackAction a) => $"clips/{a.Id}/a.webm";

    public static void Write(PackSpec spec, string path)
    {
        var files = new SortedDictionary<string, (byte[] Data, bool Compress)>(StringComparer.Ordinal)
        {
            ["pet.json"] = (Encoding.UTF8.GetBytes(Manifest(spec).ToJsonString(Pretty) + "\n"), true),
        };
        if (spec.PromptMarkdown is { Length: > 0 } prompt) files[PromptFile] = (Encoding.UTF8.GetBytes(prompt), true);
        foreach (var a in spec.Actions) files[ClipEntry(a)] = (File.ReadAllBytes(a.ClipFile), false);
        if (spec.Preview is { } preview) files["preview.png"] = (preview, false);
        if (spec.Icon is { } icon) files["icon.png"] = (icon, false);
        if (spec.LicenseText is { Length: > 0 } license) files["LICENSE.txt"] = (Encoding.UTF8.GetBytes(license), true);
        foreach (var (name, data) in spec.Sources)
            files["source/" + name] = (data, !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var part = path + ".part";
        using (var stream = File.Create(part))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, (data, compress)) in files)
            {
                var entry = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                entry.LastWriteTime = FixedTime;
                using var s = entry.Open();
                s.Write(data);
            }
        }
        File.Move(part, path, overwrite: true);
    }
}
