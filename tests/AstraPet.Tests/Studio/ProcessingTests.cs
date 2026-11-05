using System.Diagnostics;
using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Infrastructure.Media;
using AstraPet.Infrastructure.Studio;

namespace AstraPet.Tests;

/// <summary>抠像、缩放的单元测试：合成帧，不需要 ffmpeg。</summary>
public class ChromaKeyerTests
{
    private const int W = 120, H = 90;

    /// <summary>纯色背景中间一个实心圆，边缘一圈按已知覆盖率和背景混合。</summary>
    private static (byte[] Rgb, float[] Coverage) Synthetic(byte[] background, byte[] figure)
    {
        var rgb = new byte[W * H * 3];
        var coverage = new float[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var d = MathF.Sqrt((x - 60) * (x - 60) + (y - 45) * (y - 45));
                var a = Math.Clamp(25.5f - d, 0, 1);   // 半径 25 的圆，1 像素宽的抗锯齿边
                coverage[y * W + x] = a;
                for (var c = 0; c < 3; c++)
                    rgb[(y * W + x) * 3 + c] = (byte)MathF.Round(a * figure[c] + (1 - a) * background[c]);
            }
        return (rgb, coverage);
    }

    public static TheoryData<KeyColor, byte[]> Backgrounds => new()
    {
        { KeyColor.Green, [0, 255, 0] },
        { KeyColor.Green, [20, 215, 30] },   // 偏暗的绿幕，靠归一化
        { KeyColor.Blue, [0, 0, 255] },
        { KeyColor.Magenta, [255, 0, 255] },
    };

    [Theory]
    [MemberData(nameof(Backgrounds))]
    public void Keys_solid_backgrounds(KeyColor key, byte[] background)
    {
        byte[] figure = [230, 160, 90];   // 肤色，三种抠像色下都不算背景
        var (rgb, coverage) = Synthetic(background, figure);
        var rgba = ChromaKeyer.Key(rgb, W, H, key);

        for (var i = 0; i < W * H; i++)
        {
            var a = rgba[i * 4 + 3];
            if (coverage[i] == 0) Assert.True(a == 0 && rgba[i * 4] == 0, $"背景像素 {i} 没抠干净: alpha {a}");
            if (coverage[i] == 1)
            {
                Assert.Equal(255, a);
                for (var c = 0; c < 3; c++) Assert.InRange(rgba[i * 4 + c], figure[c] - 3, figure[c] + 3);
            }
            else if (a > 0)
            {
                // 边缘：不带背景色（抠像色通道不超过其余通道）。
                int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                var spill = key switch
                {
                    KeyColor.Green => g - Math.Max(r, b),
                    KeyColor.Blue => b - Math.Max(r, g),
                    _ => Math.Min(r, b) - g,
                };
                Assert.True(spill <= 1, $"边缘像素 {i} 带背景色: {r},{g},{b}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Backgrounds))]
    public void Detects_background_color_from_corners(KeyColor key, byte[] background)
    {
        var (rgb, _) = Synthetic(background, [230, 160, 90]);
        Assert.Equal(key, ChromaKeyer.Detect(rgb, W, H));
    }

    [Fact]
    public void Rejects_non_solid_backgrounds()
    {
        var (rgb, _) = Synthetic([128, 128, 128], [230, 160, 90]);
        Assert.Null(ChromaKeyer.Detect(rgb, W, H));
    }

    [Fact]
    public void Placement_scales_to_standard_pose()
    {
        // 源视频里人物 200 高、脚底 y=500、中心 x=640。
        var p = Placement.For(new FigureBox(301, 500, 600, 680, 640));
        Assert.Equal(270.0 / 200, p.Scale);
        Assert.Equal(PetClip.FeetY, (int)Math.Round(500 * p.Scale) + p.Dy);
        Assert.Equal(PetClip.CenterX, (int)Math.Round(640 * p.Scale) + p.Dx);
        Assert.False(p.NarrowedForWidth);

        var wide = Placement.For(new FigureBox(301, 500, 100, 1100, 640));
        Assert.True(wide.NarrowedForWidth);
        Assert.Equal(Placement.MaxFigureWidth, 1001 * wide.Scale, 3);
    }

    [Fact]
    public void Gait_curve_is_flat_outside_window_and_where_feet_rest()
    {
        double[] motion = [0, 1, 1, 0, 1, 1, 0, 0];
        var curve = QaMeter.GaitCurve(motion, fps: 1, windowStart: 1, windowEnd: 6);
        Assert.Equal([0, 0.25, 0.5, 0.5, 0.75, 1, 1, 1], curve);
    }
}

/// <summary>
/// 金标准（06 §6）：和 Pillow / tools/petgen/process.py 逐项对照。需要 tools/petgen/.venv 和带 libvpx-vp9 编码的 ffmpeg
/// （FFMPEG 环境变量，默认 /opt/homebrew/bin/ffmpeg），缺了就跳过。
/// </summary>
[Trait("Category", "Slow")]
public class ProcessingGoldenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-golden-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ProcessingGoldenTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AstraPet.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Python => Path.Combine(Root, "tools", "petgen", ".venv", "bin", "python");
    private static string Ffmpeg => Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } f ? f : "/opt/homebrew/bin/ffmpeg";
    private static bool Available => File.Exists(Python) && File.Exists(Ffmpeg);

    private static void Reference(params string[] args)
    {
        var info = new ProcessStartInfo(Python) { RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(Root, "tests", "AstraPet.Tests", "Studio", "reference.py"));
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>有一片实心区、一片透明区、中间随机的 RGBA 图，接近抠像后的帧。</summary>
    private static byte[] Figure(int w, int h, int seed)
    {
        var rgba = Random(w * h * 4, seed);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4 + 3;
                if (x < w / 3) rgba[i] = 0;
                else if (x > w * 2 / 3) rgba[i] = 255;
            }
        return rgba;
    }

    [Theory]
    [InlineData(1, 2.0)]
    [InlineData(3, 2.0)]
    [InlineData(1, 0.7)]
    [InlineData(3, 5.0)]
    public void Blur_matches_pillow(int channels, double radius)
    {
        if (!Available) return;
        const int w = 97, h = 61;
        var input = Random(w * h * channels, channels);
        File.WriteAllBytes(Path.Combine(_dir, "in.raw"), input);
        Reference("blur", Path.Combine(_dir, "in.raw"), $"{w}", $"{h}", $"{channels}", $"{radius}", Path.Combine(_dir, "out.raw"));

        BoxBlur.Gaussian(input, w, h, channels, radius);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "out.raw")), input);
    }

    [Theory]
    [InlineData(160, 90, 73, 41)]
    [InlineData(128, 72, 128, 41)]
    [InlineData(64, 48, 90, 70)]
    public void Premultiplied_lanczos_matches_pillow(int w, int h, int ow, int oh)
    {
        if (!Available) return;
        var input = Figure(w, h, 7);
        File.WriteAllBytes(Path.Combine(_dir, "in.raw"), input);
        Reference("resize", Path.Combine(_dir, "in.raw"), $"{w}", $"{h}", $"{ow}", $"{oh}", Path.Combine(_dir, "out.raw"));

        var premultiplied = (byte[])input.Clone();
        Lanczos.Premultiply(premultiplied);
        var output = Lanczos.Resize(premultiplied, w, h, ow, oh);
        Lanczos.Unpremultiply(output);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "out.raw")), output);
    }

    private static List<byte[]> DecodeFrames(string path, string pixFmt, int w, int h, bool libvpx, int? limit = null)
    {
        var info = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var args = new List<string> { "-v", "error" };
        if (libvpx) args.AddRange(["-c:v", "libvpx-vp9"]);
        args.AddRange(["-i", path]);
        if (limit is { } n) args.AddRange(["-frames:v", $"{n}"]);
        args.AddRange(["-f", "rawvideo", "-pix_fmt", pixFmt, "-"]);
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        _ = p.StandardError.ReadToEndAsync();
        var size = w * h * (pixFmt == "rgba" ? 4 : 3);
        var frames = new List<byte[]>();
        var stream = p.StandardOutput.BaseStream;
        while (true)
        {
            var buf = new byte[size];
            if (stream.ReadAtLeast(buf, size, throwOnEndOfStream: false) < size) break;
            frames.Add(buf);
        }
        p.WaitForExit();
        return frames;
    }

    private static string RawClip(string category) =>
        Directory.GetFiles(Path.Combine(Root, "characters", "小t", "raw", category), "*.mp4").Order().First();

    [Fact]
    public void Chroma_key_matches_process_py_on_real_frames()
    {
        if (!Available) return;
        var src = RawClip("click");
        var frames = DecodeFrames(src, "rgb24", 1280, 720, libvpx: false, limit: 40);
        foreach (var frame in new[] { frames[0], frames[^1] })
        {
            File.WriteAllBytes(Path.Combine(_dir, "in.raw"), frame);
            Reference("key", Path.Combine(_dir, "in.raw"), "1280", "720", Path.Combine(_dir, "out.raw"));
            var expected = File.ReadAllBytes(Path.Combine(_dir, "out.raw"));
            var actual = ChromaKeyer.Key(frame, 1280, 720, KeyColor.Green);
            // 单精度运算顺序不完全一样（numpy 的均值是成对求和），允许个别像素差 1。
            var off = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var d = Math.Abs(expected[i] - actual[i]);
                Assert.True(d <= 1 || ++off < expected.Length / 10000, $"字节 {i} 差 {d}");
            }
        }
    }

    public static TheoryData<string> Categories => new() { "idle", "click", "move", "drag" };

    [Theory]
    [MemberData(nameof(Categories))]
    public async Task Pipeline_matches_process_py(string category)
    {
        if (!Available) return;
        var src = RawClip(category);
        var expectedWebm = Path.Combine(_dir, "py.webm");
        var reportFile = Path.Combine(_dir, "py.json");
        var watch = Stopwatch.StartNew();
        Reference("process", src, expectedWebm, category, reportFile);
        var py = JsonNode.Parse(File.ReadAllText(reportFile))!;
        var pyTime = watch.Elapsed;

        var actualWebm = Path.Combine(_dir, "cs.webm");
        watch.Restart();
        var report = await new ClipProcessor(Ffmpeg).ProcessAsync(src, actualWebm,
            new ClipOptions { Key = KeyColor.Green, IsMove = category == "move", IsDrag = category == "drag" });
        _output.WriteLine($"{category}: {report.Frames} 帧，process.py {pyTime.TotalSeconds:0.0}s，C# {watch.Elapsed.TotalSeconds:0.0}s");
        Assert.False(File.Exists(actualWebm + ".part"));

        Assert.Equal((int)py["frames"]!, report.Frames);
        Assert.Equal((double)py["scale"]!, report.Scale, 6);
        Assert.Equal((double)py["loop_iou"]!, report.LoopIou, 2);
        Assert.Equal((double)py["drift_x"]!, report.DriftX, 0.5);
        Assert.Equal((int)py["drift_feet"]!, report.DriftFeet);
        Assert.Equal((double)py["edge_leak"]!, report.EdgeLeak, 3);
        Assert.Equal((double)py["haze"]!, report.Haze, 3);
        if (py["curve"] is JsonArray curve)
            Assert.All(curve.Select((v, i) => Math.Abs((double)v! - report.Curve![i])), d => Assert.True(d < 0.02, $"步态曲线差 {d}"));

        // 解出两边的 webm 比逐帧 alpha：平均绝对差 < 3/255。
        var expected = DecodeFrames(expectedWebm, "rgba", PetClip.Width, PetClip.Height, libvpx: true);
        var actual = DecodeFrames(actualWebm, "rgba", PetClip.Width, PetClip.Height, libvpx: true);
        Assert.Equal(expected.Count, actual.Count);
        for (var f = 0; f < expected.Count; f++)
        {
            long diff = 0;
            for (var i = 3; i < expected[f].Length; i += 4) diff += Math.Abs(expected[f][i] - actual[f][i]);
            var mean = (double)diff / (PetClip.Width * PetClip.Height);
            Assert.True(mean < 3, $"第 {f} 帧 alpha 平均差 {mean:0.00}");
        }

        // 处理出来的 webm 宠物能直接播：尺寸、帧率、alpha 都对。
        var clip = new FfmpegClipDecoder(Ffmpeg).Probe(actualWebm, category);
        Assert.Equal(report.Fps, clip.Fps);
    }

    [Fact]
    public async Task Imported_green_screen_clip_loads_as_user_action()
    {
        if (!Available) return;
        // 08 第三期验收：导入一段绿幕 mp4，几分钟内在宠物上看到新动作。这里走和控制台「导入视频」一样的路径。
        var overlayDir = Path.Combine(_dir, "overlay");
        var report = await new ClipProcessor(Ffmpeg).ProcessAsync(RawClip("click"),
            Path.Combine(overlayDir, "clips", "my-1", "a.webm"), new ClipOptions());
        Assert.Equal(KeyColor.Green, report.Key);   // 自动判断出绿幕

        var overlay = new ActionOverlay();
        overlay.Added.Add(new AddedAction { Id = "my-1", Name = "我的动作", Roles = ["celebrate"], Clips = ["clips/my-1/a.webm"] });
        overlay.Save(overlayDir);

        var pack = PetPack.Extract(Path.Combine(Root, "src", "Assets", "pets", "xiaot.astrapet"), Path.Combine(_dir, "cache"));
        var logs = new List<string>();
        var lib = PetLibrary.Load(pack, new FfmpegClipDecoder(Ffmpeg), logs.Add, overlayDir);
        var action = lib.Actions.Find("my-1");
        Assert.True(action is not null, string.Join("\n", logs));
        Assert.Equal(ActionOrigin.User, action.Origin);
        Assert.InRange(action.Clips[0].EstimatedFrames, report.Frames - 2, report.Frames + 2);
    }

    [Fact]
    public async Task Transparent_webm_is_not_keyed_again()
    {
        if (!Available) return;
        var src = Path.Combine(Root, "characters", "小t", "videos", "idle", "待机呼吸.webm");
        if (!File.Exists(src)) return;
        var processor = new ClipProcessor(Ffmpeg);
        var info = await processor.ProbeAsync(src);
        Assert.True(info.HasAlpha);
        Assert.True(info.NeedsLibvpx);

        var report = await processor.ProcessAsync(src, Path.Combine(_dir, "out.webm"), new ClipOptions());
        Assert.Null(report.Key);
        // 已经是标准站姿，重新定位后几乎不缩放。
        Assert.InRange(report.Scale, 0.97, 1.03);
        Assert.Empty(report.Warnings);
        Assert.NotNull(report.Preview);
    }

    [Fact]
    public async Task Rejects_videos_without_solid_background()
    {
        if (!Available) return;
        var src = Path.Combine(_dir, "noise.mp4");
        var info = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1", "-pix_fmt", "yuv420p", src })
            info.ArgumentList.Add(a);
        using (var p = Process.Start(info)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) return; }

        var e = await Assert.ThrowsAsync<ClipProcessingException>(() =>
            new ClipProcessor(Ffmpeg).ProcessAsync(src, Path.Combine(_dir, "out.webm"), new ClipOptions()));
        Assert.Contains("纯色", e.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "out.webm.part")));
    }
}
