using AstraPet.Core;

namespace AstraPet.Infrastructure.Studio;

/// <summary>人物在一帧里的范围（源视频坐标）：头顶、脚底、最左、最右、水平中心（alpha &gt; 128 的像素）。</summary>
public readonly record struct FigureBox(int Top, int Bottom, int Left, int Right, double CenterX)
{
    public int Height => Bottom - Top + 1;
    public int Width => Right - Left + 1;
}

/// <summary>站姿包围盒（06 §2.2，process.py 的 standing_box）：前几帧各量一次，取中位数。</summary>
public static class FigureLocator
{
    public const int StandingFrames = 5;

    /// <summary>一帧 RGBA 里人物的范围；整帧透明时返回 null。</summary>
    public static FigureBox? Measure(ReadOnlySpan<byte> rgba, int w, int h)
    {
        int top = -1, bottom = -1, left = w, right = -1;
        var columns = new long[w];
        long count = 0;
        for (var y = 0; y < h; y++)
        {
            var row = rgba.Slice(y * w * 4, w * 4);
            var any = false;
            for (var x = 0; x < w; x++)
            {
                if (row[x * 4 + 3] <= 128) continue;
                any = true;
                columns[x]++;
                count++;
                if (x < left) left = x;
                if (x > right) right = x;
            }
            if (!any) continue;
            if (top < 0) top = y;
            bottom = y;
        }
        if (count == 0) return null;
        return new FigureBox(top, bottom, left, right, MedianIndex(columns, count));
    }

    /// <summary>按列计数求所有像素 x 坐标的中位数（和 numpy.median 一样，偶数个取中间两个的平均）。</summary>
    private static double MedianIndex(long[] counts, long total)
    {
        long Nth(long n)
        {
            long seen = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen > n) return i;
            }
            return counts.Length - 1;
        }
        return total % 2 == 1 ? Nth(total / 2) : (Nth(total / 2 - 1) + Nth(total / 2)) / 2.0;
    }

    /// <summary>几帧的中位数站姿。没有一帧量得到人物时返回 null。</summary>
    public static FigureBox? Standing(IReadOnlyList<FigureBox?> frames)
    {
        var boxes = frames.Where(b => b is not null).Select(b => b!.Value).ToList();
        if (boxes.Count == 0) return null;
        return new FigureBox(
            (int)Median(boxes.Select(b => (double)b.Top)),
            (int)Median(boxes.Select(b => (double)b.Bottom)),
            (int)Median(boxes.Select(b => (double)b.Left)),
            (int)Median(boxes.Select(b => (double)b.Right)),
            Median(boxes.Select(b => b.CenterX)));
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>站姿 → 缩放和平移：身高 270、脚底 y = 330、水平居中；太宽的话按宽度再缩。</summary>
public readonly record struct Placement(double Scale, int Dx, int Dy, bool NarrowedForWidth)
{
    /// <summary>人物最宽能占多少：左右各留 80px（边缘残留的检测区）。</summary>
    public const int MaxFigureWidth = PetClip.Width - 2 * QaMeter.EdgeBand;

    /// <summary>
    /// 按画面锁定（design/pet-life/05 §1 ⑥）：不量人物，按首尾帧合成时的版式整体缩放平移——
    /// 画面高 65% 对应身高 270、85% 的地面线对齐脚底 y=330、画面中线对齐画布中线。
    /// 贴墙、坐、睡和过渡动作用它：这些姿势的包围盒和站姿不一样，按人物量会忽大忽小，换姿态时跳。
    /// </summary>
    public static Placement FrameLocked(int w, int h)
    {
        var scale = PetClip.BodyHeight / (h * (FigureTools.Feet - FigureTools.Head));
        return new Placement(scale,
            (int)Math.Round(PetClip.CenterX - w / 2.0 * scale),
            (int)Math.Round(PetClip.FeetY - h * FigureTools.Feet * scale),
            false);
    }

    public static Placement For(FigureBox standing)
    {
        var scale = (double)PetClip.BodyHeight / standing.Height;
        var narrowed = false;
        if (standing.Width * scale > MaxFigureWidth)
        {
            scale = (double)MaxFigureWidth / standing.Width;
            narrowed = true;
        }
        // Python 的 round 是四舍六入五成双，Math.Round 默认也是。
        return new Placement(scale,
            (int)Math.Round(PetClip.CenterX - standing.CenterX * scale),
            (int)Math.Round(PetClip.FeetY - standing.Bottom * scale),
            narrowed);
    }
}

/// <summary>把抠好的帧缩放后贴到 640×360 透明画布上（process.py 的 place）：预乘 alpha 下 Lanczos 缩放，边缘不发灰发黑。</summary>
public static class FramePlacer
{
    /// <summary>
    /// 缩放后贴到画布上，返回新的 640×360 RGBA。<paramref name="rgba"/> 会被就地改成预乘 alpha，
    /// 调用方之后不能再用它（处理流水线里每帧只用一次，省一次整帧拷贝）。
    /// </summary>
    public static byte[] Place(byte[] rgba, int w, int h, Placement placement)
    {
        var outW = (int)Math.Round(w * placement.Scale);
        var outH = (int)Math.Round(h * placement.Scale);
        var premultiplied = rgba;
        Lanczos.Premultiply(premultiplied);
        var scaled = Lanczos.Resize(premultiplied, w, h, outW, outH);
        Lanczos.Unpremultiply(scaled);

        var canvas = new byte[PetClip.FrameBytes];
        var x0 = Math.Max(0, placement.Dx);
        var x1 = Math.Min(PetClip.Width, placement.Dx + outW);
        if (x1 <= x0) return canvas;
        for (var y = Math.Max(0, placement.Dy); y < Math.Min(PetClip.Height, placement.Dy + outH); y++)
        {
            var sy = y - placement.Dy;
            Buffer.BlockCopy(scaled, (sy * outW + (x0 - placement.Dx)) * 4, canvas, (y * PetClip.Width + x0) * 4, (x1 - x0) * 4);
        }
        return canvas;
    }
}
