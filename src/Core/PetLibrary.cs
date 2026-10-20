using System.Text.Json;

namespace AstraPet.Core;

/// <summary>
/// 角色素材库。目录结构与原版一致：videos/{idle,click,drag,move,turn}/*.webm，
/// 另有 move_strides.json（移动动画每圈步幅和逐帧位移曲线）和
/// text_clips.json（带文字、朝右时不能镜像的片段）。
/// 这里只存元数据，帧在播放时由 <see cref="ClipStream"/> 解码。
/// </summary>
public sealed class PetLibrary
{
    public const string Idle = "idle";
    public const string Click = "click";
    public const string Drag = "drag";
    public const string Move = "move";
    public const string Turn = "turn";
    /// <summary>唱歌动画，可选：没有这个目录时听歌只播动作片段。</summary>
    public const string Sing = "sing";

    private static readonly string[] Categories = [Idle, Click, Drag, Move, Turn, Sing];
    private static readonly string[] Optional = [Sing];

    /// <summary>必须有素材的分类，缺一个就加载不了。</summary>
    public static IEnumerable<string> Required => Categories.Except(Optional);

    private readonly Dictionary<string, List<PetClip>> _byCategory = new();
    private readonly Dictionary<string, Gait> _gaits = new();
    private readonly HashSet<string> _noMirror = new();
    private readonly string _ffmpeg;

    private PetLibrary(string ffmpeg) => _ffmpeg = ffmpeg;

    public int ClipCount => _byCategory.Values.Sum(l => l.Count);

    public IReadOnlyList<PetClip> this[string category] => _byCategory.GetValueOrDefault(category) ?? [];

    public bool Has(string category) => _byCategory.ContainsKey(category);

    public PetClip Pick(string category, Random random)
    {
        // 可选分类缺素材时用动作片段代替。
        var clips = _byCategory.GetValueOrDefault(category) ?? _byCategory[Click];
        return clips[random.Next(clips.Count)];
    }

    /// <summary>移动动画的步态，没标定的片段返回 null（只播动画，不移动窗口）。</summary>
    public Gait? GaitOf(PetClip clip) => _gaits.GetValueOrDefault(clip.Name);

    /// <summary>带文字的片段镜像后字是反的，朝右时也按原样画。</summary>
    public bool CanMirror(PetClip clip) => !_noMirror.Contains(clip.Name);

    public ClipStream Open(PetClip clip) => new(clip, _ffmpeg);

    public static PetLibrary Load(string videosDir, string ffmpeg, Action<string>? log = null)
    {
        var lib = new PetLibrary(ffmpeg);
        foreach (var category in Categories)
        {
            var clips = new List<PetClip>();
            var dir = Path.Combine(videosDir, category);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.webm").OrderBy(f => f))
                {
                    var clip = PetClip.Probe(file, category, ffmpeg);
                    log?.Invoke($"{category}/{clip.Name}: {clip.Fps:0.##} fps, 约 {clip.EstimatedFrames} 帧");
                    clips.Add(clip);
                }
            }
            if (clips.Count == 0 && Optional.Contains(category))
                continue;
            if (clips.Count == 0)
                throw new InvalidOperationException($"分类 {category} 里没有可用的 webm: {dir}");
            lib._byCategory[category] = clips;
        }

        var stridesFile = Path.Combine(videosDir, "move_strides.json");
        if (File.Exists(stridesFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stridesFile));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 文件里还有 _comment 之类的字符串字段，只认对象。
                if (prop.Value.ValueKind != JsonValueKind.Object
                    || !prop.Value.TryGetProperty("stride", out var stride))
                    continue;
                var curve = prop.Value.TryGetProperty("curve", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(v => v.GetDouble()).ToArray()
                    : null;
                lib._gaits[prop.Name] = new Gait(stride.GetDouble(), curve);
            }
        }

        var textFile = Path.Combine(videosDir, "text_clips.json");
        if (File.Exists(textFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(textFile));
            if (doc.RootElement.TryGetProperty("no_mirror", out var names))
                foreach (var name in names.EnumerateArray())
                    if (name.GetString() is { } s)
                        lib._noMirror.Add(s);
        }
        return lib;
    }
}

/// <summary>
/// 移动动画的步态：一圈（整段片段）在素材原寸下前进 <see cref="Stride"/> 像素。
/// curve[i] 是播到第 i 帧时这一圈完成的比例，动帧段上升、静帧段走平，照着走就不会滑步。
/// </summary>
public sealed class Gait(double stride, double[]? curve)
{
    public double Stride { get; } = stride;

    /// <summary>
    /// 第 frame 帧时这一圈完成的比例，0..1。片段帧数和曲线点数不一致时按比例插值，
    /// 和原版 movement.move_position_at_frame 的取法一致；没有曲线就按帧数线性推进。
    /// </summary>
    public double ProgressAt(int frame, int totalFrames)
    {
        if (curve is not { Length: > 0 })
            return totalFrames <= 1 ? 1 : Math.Clamp((double)frame / (totalFrames - 1), 0, 1);

        var last = curve.Length - 1;
        var pos = Math.Clamp(frame * (double)last / Math.Max(1, totalFrames - 1), 0, last);
        var lo = (int)pos;
        var hi = Math.Min(lo + 1, last);
        return curve[lo] + (curve[hi] - curve[lo]) * (pos - lo);
    }
}
