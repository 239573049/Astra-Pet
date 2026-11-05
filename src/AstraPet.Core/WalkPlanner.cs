namespace AstraPet.Core;

/// <summary>一圈走路：起点、方向（-1 左 / +1 右）、这一圈走多少桌面像素、按哪条步态曲线走。</summary>
public sealed record Walk(int StartX, int Direction, double Distance, Gait Gait)
{
    /// <summary>
    /// 第 <paramref name="frame"/> 帧时窗口左上角的 X。位置只由帧号决定：起点 + 距离 × 曲线进度，
    /// 不累加每帧位移，取整误差不会攒起来。
    /// </summary>
    public int XAt(int frame, int totalFrames) =>
        StartX + Direction * (int)Math.Round(Distance * Gait.ProgressAt(frame, totalFrames));
}

/// <summary>
/// 开始走一圈前定方向和距离，照原版 choose_move_direction：两边都够走一圈就随机挑，
/// 只有一边够就往那边，都不够就往空间大的一边走、距离缩到剩余空间。
/// 纯计算，屏幕几何（可走范围、缩放）由窗口算好传进来。
/// </summary>
public static class WalkPlanner
{
    /// <param name="x">窗口左上角当前的 X（桌面像素）。</param>
    /// <param name="minX">左上角 X 的活动范围下限（已经扣掉边缘余量）。</param>
    /// <param name="maxX">左上角 X 的活动范围上限。</param>
    /// <param name="stride">这一圈完整走下来的距离（桌面像素，已经乘过缩放）。</param>
    public static Walk Plan(int x, int minX, int maxX, double stride, Gait gait, Random random) =>
        Plan(x, minX, maxX, stride, gait, random, null);

    /// <param name="target">
    /// 目标位置（窗口左上角 X）：主动靠近光标、陪玩、去屏幕边（design/pet-life/04 §6）。朝它走，到了就停；null 随机走。
    /// </param>
    public static Walk Plan(int x, int minX, int maxX, double stride, Gait gait, Random random, int? target)
    {
        double Room(int dir) => dir < 0 ? x - minX : maxX - x;

        if (target is { } t)
        {
            var goal = Math.Clamp(t, minX, maxX);
            var dir = goal < x ? -1 : 1;
            return new Walk(x, dir, Math.Min(stride, Math.Abs(goal - x)), gait);
        }

        var direction = (Room(-1) >= stride, Room(1) >= stride) switch
        {
            (true, true) => random.Next(2) == 0 ? -1 : 1,
            (true, false) => -1,
            (false, true) => 1,
            _ => Room(-1) >= Room(1) ? -1 : 1,
        };
        return new Walk(x, direction, Math.Clamp(Room(direction), 0, stride), gait);
    }
}
