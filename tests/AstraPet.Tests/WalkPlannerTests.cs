using AstraPet.Core;

namespace AstraPet.Tests;

public class WalkPlannerTests
{
    private static readonly Gait Linear = new(stride: 240, curve: null);

    [Fact]
    public void Room_on_both_sides_picks_either_direction_with_full_stride()
    {
        var directions = new HashSet<int>();
        var random = new Random(7);
        for (var i = 0; i < 50; i++)
        {
            var walk = WalkPlanner.Plan(x: 500, minX: 0, maxX: 1000, stride: 200, Linear, random);
            directions.Add(walk.Direction);
            Assert.Equal(200, walk.Distance);
        }
        Assert.Equal([-1, 1], directions.Order());
    }

    [Theory]
    [InlineData(100, 1)]    // 左边只剩 100，右边够：往右
    [InlineData(900, -1)]   // 右边只剩 100，左边够：往左
    public void Only_one_side_has_room(int x, int expected)
    {
        var walk = WalkPlanner.Plan(x, minX: 0, maxX: 1000, stride: 200, Linear, new Random(1));
        Assert.Equal(expected, walk.Direction);
        Assert.Equal(200, walk.Distance);
    }

    [Fact]
    public void Neither_side_has_room_goes_to_the_bigger_side_and_shortens()
    {
        // 左边 60、右边 90，一圈要 200：往右走，只走 90。
        var walk = WalkPlanner.Plan(x: 60, minX: 0, maxX: 150, stride: 200, Linear, new Random(1));
        Assert.Equal(1, walk.Direction);
        Assert.Equal(90, walk.Distance);
    }

    [Fact]
    public void Window_beyond_bounds_never_walks_backwards()
    {
        // 窗口被拖到了范围外：剩余空间是负的，距离夹到 0，原地播完。
        var walk = WalkPlanner.Plan(x: -50, minX: 0, maxX: 0, stride: 200, Linear, new Random(1));
        Assert.True(walk.Distance >= 0);
    }

    [Fact]
    public void Position_follows_curve_from_start_to_end()
    {
        var walk = new Walk(StartX: 1000, Direction: -1, Distance: 300, Linear);
        Assert.Equal(1000, walk.XAt(0, 11));
        Assert.Equal(850, walk.XAt(5, 11));
        Assert.Equal(700, walk.XAt(10, 11));
        Assert.Equal(700, walk.XAt(50, 11));   // 超出估算帧数（流式解码事先不知道确切帧数）也停在终点
    }

    [Fact]
    public void Flat_curve_segments_hold_position()
    {
        // 前半段原地（转身），后半段才往前走：曲线走平的帧位置不变，不会滑步。
        var gait = new Gait(240, [0, 0, 0, 0.5, 1]);
        var walk = new Walk(0, 1, 100, gait);
        Assert.Equal(0, walk.XAt(0, 5));
        Assert.Equal(0, walk.XAt(2, 5));
        Assert.Equal(50, walk.XAt(3, 5));
        Assert.Equal(100, walk.XAt(4, 5));
    }
}
