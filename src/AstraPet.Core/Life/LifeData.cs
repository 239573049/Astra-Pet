using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraPet.Core;

/// <summary>
/// 一只宠物的数值和动态档案（design/pet-life/02、06 §2），存在 <c>&lt;数据目录&gt;/pets/&lt;petKey&gt;/life.json</c>。
/// 只在 UI 线程上读写；计算规则在 <see cref="LifeRules"/>。
/// </summary>
public sealed class LifeData
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }
    [JsonPropertyName("adoptedAt")] public DateTimeOffset AdoptedAt { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }

    [JsonPropertyName("satiety")] public double Satiety { get; set; } = 80;
    [JsonPropertyName("energy")] public double Energy { get; set; } = 90;
    [JsonPropertyName("mood")] public double Mood { get; set; } = 10;

    [JsonPropertyName("xp")] public double Xp { get; set; }
    [JsonPropertyName("todayXp")] public double TodayXp { get; set; }
    /// <summary>yyyy-MM-dd（本地日期），跨天时 <see cref="TodayXp"/> 清零。</summary>
    [JsonPropertyName("today")] public string Today { get; set; } = "";

    [JsonPropertyName("lastInteractionAt")] public DateTimeOffset LastInteractionAt { get; set; }
    /// <summary>用户在电脑前、却超过 2 小时没理它，累计了多少小时（有互动清零）。</summary>
    [JsonPropertyName("neglectHours")] public double NeglectHours { get; set; }
    /// <summary>今天对话已经计过经验的轮数。</summary>
    [JsonPropertyName("todayChats")] public int TodayChats { get; set; }

    [JsonPropertyName("snacks")] public Dictionary<string, int> Snacks { get; set; } = new();
    [JsonPropertyName("cooldowns")] public Dictionary<string, DateTimeOffset> Cooldowns { get; set; } = new();
    [JsonPropertyName("stats")] public Dictionary<string, long> Stats { get; set; } = new();
    [JsonPropertyName("traitOverrides")] public Dictionary<string, double> TraitOverrides { get; set; } = new();

    // 阈值状态（带回差，见 02 §3.3），存下来免得重启后重复提醒。
    [JsonPropertyName("hungry")] public bool Hungry { get; set; }
    [JsonPropertyName("starvingAt")] public DateTimeOffset? StarvingAt { get; set; }
    [JsonPropertyName("sleepy")] public bool Sleepy { get; set; }

    public long Stat(string key) => Stats.GetValueOrDefault(key);
    public void Count(string key, long by = 1) => Stats[key] = Stat(key) + by;
    public int SnackCount => Snacks.Values.Sum();

    public LifeData Clone()
    {
        var json = JsonSerializer.Serialize(this, LifeJsonContext.Default.LifeData);
        return JsonSerializer.Deserialize(json, LifeJsonContext.Default.LifeData)!;
    }

    /// <summary>新宠物：今天是第一天。</summary>
    public static LifeData New(DateTimeOffset now) => new() { AdoptedAt = now, SavedAt = now, LastInteractionAt = now };
}

/// <summary>累计数据的 key（控制台「累计」一行）。</summary>
public static class LifeStats
{
    public const string Petted = "petted";
    public const string Clicked = "clicked";
    public const string Fed = "fed";
    public const string Chats = "chats";
    public const string FocusMinutes = "focusMinutes";
    public const string Thrown = "thrown";
    public const string Climbs = "climbs";
    public const string Played = "played";
}

/// <summary>life.json 的读写：写临时文件再原子替换；坏了备份一份并从新开始。</summary>
public static class LifeStore
{
    public const string FileName = "life.json";

    private static readonly JsonSerializerOptions Options = new(LifeJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读存档；没有返回 null，坏了（或版本更高）备份成 life.json.bad-&lt;时间&gt; 并返回 null。</summary>
    public static LifeData? Load(string directory, Action<string>? log = null)
    {
        var file = Path.Combine(directory, FileName);
        if (!File.Exists(file)) return null;
        try
        {
            var data = JsonSerializer.Deserialize(File.ReadAllText(file),
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LifeData>)Options.GetTypeInfo(typeof(LifeData)));
            if (data is null) throw new JsonException("空文件");
            if (data.Version > LifeData.CurrentVersion) throw new JsonException($"版本 {data.Version} 比程序新");
            data.Snacks ??= new();
            data.Cooldowns ??= new();
            data.Stats ??= new();
            data.TraitOverrides ??= new();
            data.Today ??= "";
            return data;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"读取 {file} 失败，宠物状态从头开始: {e.Message}");
            try { File.Copy(file, $"{file}.bad-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); } catch { }
            return null;
        }
    }

    public static void Save(string directory, LifeData data, Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, FileName);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options.GetTypeInfo(typeof(LifeData))));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"保存宠物状态失败: {e.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LifeData))]
internal sealed partial class LifeJsonContext : JsonSerializerContext;
