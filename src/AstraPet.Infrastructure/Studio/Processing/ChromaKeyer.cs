namespace AstraPet.Infrastructure.Studio;

/// <summary>抠像色：视频模型生成时用的纯色背景（04 §3 ③），或者导入时从四角采样判断出来的。</summary>
public enum KeyColor { Green, Blue, Magenta }

/// <summary>
/// 纯色背景帧 → RGBA（06 §2.1），照搬 tools/petgen/process.py 的 key()，把绿幕推广到三种抠像色：
/// 「抠像色通道超出其余通道的量」约等于 1 - 覆盖率，据此算 alpha；实心像素去溢色；半透明边缘用附近实心像素的颜色；
/// 附近没有实心像素的半透明特效按「观测 = a·前景 + (1-a)·背景」反解；全透明处颜色清零。
/// </summary>
public static class ChromaKeyer
{
    public const float AlphaLo = 0.12f, AlphaHi = 0.85f;
    /// <summary>和 numpy 一样先用双精度算差再转单精度。</summary>
    private const float AlphaRange = (float)(0.85 - 0.12);
    /// <summary>边缘取色时实心区颜色往外扩散的半径（源视频像素）。</summary>
    public const double EdgeRadius = 2;

    /// <summary>抠像色通道超出其余通道的量（未截断）。</summary>
    private static float Excess(KeyColor key, float r, float g, float b) => key switch
    {
        KeyColor.Green => g - MathF.Max(r, b),
        KeyColor.Blue => b - MathF.Max(r, g),
        _ => MathF.Min(r, b) - g,
    };

    /// <summary>去溢色：把超出量从抠像色通道里减掉（绿幕时就是绿不超过红蓝的较大值）。</summary>
    private static void Despill(KeyColor key, ref float r, ref float g, ref float b)
    {
        switch (key)
        {
            case KeyColor.Green: g = MathF.Min(g, MathF.Max(r, b)); break;
            case KeyColor.Blue: b = MathF.Min(b, MathF.Max(r, g)); break;
            default:
                var e = MathF.Max(MathF.Min(r, b) - g, 0);
                r -= e; b -= e;
                break;
        }
    }

    private static float Clip01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>抠一帧。<paramref name="rgb"/> 是 rgb24（w×h×3），返回 RGBA（w×h×4）。</summary>
    public static byte[] Key(ReadOnlySpan<byte> rgb, int w, int h, KeyColor key)
    {
        var n = w * h;
        var f = new float[n * 3];
        for (var i = 0; i < f.Length; i++) f[i] = rgb[i] / 255f;

        var excess = new float[n];
        for (var i = 0; i < n; i++)
            excess[i] = Clip01(Excess(key, f[i * 3], f[i * 3 + 1], f[i * 3 + 2]));

        // 生成的背景不一定是纯色（有的偏暗，超出量只有 0.86 左右）：按这一帧背景实际的超出量归一化，否则会残留一层雾。
        var sample = new List<float>();
        var sampled = 0;
        for (var y = 0; y < h; y += 4)
            for (var x = 0; x < w; x += 4)
            {
                sampled++;
                var e = excess[y * w + x];
                if (e > 0.4f) sample.Add(e);
            }
        if (sample.Count > sampled * 0.2)
        {
            var median = Median(sample);
            for (var i = 0; i < n; i++) excess[i] = Clip01(excess[i] / median);
        }

        var alpha = new float[n];
        var color = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            alpha[i] = Clip01((1 - excess[i] - AlphaLo) / AlphaRange);
            float r = f[i * 3], g = f[i * 3 + 1], b = f[i * 3 + 2];
            Despill(key, ref r, ref g, ref b);
            color[i * 3] = r; color[i * 3 + 1] = g; color[i * 3 + 2] = b;
        }

        FixEdges(f, excess, alpha, color, w, h, key);

        var output = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            var visible = alpha[i] > 0;
            output[i * 4] = visible ? ToByte(color[i * 3]) : (byte)0;
            output[i * 4 + 1] = visible ? ToByte(color[i * 3 + 1]) : (byte)0;
            output[i * 4 + 2] = visible ? ToByte(color[i * 3 + 2]) : (byte)0;
            output[i * 4 + 3] = ToByte(alpha[i]);
        }
        return output;
    }

    private static byte ToByte(float v) => (byte)(Clip01(v) * 255 + 0.5f);

    /// <summary>和 numpy.median 一样：偶数个取中间两个的平均。</summary>
    internal static float Median(List<float> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static void FixEdges(float[] f, float[] excess, float[] alpha, float[] color, int w, int h, KeyColor key)
    {
        var n = w * h;
        bool anySolid = false, anyEdge = false;
        for (var i = 0; i < n && !(anySolid && anyEdge); i++)
        {
            if (alpha[i] >= 0.95f) anySolid = true;
            else if (alpha[i] > 0) anyEdge = true;
        }
        if (!anySolid || !anyEdge) return;

        // 半透明边缘是前景和背景混出来的，只去溢色会发暗：把实心区的颜色往外扩散几个像素，用它当边缘的颜色。
        // 和 PIL 一样先量化成 8 位再模糊。
        var mask = new byte[n];
        var solidColor = new byte[n * 3];
        for (var i = 0; i < n; i++)
        {
            if (alpha[i] < 0.95f) continue;
            mask[i] = 255;
            for (var c = 0; c < 3; c++) solidColor[i * 3 + c] = (byte)(color[i * 3 + c] * 255);
        }
        BoxBlur.Gaussian(mask, w, h, 1, EdgeRadius);
        BoxBlur.Gaussian(solidColor, w, h, 3, EdgeRadius);

        float[]? keyRgb = null;
        for (var i = 0; i < n; i++)
        {
            var a = alpha[i];
            if (a <= 0 || a >= 0.95f) continue;
            var m = mask[i] / 255f;
            if (m > 0.02f)
            {
                var d = MathF.Max(m, 1e-3f);
                for (var c = 0; c < 3; c++) color[i * 3 + c] = Clip01(solidColor[i * 3 + c] / 255f / d);
                continue;
            }

            // 附近没有实心像素的半透明区域（淡入淡出的爱心、光点）：反解前景色。
            keyRgb ??= KeyRgb(f, excess, key);
            var aLin = MathF.Max(1 - excess[i], 0.05f);
            float r = (f[i * 3] - (1 - aLin) * keyRgb[0]) / aLin;
            float g = (f[i * 3 + 1] - (1 - aLin) * keyRgb[1]) / aLin;
            float b = (f[i * 3 + 2] - (1 - aLin) * keyRgb[2]) / aLin;
            Despill(key, ref r, ref g, ref b);
            color[i * 3] = Clip01(r); color[i * 3 + 1] = Clip01(g); color[i * 3 + 2] = Clip01(b);
        }
    }

    /// <summary>背景的平均颜色（超出量 ≥ 0.95 的像素）；没有就用纯色。</summary>
    private static float[] KeyRgb(float[] f, float[] excess, KeyColor key)
    {
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < excess.Length; i++)
        {
            if (excess[i] < 0.95f) continue;
            r += f[i * 3]; g += f[i * 3 + 1]; b += f[i * 3 + 2];
            count++;
        }
        if (count > 0) return [(float)(r / count), (float)(g / count), (float)(b / count)];
        return key switch
        {
            KeyColor.Green => [0, 1, 0],
            KeyColor.Blue => [0, 0, 1],
            _ => [1, 0, 1],
        };
    }

    /// <summary>
    /// 从四角各取一块判断背景色（06 §3.2）。返回 null 表示不是纯色背景（实拍、复杂背景不支持）。
    /// </summary>
    public static KeyColor? Detect(ReadOnlySpan<byte> rgb, int w, int h)
    {
        var size = Math.Max(4, Math.Min(w, h) / 20);
        double r = 0, g = 0, b = 0;
        var count = 0;
        foreach (var (x0, y0) in new[] { (0, 0), (w - size, 0), (0, h - size), (w - size, h - size) })
            for (var y = y0; y < y0 + size; y++)
                for (var x = x0; x < x0 + size; x++)
                {
                    var i = (y * w + x) * 3;
                    r += rgb[i]; g += rgb[i + 1]; b += rgb[i + 2];
                    count++;
                }
        r /= count * 255.0; g /= count * 255.0; b /= count * 255.0;
        const double margin = 0.25;
        if (g - Math.Max(r, b) > margin) return KeyColor.Green;
        if (b - Math.Max(r, g) > margin) return KeyColor.Blue;
        if (Math.Min(r, b) - g > margin) return KeyColor.Magenta;
        return null;
    }
}
