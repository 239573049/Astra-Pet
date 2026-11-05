using AstraPet.Core;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// ③ 站姿与首尾帧（04 §3 ③）里不调模型的部分：选抠像色、把站姿图抠成透明图、检查、合成 1280×720 的首尾帧。
/// 规则照 tools/petgen/make_first_frame.py：头顶在 20%、脚底在 85%、水平居中。
/// </summary>
public static class FigureTools
{
    public const int FrameWidth = 1280, FrameHeight = 720;
    public const double Head = 0.20, Feet = 0.85;

    public static KeyColor ToKey(string key) => key switch { "blue" => KeyColor.Blue, "magenta" => KeyColor.Magenta, _ => KeyColor.Green };
    public static string FromKey(KeyColor key) => key switch { KeyColor.Blue => "blue", KeyColor.Magenta => "magenta", _ => "green" };

    /// <summary>
    /// 按设定图（白底或透明底）里角色的颜色选抠像色：把角色单独放到每种抠像色上真抠一遍，看角色内部被抠掉了多少，
    /// 依次试绿、蓝、品红，第一个几乎不伤角色的就用它；都不行选伤得最少的。绿色角色在绿幕上抠不干净就是这么避开的。
    /// </summary>
    public static (string Key, IReadOnlyDictionary<string, double> Damage) ChooseKey(RgbaImage sheet)
    {
        // 缩到长边 512 左右，够判断颜色，也快。
        var image = Shrink(sheet, 512);
        var (w, h) = (image.Width, image.Height);
        var subject = new bool[w * h];
        for (var i = 0; i < w * h; i++)
        {
            int r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2], a = image.Pixels[i * 4 + 3];
            var nearWhite = r > 225 && g > 225 && b > 225;
            subject[i] = a > 200 && !nearWhite;
        }
        // 只看角色内部（离边缘 2 像素以上），边缘本来就会和背景混合。
        var interior = new bool[w * h];
        var count = 0;
        for (var y = 2; y < h - 2; y++)
            for (var x = 2; x < w - 2; x++)
            {
                var i = y * w + x;
                if (subject[i] && subject[i - 2] && subject[i + 2] && subject[i - 2 * w] && subject[i + 2 * w])
                {
                    interior[i] = true;
                    count++;
                }
            }

        var damage = new Dictionary<string, double>();
        foreach (var key in StudioPrompts.KeyColors)
        {
            var (kr, kg, kb) = StudioPrompts.KeyRgb(key);
            var rgb = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++)
            {
                var s = subject[i];
                rgb[i * 3] = s ? image.Pixels[i * 4] : kr;
                rgb[i * 3 + 1] = s ? image.Pixels[i * 4 + 1] : kg;
                rgb[i * 3 + 2] = s ? image.Pixels[i * 4 + 2] : kb;
            }
            var keyed = ChromaKeyer.Key(rgb, w, h, ToKey(key));
            var eaten = 0;
            for (var i = 0; i < w * h; i++)
                if (interior[i] && keyed[i * 4 + 3] < 250) eaten++;
            damage[key] = count == 0 ? 0 : (double)eaten / count;
        }
        var chosen = StudioPrompts.KeyColors.FirstOrDefault(k => damage[k] < 0.005)
                     ?? damage.OrderBy(d => d.Value).First().Key;
        return (chosen, damage);
    }

    /// <summary>
    /// 站姿图抠成透明图：自带透明背景的直接用 alpha；否则按四角判断纯色背景再抠（优先用 <paramref name="key"/>）。
    /// 然后照 make_first_frame.py 清理 alpha：低于 16 归零、高于 248 补满，中间线性拉开。背景不是纯色时抛异常。
    /// </summary>
    public static RgbaImage Extract(RgbaImage figure, string key)
    {
        var (w, h) = (figure.Width, figure.Height);
        byte[] rgba;
        if (HasTransparentBorder(figure))
        {
            rgba = (byte[])figure.Pixels.Clone();
        }
        else
        {
            var rgb = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++)
            {
                rgb[i * 3] = figure.Pixels[i * 4];
                rgb[i * 3 + 1] = figure.Pixels[i * 4 + 1];
                rgb[i * 3 + 2] = figure.Pixels[i * 4 + 2];
            }
            var detected = ChromaKeyer.Detect(rgb, w, h)
                           ?? throw new InvalidDataException("站姿图的背景不是纯色，抠不出来。重新生成一张，或者上传透明底的 PNG");
            rgba = ChromaKeyer.Key(rgb, w, h, detected);
            _ = key;   // 模型偶尔不听话用了别的底色：以实际检测到的为准
        }
        for (var i = 3; i < rgba.Length; i += 4)
        {
            int v = rgba[i];
            rgba[i] = (byte)(v < 16 ? 0 : v > 248 ? 255 : (int)Math.Round((v - 16) * 255.0 / 232));
            if (rgba[i] == 0) rgba[i - 3] = rgba[i - 2] = rgba[i - 1] = 0;
        }
        return new RgbaImage(w, h, rgba);
    }

    private static bool HasTransparentBorder(RgbaImage image)
    {
        int clear = 0, total = 0;
        for (var x = 0; x < image.Width; x++)
            foreach (var y in new[] { 0, image.Height - 1 })
            {
                total++;
                if (image.Pixels[(y * image.Width + x) * 4 + 3] < 16) clear++;
            }
        return clear > total * 0.9;
    }

    /// <summary>alpha 大于 0 的外接框；全透明返回 null。</summary>
    public static (int Left, int Top, int Right, int Bottom)? Bounds(RgbaImage image, int threshold = 0)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image.Pixels[(y * image.Width + x) * 4 + 3] > threshold)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        return right < 0 ? null : (left, top, right, bottom);
    }

    /// <summary>
    /// 自动检查（04 §3 ③）：角色完整在画面内、只有一个主体。返回给人看的问题，空表示没问题。
    /// <paramref name="companion"/> 为真时允许两个主体（角色 + 伙伴）。
    /// </summary>
    public static List<string> Check(RgbaImage keyed, bool companion)
    {
        var problems = new List<string>();
        if (Bounds(keyed, 128) is not { } box)
        {
            problems.Add("没抠出角色：整张图都被当成了背景");
            return problems;
        }
        var margin = 2;
        if (box.Left < margin || box.Top < margin || box.Right >= keyed.Width - margin || box.Bottom >= keyed.Height - margin)
            problems.Add("角色碰到了画面边缘，可能没画全（要完整全身、四周留白）");

        var parts = Components(Shrink(keyed, 256));
        if (parts.Count > 0)
        {
            var largest = parts.Max();
            var big = parts.Count(p => p >= largest * 0.08);
            if (big > (companion ? 2 : 1))
                problems.Add(companion ? "画面里好像不止角色和伙伴" : "画面里好像不止一个角色（多余的东西也会出现在动画里）");
        }
        return problems;
    }

    /// <summary>alpha &gt; 128 的四连通区域面积。</summary>
    private static List<int> Components(RgbaImage image)
    {
        var (w, h) = (image.Width, image.Height);
        var seen = new bool[w * h];
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (seen[start] || image.Pixels[start * 4 + 3] <= 128) continue;
            var size = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                size++;
                int x = i % w, y = i / w;
                foreach (var n in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                    if (n >= 0 && !seen[n] && image.Pixels[n * 4 + 3] > 128)
                    {
                        seen[n] = true;
                        stack.Push(n);
                    }
            }
            sizes.Add(size);
        }
        return sizes;
    }

    /// <summary>
    /// 合成首尾帧：裁掉透明边，等比缩放到 65% 画面高（头顶 20%、脚底 85%），水平居中，铺在纯抠像色上。输出不透明。
    /// 太宽的角色（横向的动物）按 90% 画面宽缩，竖直方向仍然脚底对齐 85%。
    /// </summary>
    public static RgbaImage ComposeFirstFrame(RgbaImage keyed, string key) => Compose(keyed, key, Stances.Ground);

    /// <summary>贴墙关键帧里，角色最左侧落在画面宽度的这个比例处（给攀爬时手往外伸留余量）。</summary>
    public const double WallLeft = 0.22;

    /// <summary>倒挂关键帧里，角色顶边落在画面高度的这个比例处。</summary>
    public const double CeilingTop = 0.12;

    /// <summary>
    /// 合成某个姿态的首尾帧（design/pet-life/05 §1 ③）：站姿按 65% 画面高；坐、睡按站姿高度的比例缩（大概的身体比例），
    /// 贴墙和站姿一样高。脚底（姿势的最低点）都对齐 85%，这样换姿态时角色不会忽大忽小、上下跳；
    /// 贴墙的左缘对齐 <see cref="WallLeft"/>，其余水平居中。
    /// </summary>
    public static RgbaImage Compose(RgbaImage keyed, string key, string stance)
    {
        var box = Bounds(keyed) ?? throw new InvalidDataException("关键帧图是全透明的");
        var (bw, bh) = (box.Right - box.Left + 1, box.Bottom - box.Top + 1);
        var cropped = new byte[bw * bh * 4];
        for (var y = 0; y < bh; y++)
            Array.Copy(keyed.Pixels, ((box.Top + y) * keyed.Width + box.Left) * 4, cropped, y * bw * 4, bw * 4);

        var factor = stance switch { Stances.Sit => 0.72, Stances.Lie => 0.5, _ => 1.0 };
        var targetH = (int)Math.Round(FrameHeight * (Feet - Head) * factor);
        var scale = (double)targetH / bh;
        if (bw * scale > FrameWidth * 0.9) scale = FrameWidth * 0.9 / bw;
        var (w, h) = (Math.Max(1, (int)Math.Round(bw * scale)), Math.Max(1, (int)Math.Round(bh * scale)));

        Lanczos.Premultiply(cropped);
        var resized = Lanczos.Resize(cropped, bw, bh, w, h);   // 预乘的
        var (kr, kg, kb) = StudioPrompts.KeyRgb(key);
        var frame = new byte[FrameWidth * FrameHeight * 4];
        for (var i = 0; i < FrameWidth * FrameHeight; i++)
        {
            frame[i * 4] = kr;
            frame[i * 4 + 1] = kg;
            frame[i * 4 + 2] = kb;
            frame[i * 4 + 3] = 255;
        }
        var left = stance == Stances.Wall ? (int)Math.Round(FrameWidth * WallLeft) : (FrameWidth - w) / 2;
        // 倒挂：顶边对齐画面 12%（贴天花板的线）；其余脚底对齐 85%。
        var top = stance == Stances.Ceiling ? (int)Math.Round(FrameHeight * CeilingTop) : (int)Math.Round(FrameHeight * Feet) - h;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int fx = left + x, fy = top + y;
                if (fx < 0 || fy < 0 || fx >= FrameWidth || fy >= FrameHeight) continue;
                var s = (y * w + x) * 4;
                var d = (fy * FrameWidth + fx) * 4;
                var a = resized[s + 3];
                // 预乘的源 over 不透明底：out = src + dst × (1 − a)
                frame[d] = (byte)Math.Min(255, resized[s] + (kr * (255 - a) + 127) / 255);
                frame[d + 1] = (byte)Math.Min(255, resized[s + 1] + (kg * (255 - a) + 127) / 255);
                frame[d + 2] = (byte)Math.Min(255, resized[s + 2] + (kb * (255 - a) + 127) / 255);
            }
        return new RgbaImage(FrameWidth, FrameHeight, frame);
    }

    /// <summary>
    /// 贴墙关键帧的检查（05 §1 ③）：手脚贴墙的一侧要形成一条近似竖直的边——
    /// 上半身和下半身各自最左的不透明像素，水平相差不超过身宽的 8%。返回问题，空表示没问题。
    /// </summary>
    public static List<string> CheckWall(RgbaImage keyed)
    {
        var problems = new List<string>();
        if (Bounds(keyed, 128) is not { } box) return ["关键帧图是全透明的"];
        int LeftMost(int y0, int y1)
        {
            var min = int.MaxValue;
            for (var y = y0; y <= y1; y++)
                for (var x = box.Left; x <= box.Right && x < min; x++)
                    if (keyed.Pixels[(y * keyed.Width + x) * 4 + 3] > 128) { min = x; break; }
            return min;
        }
        var mid = (box.Top + box.Bottom) / 2;
        var upper = LeftMost(box.Top, mid);
        var lower = LeftMost(mid + 1, box.Bottom);
        var width = box.Right - box.Left + 1;
        if (upper == int.MaxValue || lower == int.MaxValue || Math.Abs(upper - lower) > width * 0.08)
            problems.Add("手和脚没有贴在同一道竖直的墙面上（左侧不齐），挂在屏幕边上会飘，建议重新生成");
        return problems;
    }

    /// <summary>最近邻缩到长边不超过 <paramref name="max"/>（只用于统计）。</summary>
    public static RgbaImage Shrink(RgbaImage image, int max)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(image.Width, image.Height));
        if (scale >= 1) return image;
        var (w, h) = (Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Array.Copy(image.Pixels, ((int)(y / scale) * image.Width + (int)(x / scale)) * 4, pixels, (y * w + x) * 4, 4);
        return new RgbaImage(w, h, pixels);
    }

    /// <summary>缩略图：等比缩到长边 <paramref name="max"/>，Lanczos。</summary>
    public static RgbaImage Thumbnail(RgbaImage image, int max)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(image.Width, image.Height));
        if (scale >= 1) return image;
        var (w, h) = (Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        var pre = (byte[])image.Pixels.Clone();
        Lanczos.Premultiply(pre);
        var resized = Lanczos.Resize(pre, image.Width, image.Height, w, h);
        Lanczos.Unpremultiply(resized);
        return new RgbaImage(w, h, resized);
    }
}
