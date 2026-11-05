using AstraPet.Core;

namespace AstraPet.Infrastructure.Studio;

/// <summary>一段处理完的动画的质检结果（06 §4）。</summary>
public sealed record ClipReport
{
    public int Frames { get; init; }
    public double Fps { get; init; }
    public double LoopIou { get; init; }
    public double DriftX { get; init; }
    public int DriftFeet { get; init; }
    public double EdgeLeak { get; init; }
    public double Haze { get; init; }
    /// <summary>人物包围盒高度相对首帧的最大变化（比例）。</summary>
    public double ScaleChange { get; init; }
    /// <summary>人物几乎看不见（出画或被抠掉）的帧数。</summary>
    public int MissingFrames { get; init; }
    public string Source { get; init; } = "";
    public double Scale { get; init; }
    /// <summary>抠像色；源视频自带透明通道时为 null。</summary>
    public KeyColor? Key { get; init; }
    /// <summary>move 动作的步态曲线（每帧 0..1 的圈内进度）。</summary>
    public IReadOnlyList<double>? Curve { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>中间一帧（640×360 RGBA），给界面预览用。</summary>
    public byte[]? Preview { get; init; }
    /// <summary>每帧最左不透明列的中位数（贴墙姿态的接触线，design/pet-life/05 §1 ⑥）。</summary>
    public double? ContactX { get; init; }
    /// <summary>接触线逐帧的标准差（像素）。</summary>
    public double ContactSpread { get; init; }
    /// <summary>最后一帧最左不透明列（上墙过渡的终点接触线）。</summary>
    public double? LastContactX { get; init; }
    /// <summary>第一帧的不透明包围盒 [x0, y0, x1, y1]（站姿的身体范围）。</summary>
    public int[]? FirstBounds { get; init; }
}

/// <summary>
/// 逐帧累计质检指标（process.py 的 process() 里那部分，外加主体缺失和镜头变化），帧都是 640×360 RGBA。
/// </summary>
public sealed class QaMeter
{
    /// <summary>左右各多宽算「边缘」。</summary>
    public const int EdgeBand = 80;
    public const double MinLoopIou = 0.9, MaxDrift = 25, MaxEdgeLeak = 0.01, MaxHaze = 0.05, MaxScaleChange = 0.25;
    /// <summary>贴墙的接触线逐帧标准差超过这个像素算「手脚离开了墙」。</summary>
    public const double MaxContactSpread = 6;
    /// <summary>实心像素少于首帧的这个比例（且少于 <see cref="MinSolidPixels"/>）算主体缺失。</summary>
    private const double MissingRatio = 0.1;
    private const int MinSolidPixels = 200;

    private const int W = PetClip.Width, H = PetClip.Height;
    // 腿部区域：脚底往上 35% 身高，水平方向身高那么宽。
    private const int LegTop = PetClip.FeetY - (int)(0.35 * PetClip.BodyHeight), LegBottom = PetClip.FeetY + 1;
    private const int LegLeft = PetClip.CenterX - PetClip.BodyHeight / 2, LegRight = PetClip.CenterX + PetClip.BodyHeight / 2;

    private byte[]? _first, _prev;
    private double _x0;
    private int _firstHeight, _firstSolid;
    private readonly List<double> _legMotion = new();
    private readonly List<int> _solidCounts = new();
    private readonly List<int> _lefts = new();

    /// <summary>每帧最左的实心列（alpha &gt; 0.5）。</summary>
    public double? ContactX => _lefts.Count == 0 ? null : FramePlacerMedian(_lefts);
    public double ContactSpread
    {
        get
        {
            if (_lefts.Count < 2) return 0;
            var mean = _lefts.Average();
            return Math.Sqrt(_lefts.Average(v => (v - mean) * (v - mean)));
        }
    }
    public double? LastLeft => _lefts.Count == 0 ? null : _lefts[^1];
    public int[]? FirstBounds { get; private set; }

    private static double FramePlacerMedian(List<int> values) => FigureLocator.Median(values.Select(v => (double)v));

    public double DriftX { get; private set; }
    public int DriftFeet { get; private set; }
    public double EdgeLeak { get; private set; }
    public double Haze { get; private set; }
    public double ScaleChange { get; private set; }
    public int Frames => _legMotion.Count;
    public IReadOnlyList<double> LegMotion => _legMotion;

    public void Add(byte[] rgba)
    {
        var columns = new double[W];
        double total = 0;
        int solid = 0, top = -1, bottom = -1, haze = 0, leftLeak = 0, rightLeak = 0, leftmost = W, rightmost = -1;
        for (var y = 0; y < H; y++)
        {
            var rowSolid = false;
            for (var x = 0; x < W; x++)
            {
                var a = rgba[(y * W + x) * 4 + 3];
                if (a == 0) continue;
                var af = a / 255f;
                columns[x] += af;
                total += af;
                if (af > 0.5f)
                {
                    solid++;
                    rowSolid = true;
                    if (x < leftmost) leftmost = x;
                    if (x > rightmost) rightmost = x;
                }
                if (af < 0.1f) haze++;
                if (af > 0.1f)
                {
                    if (x < EdgeBand) leftLeak++;
                    else if (x >= W - EdgeBand) rightLeak++;
                }
            }
            if (!rowSolid) continue;
            if (top < 0) top = y;
            bottom = y;
        }

        if (rightmost >= 0)
        {
            _lefts.Add(leftmost);
            FirstBounds ??= [leftmost, top, rightmost, bottom];
        }
        var cx = total > 0 ? Weighted(columns, total) : PetClip.CenterX;
        if (_first is null)
        {
            _first = rgba;
            _x0 = cx;
            _firstHeight = solid > 0 ? bottom - top + 1 : 0;
            _firstSolid = solid;
        }
        if (solid > 0)
        {
            DriftX = Math.Max(DriftX, Math.Abs(cx - _x0));
            DriftFeet = Math.Max(DriftFeet, Math.Abs(bottom - PetClip.FeetY));
            if (_firstHeight > 0)
                ScaleChange = Math.Max(ScaleChange, Math.Abs((double)(bottom - top + 1) / _firstHeight - 1));
        }
        _solidCounts.Add(solid);
        EdgeLeak = Math.Max(EdgeLeak, ((double)leftLeak / (H * EdgeBand) + (double)rightLeak / (H * EdgeBand)) / 2);
        Haze = Math.Max(Haze, (double)haze / (W * H));

        double motion = 0;
        if (_prev is { } prev)
        {
            for (var y = LegTop; y < LegBottom; y++)
                for (var x = LegLeft; x < LegRight; x++)
                {
                    var i = (y * W + x) * 4 + 3;
                    motion += Math.Abs(rgba[i] / 255f - prev[i] / 255f);
                }
            motion /= (LegBottom - LegTop) * (LegRight - LegLeft);
        }
        _legMotion.Add(motion);
        _prev = rgba;
    }

    private static double Weighted(double[] weights, double total)
    {
        double sum = 0;
        for (var x = 0; x < weights.Length; x++) sum += x * weights[x];
        return sum / total;
    }

    /// <summary>首尾帧轮廓的交并比：首帧 alpha &gt; 128，末帧 alpha &gt; 0.5。</summary>
    public double LoopIou()
    {
        if (_first is null || _prev is null) return 0;
        long both = 0, either = 0;
        for (var i = 3; i < _first.Length; i += 4)
        {
            bool a = _first[i] > 128, b = _prev[i] / 255f > 0.5f;
            if (a && b) both++;
            if (a || b) either++;
        }
        return (double)both / Math.Max(either, 1);
    }

    public int MissingFrames()
    {
        var threshold = Math.Min(MinSolidPixels, _firstSolid * MissingRatio);
        return _solidCounts.Count(c => c < Math.Max(threshold, 1));
    }

    /// <summary>
    /// 腿部轮廓帧间变化 → 圈内累计进度 0..1（process.py 的 gait_curve）。窗口外和腿不动（小于中位数 20%）的帧不推进。
    /// </summary>
    public static IReadOnlyList<double> GaitCurve(IReadOnlyList<double> legMotion, double fps, double windowStart, double windowEnd)
    {
        var n = legMotion.Count;
        if (n == 0) return [];
        int lo = Math.Clamp((int)(windowStart * fps), 0, n), hi = Math.Clamp((int)(windowEnd * fps), lo, n);
        var step = new double[n];
        if (hi > lo)
        {
            var floor = 0.2 * FigureLocator.Median(legMotion.Skip(lo).Take(hi - lo));
            for (var i = lo; i < hi; i++) step[i] = legMotion[i] > floor ? legMotion[i] : 0;
        }
        if (step.Sum() == 0)
            for (var i = lo; i < hi; i++) step[i] = 1;
        var sum = step.Sum();
        var curve = new double[n];
        double acc = 0;
        for (var i = 0; i < n; i++)
        {
            acc += step[i];
            curve[i] = sum > 0 ? Math.Round(acc / sum, 4, MidpointRounding.ToEven) : 0;
        }
        curve[0] = 0;
        return curve;
    }

    /// <summary>超阈值的项，写成给用户看的提示和建议。</summary>
    public static List<string> Warnings(ClipReport r, bool isMove, bool isDrag)
    {
        var list = new List<string>();
        if (r.MissingFrames > 0)
            list.Add($"有 {r.MissingFrames} 帧几乎看不到人物（出画或被抠掉了），建议重新生成或换个抠像色");
        if (r.LoopIou < MinLoopIou)
            list.Add($"首尾帧轮廓重合度 {r.LoopIou:0.00}，循环衔接时会跳；可以重新生成，提示词里强调首尾同一姿势");
        if (!isMove && r.DriftX > MaxDrift)
            list.Add($"人物在画面里横向漂了 {r.DriftX:0} px，建议重新生成，提示词里强调「原地」");
        if (r.EdgeLeak > MaxEdgeLeak)
            list.Add($"画面两侧有 {r.EdgeLeak:0.0%} 的非透明像素，可能背景没抠干净或有道具出界");
        if (r.Haze > MaxHaze)
            list.Add($"{r.Haze:0%} 的像素几乎透明但不为零，背景没抠干净，会像一层雾");
        if (!isDrag && r.ScaleChange > MaxScaleChange)
            list.Add($"人物大小变化了 {r.ScaleChange:0%}，可能镜头推近或拉远了");
        return list;
    }
}
