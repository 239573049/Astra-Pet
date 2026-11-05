using AstraPet.Platform;
namespace AstraPet.Core;

/// <summary>桌面坐标里的矩形（像素），左上角 + 宽高。</summary>
public readonly record struct DesktopRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool ContainsX(double x) => x >= X && x < Right;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 其他应用窗口顶边上能站的一段（design/pet-life/04 §7）：没被更前面的窗口挡住的部分。
/// <paramref name="WindowX"/> / <paramref name="WindowId"/> 用来在窗口移动时让站在上面的宠物跟着走。
/// </summary>
public readonly record struct Ledge(long WindowId, double X0, double X1, double Y, double WindowX)
{
    public bool Contains(double x) => x >= X0 && x < X1;
}

/// <summary>
/// 表面几何（design/pet-life/04 §4.1）：每块屏幕的工作区给出地面（底边）、墙（左右边）、天花板（顶边）。
/// 两块屏幕相邻的那条边不是墙。纯几何，坐标是桌面像素，由窗口层在启动和屏幕变化时重建。
/// </summary>
public sealed class SurfaceMap
{
    private const double Touch = 2;   // 两块屏幕的边相差这么多以内算相邻

    public SurfaceMap(IEnumerable<DesktopRect> workAreas, int primary = 0, IEnumerable<Ledge>? ledges = null)
    {
        Screens = workAreas.Where(r => r.Width > 0 && r.Height > 0).ToList();
        if (Screens.Count == 0) Screens = [new DesktopRect(0, 0, 1440, 900)];
        Primary = Screens[Math.Clamp(primary, 0, Screens.Count - 1)];
        _primaryIndex = Math.Clamp(primary, 0, Screens.Count - 1);
        Ledges = ledges?.ToList() ?? [];
    }

    private readonly int _primaryIndex;

    public IReadOnlyList<DesktopRect> Screens { get; }
    public DesktopRect Primary { get; }
    /// <summary>窗口顶边（站在窗口上关着时为空）。</summary>
    public IReadOnlyList<Ledge> Ledges { get; }

    /// <summary>同样的屏幕，换一组窗口顶边。</summary>
    public SurfaceMap WithLedges(IEnumerable<Ledge> ledges) => new(Screens, _primaryIndex, ledges);

    /// <summary>
    /// 从可见窗口（最前的在前）算出能站的顶边段：太小的窗口不算；顶边贴着屏幕顶（最大化、全屏）的不算——那是天花板；
    /// 被更前面的窗口挡住的部分去掉；剩下太短的段也不要。
    /// </summary>
    public IReadOnlyList<Ledge> LedgesFrom(IReadOnlyList<DesktopWindow> windows, double minWidth)
    {
        var result = new List<Ledge>();
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w.Width < minWidth || w.Height < 60) continue;
            var screen = ScreenAt(w.X + w.Width / 2, w.Y + 1);
            if (w.Y <= screen.Y + 4 || w.Y >= screen.Bottom - 8) continue;
            var segments = new List<(double A, double B)> { (Math.Max(w.X, screen.X), Math.Min(w.X + w.Width, screen.Right)) };
            for (var j = 0; j < i; j++)
            {
                var f = windows[j];
                if (f.Y > w.Y + 2 || f.Y + f.Height < w.Y - 2) continue;   // 挡不住这条边
                var next = new List<(double, double)>();
                foreach (var (a, b) in segments)
                {
                    if (f.X + f.Width <= a || f.X >= b) { next.Add((a, b)); continue; }
                    if (f.X > a) next.Add((a, f.X));
                    if (f.X + f.Width < b) next.Add((f.X + f.Width, b));
                }
                segments = next;
            }
            foreach (var (a, b) in segments)
                if (b - a >= minWidth / 2) result.Add(new Ledge(w.Id, a, b, w.Y, w.X));
        }
        return result;
    }

    /// <summary>脚底在 <paramref name="feet"/>、横坐标 <paramref name="x"/> 处踩着的窗口顶边；不在窗口上返回 null。</summary>
    public Ledge? LedgeAt(double x, double feet)
    {
        foreach (var l in Ledges)
            if (l.Contains(x) && Math.Abs(l.Y - feet) <= Touch) return l;
        return null;
    }

    /// <summary>包含这个点的屏幕；不在任何屏幕里时取最近的。</summary>
    public DesktopRect ScreenAt(double x, double y)
    {
        foreach (var s in Screens)
            if (s.Contains(x, y)) return s;
        return Screens.MinBy(s => Distance(s, x, y));
    }

    /// <summary>这块屏幕的左 / 右边是不是墙（另一侧没有紧挨着、上下有重叠的屏幕）。</summary>
    public bool IsWall(DesktopRect screen, int side) => !Screens.Any(o => o != screen
        && (side < 0 ? Math.Abs(o.Right - screen.X) <= Touch : Math.Abs(o.X - screen.Right) <= Touch)
        && o.Y < screen.Bottom && o.Bottom > screen.Y);

    /// <summary>横坐标 <paramref name="x"/> 处、在 <paramref name="y"/> 以下（含）最近的地面；那一列没有屏幕时返回 null。</summary>
    public double? FloorBelow(double x, double y)
    {
        double? best = null;
        foreach (var s in Screens)
            if (s.ContainsX(x) && s.Bottom >= y - Touch && (best is null || s.Bottom < best))
                best = s.Bottom;
        if (best is null) return null;   // 那一列没有屏幕：窗口顶边也不算
        foreach (var l in Ledges)
            if (l.Contains(x) && l.Y >= y - Touch && l.Y < best)
                best = l.Y;
        return best;
    }

    /// <summary>所有屏幕的外接矩形，用来防止宠物飞出去。</summary>
    public DesktopRect Bounds
    {
        get
        {
            var x = Screens.Min(s => s.X);
            var y = Screens.Min(s => s.Y);
            return new DesktopRect(x, y, Screens.Max(s => s.Right) - x, Screens.Max(s => s.Bottom) - y);
        }
    }

    private static double Distance(DesktopRect s, double x, double y)
    {
        var dx = Math.Max(0, Math.Max(s.X - x, x - s.Right));
        var dy = Math.Max(0, Math.Max(s.Y - y, y - s.Bottom));
        return dx * dx + dy * dy;
    }
}
