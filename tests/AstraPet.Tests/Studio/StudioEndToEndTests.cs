using AstraPet.Core;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Media;
using AstraPet.Infrastructure.Studio;

namespace AstraPet.Tests.Studio;

/// <summary>
/// 工作台端到端（不花钱）：用仓库里小t 的参考图和原始视频走 ②③④，伪造「已下载」的任务记录代替 ⑤，
/// 真跑 ⑥ 处理和 ⑧ 导出，再用真 ffmpeg 解码器按程序加载角色的路径加载导出的包。需要带 libvpx-vp9 的 ffmpeg，缺了跳过。
/// </summary>
[Trait("Category", "Slow")]
public sealed class StudioEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-studio-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public StudioEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AstraPet.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Ffmpeg => Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } f ? f : "/opt/homebrew/bin/ffmpeg";

    /// <summary>基础套件的动作 id → 小t 的原始视频。</summary>
    internal static readonly IReadOnlyDictionary<string, string> RawOfXiaoT = new Dictionary<string, string>
    {
        ["idle"] = "idle/待机呼吸.mp4",
        ["drag"] = "drag/被拎起悬空.mp4",
        ["wave"] = "click/开心挥手.mp4",
        ["trot"] = "move/原地小跑.mp4",
        ["look"] = "turn/东张西望.mp4",
    };

    /// <summary>照工作台的流程搭一个工程，⑤ 用现成的视频代替（写成已下载的任务记录）。</summary>
    internal static StudioProject Seed(StudioEngine engine, string root, string character)
    {
        var project = StudioProject.Create(root, "小t（演示）");
        project.Data.Description = "穿蓝色卫衣的小女孩，抱着一只水豚";
        project.Data.Card.Name = "小t";
        project.Data.Card.Personality = "元气、爱撒娇";
        project.Save();
        var sheet = engine.ImportCandidate(project, "sheet", Path.Combine(character, "reference", "sheet.png"));
        engine.SelectSheet(project, sheet);
        project.Data.KeyColor = "green";   // 小t 的原始视频是绿幕
        var figure = engine.ImportCandidate(project, "figure", Path.Combine(character, "reference", "figure.png"));
        var outcome = engine.SelectFigure(project, figure);
        // 墙组是必需的：贴墙关键帧用站姿图凑一张（演示不调图片模型）。
        engine.SelectKeyframe(project, Stances.Wall, engine.ImportCandidate(project, "keyframes/wall", Path.Combine(character, "reference", "figure.png")));
        Assert.Null(engine.ConfirmPlan(project));
        foreach (var action in project.Data.Actions)
        {
            Directory.CreateDirectory(project.RawDir(action.Id));
            // 宠物体系的动作没有现成视频：用待机视频代替（只为走通流程）。
            File.Copy(Path.Combine(character, "raw", RawOfXiaoT.GetValueOrDefault(action.Id, RawOfXiaoT["idle"])), project.RawVideo(action.Id, 1));
            new TaskRecord
            {
                Action = action.Id, Take = 1, TaskId = "demo-" + action.Id, State = VideoTaskState.Succeeded, Downloaded = true,
                Provider = "演示", Model = "demo", Prompt = project.FullPrompt(action), Resolution = project.Data.Resolution,
                Duration = action.Duration, Basis = project.TakeBasis(action), SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now,
            }.Save(project.TaskFile(action.Id, 1));
        }
        _ = outcome;
        return project;
    }

    [Fact]
    public async Task Description_to_installed_pack_without_paid_calls()
    {
        if (!File.Exists(Ffmpeg))
        {
            _output.WriteLine("没有 ffmpeg，跳过");
            return;
        }
        var character = Path.Combine(Root, "characters", "小t");
        using var queue = new VideoTaskQueue(_ => new FakeVideo(), _ => null);
        var gate = new AllowAll();
        var engine = new StudioEngine(PngCodec.Instance, gate, queue, Ffmpeg);
        var project = Seed(engine, Path.Combine(_dir, "studio"), character);

        Assert.Equal(StepState.Done, project.StateOf(StepId.Sheet, imageReady: true));
        Assert.Equal(StepState.Done, project.StateOf(StepId.Figure, imageReady: true));
        Assert.Equal(StepState.Done, project.StateOf(StepId.Plan, imageReady: true));
        Assert.Equal(StepState.Done, project.StateOf(StepId.Generate, imageReady: true));
        Assert.Equal(StepState.Ready, project.StateOf(StepId.Process, imageReady: true));

        foreach (var action in project.Data.Actions)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var report = await engine.ProcessTakeAsync(project, action, 1, null, CancellationToken.None);
            _output.WriteLine($"{action.Id}: {report.Frames} 帧 {watch.Elapsed.TotalSeconds:0.0}s 首尾 {report.LoopIou:0.00} {string.Join("；", report.Warnings)}");
            Assert.True(File.Exists(project.ClipFile(action.Id, 1)));
            Assert.True(File.Exists(StudioEngine.ThumbFile(project, action.Id, 1)));
            action.Adopted = 1;
        }
        project.Save();
        Assert.Equal(StepState.Done, project.StateOf(StepId.Process, imageReady: true));
        Assert.Equal(StepState.Ready, project.StateOf(StepId.Export, imageReady: true));

        var decoder = new FfmpegClipDecoder(Ffmpeg);
        var pack = Path.Combine(_dir, "out", "xiaot-demo.astrapet");
        project.Data.Export.Id = "xiaot-demo";
        engine.Export(project, pack, decoder, Path.Combine(_dir, "cache"));
        Assert.Equal(StepState.Done, project.StateOf(StepId.Export, imageReady: true));
        Assert.Equal(0, gate.Asked);   // 全程没有付费调用

        // 「另一台机器」：只拿这个文件，放进新的角色目录，按程序启动时的路径扫描、加载。
        var users = Path.Combine(_dir, "other-machine", "characters");
        Directory.CreateDirectory(users);
        File.Copy(pack, Path.Combine(users, "xiaot-demo.astrapet"));
        var catalog = new PetCatalog(Path.Combine(_dir, "no-builtins"), users, Path.Combine(_dir, "other-machine", "cache"));
        var entry = Assert.Single(catalog.Scan());
        Assert.Null(entry.Problem);
        Assert.Equal("小t", entry.Name);
        var library = catalog.Load(entry, decoder);
        Assert.Equal(16, library.Actions.All.Count);
        Assert.Contains(Stances.Wall, library.Stances.Available);
        Assert.Empty(library.Actions.MissingRequired());
        Assert.NotNull(library.Actions.Find("trot")?.Gait);
        _output.WriteLine($"包 {new FileInfo(pack).Length / 1024} KB，{library.ClipCount} 段");

        // 导出之后又改了动作：⑧ 回到待完成。
        var wave = project.Data.Actions.First(a => a.Id == "wave");
        wave.Lines = ["你好呀"];
        Assert.Equal(StepState.Ready, project.StateOf(StepId.Export, imageReady: true));
    }
}
