namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 和 Pillow 的 <c>ImageFilter.GaussianBlur</c> 逐字节一致的模糊（libImaging/BoxBlur.c）：
/// 三遍带小数半径的盒式模糊近似高斯，先横向三遍再纵向三遍，24 位定点数，边缘像素向外延伸。
/// 抠像的边缘取色用它，和 tools/petgen/process.py 的结果对得上。
/// </summary>
public static class BoxBlur
{
    private const int Passes = 3;

    /// <summary>就地模糊交错存放的 8 位图像（<paramref name="channels"/> 个通道各自模糊）。</summary>
    public static void Gaussian(byte[] pixels, int w, int h, int channels, double radius)
    {
        var r = BoxRadius((float)radius, Passes);
        if (r == 0) return;
        var line = new byte[Math.Max(w, h)];
        var output = new byte[Math.Max(w, h)];
        for (var c = 0; c < channels; c++)
        {
            // 横向：每行三遍。
            for (var y = 0; y < h; y++)
            {
                var start = y * w * channels + c;
                for (var x = 0; x < w; x++) line[x] = pixels[start + x * channels];
                for (var p = 0; p < Passes; p++)
                {
                    Line(line, output, w, r);
                    Array.Copy(output, line, w);
                }
                for (var x = 0; x < w; x++) pixels[start + x * channels] = line[x];
            }
            // 纵向：每列三遍。
            for (var x = 0; x < w; x++)
            {
                for (var y = 0; y < h; y++) line[y] = pixels[(y * w + x) * channels + c];
                for (var p = 0; p < Passes; p++)
                {
                    Line(line, output, h, r);
                    Array.Copy(output, line, h);
                }
                for (var y = 0; y < h; y++) pixels[(y * w + x) * channels + c] = line[y];
            }
        }
    }

    /// <summary>高斯半径 → 每遍盒式模糊的半径（Gwosdek 等，扩展盒式滤波）。</summary>
    internal static float BoxRadius(float radius, int passes)
    {
        var sigma2 = radius * radius / passes;
        var L = (float)Math.Sqrt(12.0 * sigma2 + 1.0);
        var l = (float)Math.Floor((L - 1.0) / 2.0);
        var a = (2 * l + 1) * (l * (l + 1) - 3 * sigma2);
        a /= 6 * (sigma2 - (l + 1) * (l + 1));
        return l + a;
    }

    private static void Line(byte[] input, byte[] output, int size, float floatRadius)
    {
        var radius = (int)floatRadius;
        var ww = (uint)((1 << 24) / (floatRadius * 2 + 1));
        var fw = (uint)(((1 << 24) - (radius * 2 + 1) * ww) / 2);
        var lastx = size - 1;
        var edgeA = Math.Min(radius + 1, size);
        var edgeB = Math.Max(size - radius - 1, 0);

        uint acc = (uint)(input[0] * (radius + 1));
        for (var x = 0; x < edgeA - 1; x++) acc += input[x];
        acc = (uint)(acc + input[lastx] * (radius - edgeA + 1));

        if (edgeA <= edgeB)
        {
            for (var x = 0; x < edgeA; x++) Step(input, output, ref acc, ww, fw, x, 0, x + radius, 0, x + radius + 1);
            for (var x = edgeA; x < edgeB; x++) Step(input, output, ref acc, ww, fw, x, x - radius - 1, x + radius, x - radius - 1, x + radius + 1);
            for (var x = edgeB; x <= lastx; x++) Step(input, output, ref acc, ww, fw, x, x - radius - 1, lastx, x - radius - 1, lastx);
        }
        else
        {
            for (var x = 0; x < edgeB; x++) Step(input, output, ref acc, ww, fw, x, 0, x + radius, 0, lastx);
            for (var x = edgeB; x < edgeA; x++) Step(input, output, ref acc, ww, fw, x, 0, lastx, 0, lastx);
            for (var x = edgeA; x <= lastx; x++) Step(input, output, ref acc, ww, fw, x, x - radius - 1, lastx, x - radius - 1, lastx);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Step(byte[] input, byte[] output, ref uint acc, uint ww, uint fw, int x, int sub, int add, int left, int right)
    {
        acc = acc + input[add] - input[sub];
        var bulk = acc * ww + (uint)(input[left] + input[right]) * fw;
        output[x] = (byte)((bulk + (1u << 23)) >> 24);
    }
}
