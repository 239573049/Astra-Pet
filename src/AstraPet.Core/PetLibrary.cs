using System.Text.Json;
using AstraPet.Ai;

namespace AstraPet.Core;

/// <summary>
/// 角色素材库：一只宠物的全部动作（<see cref="Actions"/>）和片段元数据。帧在播放时由 <see cref="IClipDecoder"/> 打开的帧源解码。
/// 两种布局：
/// <list type="bullet">
/// <item>格式 1（原版）：videos/{idle,click,drag,move,turn,sing}/*.webm，另有 move_strides.json（步态）和 text_clips.json（不能镜像的片段），
/// 每个片段合成一个动作（<see cref="ActionManifest.FromCategories"/>）；</item>
/// <item>格式 2：根目录的 pet.json 里有 actions，片段在 clips/ 下（<see cref="ActionManifest.Parse"/>）。</item>
/// </list>
/// </summary>
public sealed class PetLibrary
{
    public const string Idle = Roles.Idle;
    public const string Click = Roles.Click;
    public const string Drag = Roles.Drag;
    public const string Move = Roles.Move;
    public const string Turn = Roles.Turn;
    /// <summary>唱歌动画，可选：没有时听歌按回退链播 happy / click。</summary>
    public const string Sing = Roles.Sing;

    private static readonly string[] Categories = [Idle, Click, Drag, Move, Turn, Sing];
    private static readonly string[] Optional = [Sing];

    /// <summary>必须有动作的角色，缺一个就加载不了。</summary>
    public static IEnumerable<string> Required => Roles.Required;

    private readonly IClipDecoder _decoder;
    private Dictionary<PetClip, ActionDef> _byClip = new();

    private PetLibrary(IClipDecoder decoder, List<ActionDef> packActions, int format)
    {
        _decoder = decoder;
        Format = format;
        PackActions = packActions;
        Actions = new ActionSet(packActions);
        Stances = new StanceGraph(Actions.All);
        Index();
    }

    /// <summary>姿态图：哪些姿态可用、怎么在姿态之间走（design/pet-life/04 §3）。动作表变了会重建。</summary>
    public StanceGraph Stances { get; private set; }

    /// <summary>宠物档案（design/pet-life/01）：格式 2 的 pet.json 的 profile，没有就是默认值。</summary>
    public PetProfile Profile => _profile ??= PetProfile.Parse(Manifest, PetLog.Write);

    private PetProfile? _profile;

    /// <summary>覆盖层（actions.user.json）被丢掉的姿态：控制台提示缺什么。</summary>
    public IReadOnlyList<string> PrunedStances { get; private set; } = [];

    /// <summary>角色包里的定义，没合并覆盖层。控制台「恢复默认」和重新合并用。</summary>
    public IReadOnlyList<ActionDef> PackActions { get; }

    /// <summary>用户覆盖层的目录（<c>&lt;数据目录&gt;/pets/&lt;key&gt;</c>）；null 表示不支持自定义（测试、开发素材）。</summary>
    public string? OverlayDirectory { get; private set; }

    /// <summary>当前合并进来的覆盖层。改完调 <see cref="ApplyOverlay"/>。</summary>
    public ActionOverlay Overlay { get; private set; } = new();

    /// <summary>包里的定义合并用户覆盖层之后的动作。</summary>
    public ActionSet Actions { get; private set; }

    /// <summary>1 是分类文件夹，2 是 pet.json 里的 actions。</summary>
    public int Format { get; }

    /// <summary>格式 2 的 pet.json 根元素（人设等以后用），格式 1 为 null。</summary>
    public JsonElement? Manifest { get; private init; }

    public int ClipCount => Actions.All.Sum(a => a.Clips.Count);

    /// <summary>角色的显示名（目录里的名字），由 <see cref="PetCatalog"/> 设置。</summary>
    public string Name { get => _name; set { _name = value ?? ""; _persona = null; } }

    private string _name = "";

    /// <summary>角色人设：格式 2 的 pet.json 里有 persona 就用它，否则是用显示名的通用人设。</summary>
    public PetPersona Persona =>
        _persona ??= Manifest is { } m && PetPersona.Parse(m) is { } p ? p : PetPersona.Generic(Name);

    private PetPersona? _persona;

    /// <summary>有这个角色的启用动作的全部片段。</summary>
    public IReadOnlyList<PetClip> this[string role] => Actions.WithRole(role).SelectMany(a => a.Clips).ToList();

    public bool Has(string role) => Actions.HasRole(role);

    /// <summary>按角色（走回退链）挑一个动作，再挑一个片段。找不到时用 click。</summary>
    public PetClip Pick(string role, Random random) =>
        PickClip(Actions.PickByRole(role, random) ?? Actions.PickByRole(Click, random)!, random);

    /// <summary>动作只有一个片段时不消耗随机数，格式 1 的随机序列和 0.1.4 一致。</summary>
    public static PetClip PickClip(ActionDef action, Random random) =>
        action.Clips.Count == 1 ? action.Clips[0] : action.PickClip(random);

    /// <summary>片段属于哪个动作；不是这个库的片段返回 null。</summary>
    public ActionDef? ActionOf(PetClip clip) => _byClip.GetValueOrDefault(clip);

    /// <summary>移动动画的步态，没标定的片段返回 null（只播动画，不移动窗口）。</summary>
    public Gait? GaitOf(PetClip clip) => ActionOf(clip)?.Gait;

    /// <summary>带文字的片段镜像后字是反的，朝右时也按原样画。</summary>
    public bool CanMirror(PetClip clip) => ActionOf(clip)?.Mirror ?? true;

    public IFrameSource Open(PetClip clip) => _decoder.Open(clip);

    /// <summary>动作表变了（用户改了覆盖层），控制台、Agent 的动作清单据此刷新。</summary>
    public event Action? ActionsChanged;

    /// <summary>
    /// 合并覆盖层，<paramref name="save"/> 为真时同时存盘。合并结果的必需角色总是齐的（<see cref="ActionOverlay.Apply"/> 会兜底）。
    /// </summary>
    public void ApplyOverlay(ActionOverlay overlay, bool save = true, Action<string>? log = null)
    {
        var merged = overlay.Apply(PackActions, OverlayDirectory, _decoder, log ?? PetLog.Write);
        var pruned = new List<string>();
        var set = new ActionSet(StanceGraph.Prune(merged, m => { pruned.Add(m); (log ?? PetLog.Write)(m); }));
        if (set.MissingRequired() is { Count: > 0 } missing)
            throw new InvalidOperationException($"缺少 {string.Join("、", missing)} 的动作");
        Overlay = overlay;
        Actions = set;
        Stances = new StanceGraph(set.All);
        PrunedStances = pruned;
        Index();
        if (save && OverlayDirectory is not null) overlay.Save(OverlayDirectory);
        ActionsChanged?.Invoke();
    }

    private void Index()
    {
        var map = new Dictionary<PetClip, ActionDef>();   // PetClip 没重写 Equals，按引用
        foreach (var action in Actions.All)
        {
            foreach (var clip in action.Clips)
                map.TryAdd(clip, action);
            if (action.Phases is { } p)
                foreach (var clip in new[] { p.Enter, p.Loop, p.Exit })
                    map.TryAdd(clip, action);
        }
        _byClip = map;
    }

    /// <summary>
    /// 加载一只宠物。<paramref name="dir"/> 是格式 1 的 videos 目录，或者格式 2 的根目录（有 pet.json）；
    /// 格式 1 的包解压后 videos 的上一级也有 pet.json（format 1），那种按格式 1 读。
    /// <paramref name="overlayDirectory"/> 不为空时读那里的用户覆盖层并合并。
    /// </summary>
    public static PetLibrary Load(string dir, IClipDecoder decoder, Action<string>? log = null, string? overlayDirectory = null)
    {
        var library = LoadPack(dir, decoder, log);
        if (overlayDirectory is not null)
        {
            library.OverlayDirectory = overlayDirectory;
            var overlay = ActionOverlay.Load(overlayDirectory);
            library.ApplyOverlay(overlay, save: false, log);
        }
        return library;
    }

    private static PetLibrary LoadPack(string dir, IClipDecoder decoder, Action<string>? log)
    {
        if (FindManifest(dir) is { } found)
        {
            using var doc = found.Doc;
            var root = doc.RootElement;
            var actions = ActionManifest.Parse(
                root.TryGetProperty("actions", out var a) ? a : default, found.Root, decoder, log);
            return Build(decoder, actions, 2, root.Clone());
        }
        return Build(decoder, LoadFormat1(dir, decoder, log), 1, null);
    }

    private static PetLibrary Build(IClipDecoder decoder, List<ActionDef> actions, int format, JsonElement? manifest)
    {
        actions = StanceGraph.Prune(actions, PetLog.Write);
        if (new ActionSet(actions).MissingRequired() is { Count: > 0 } missing)
            throw new InvalidOperationException($"缺少 {string.Join("、", missing)} 的动作");
        return new PetLibrary(decoder, actions, format) { Manifest = manifest };
    }

    /// <summary>dir 或它的上一级有 format ≥ 2 的 pet.json 时返回它。</summary>
    private static (string Root, JsonDocument Doc)? FindManifest(string dir)
    {
        foreach (var root in new[] { dir, Path.GetDirectoryName(Path.GetFullPath(dir)) })
        {
            if (root is null) continue;
            var file = Path.Combine(root, "pet.json");
            if (!File.Exists(file)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var f) && f.TryGetInt32(out var format) && format >= 2)
                return (root, doc);
            doc.Dispose();
        }
        return null;
    }

    private static List<ActionDef> LoadFormat1(string videosDir, IClipDecoder decoder, Action<string>? log)
    {
        var categories = new List<(string, IReadOnlyList<PetClip>)>();
        foreach (var category in Categories)
        {
            var clips = new List<PetClip>();
            var dir = Path.Combine(videosDir, category);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.webm").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var clip = decoder.Probe(file, category);
                    log?.Invoke($"{category}/{clip.Name}: {clip.Fps:0.##} fps, 约 {clip.EstimatedFrames} 帧");
                    clips.Add(clip);
                }
            }
            if (clips.Count == 0 && Optional.Contains(category))
                continue;
            if (clips.Count == 0)
                throw new InvalidOperationException($"分类 {category} 里没有可用的 webm: {dir}");
            categories.Add((category, clips));
        }

        var gaits = new Dictionary<string, Gait>();
        var stridesFile = Path.Combine(videosDir, "move_strides.json");
        if (File.Exists(stridesFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stridesFile));
            foreach (var prop in doc.RootElement.EnumerateObject())
                // 文件里还有 _comment 之类的字符串字段，只认对象。
                if (ActionManifest.ParseGait(prop.Value) is { } gait)
                    gaits[prop.Name] = gait;
        }

        var noMirror = new HashSet<string>();
        var textFile = Path.Combine(videosDir, "text_clips.json");
        if (File.Exists(textFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(textFile));
            if (doc.RootElement.TryGetProperty("no_mirror", out var names))
                foreach (var name in names.EnumerateArray())
                    if (name.GetString() is { } s)
                        noMirror.Add(s);
        }
        return ActionManifest.FromCategories(categories, gaits, noMirror);
    }
}

public enum GaitAxis { X, Y }

/// <summary>
/// 移动动画的步态：一圈（整段片段）在素材原寸下前进 <see cref="Stride"/> 像素。
/// curve[i] 是播到第 i 帧时这一圈完成的比例，动帧段上升、静帧段走平，照着走就不会滑步。
/// </summary>
public sealed class Gait(double stride, double[]? curve, GaitAxis axis = GaitAxis.X)
{
    public double Stride { get; } = stride;

    /// <summary>窗口沿哪个方向走：地面上走路是 X，墙上攀爬是 Y（design/pet-life/04 §3.2）。</summary>
    public GaitAxis Axis { get; } = axis;

    /// <summary>逐帧曲线，可能为 null（按帧数线性推进）。</summary>
    public IReadOnlyList<double>? Curve => curve;

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
