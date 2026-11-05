using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

/// <summary>按顺序回放的假 HTTP：记录每个请求，按 URL 前缀给响应。</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string Body, string? Auth)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>假视频接口：数提交次数，查询按脚本回状态。</summary>
internal sealed class FakeVideo : IVideoGenerator
{
    public int Submits;
    public int Polls;
    public Func<int, VideoTaskStatus>? Status;
    public Exception? SubmitError;
    public List<VideoJob> Jobs { get; } = new();

    public Task<string> SubmitAsync(MediaProvider provider, VideoJob job, CancellationToken ct)
    {
        Interlocked.Increment(ref Submits);
        if (SubmitError is not null) throw SubmitError;
        lock (Jobs) Jobs.Add(job);
        return Task.FromResult($"cgt-{Submits}");
    }

    public Task<VideoTaskStatus> GetAsync(MediaProvider provider, string taskId, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref Polls);
        var status = Status?.Invoke(n) ?? new VideoTaskStatus("", VideoTaskState.Succeeded, "https://cdn.example/v.mp4", 1000, null);
        return Task.FromResult(status with { TaskId = taskId });
    }

    public Task CancelAsync(MediaProvider provider, string taskId, CancellationToken ct) => Task.CompletedTask;
    public Task<string> TestAsync(MediaProvider provider, CancellationToken ct) => Task.FromResult("ok");
}

internal sealed class AllowAll : ICostGate
{
    public int Asked;
    public bool Answer = true;
    public CostEstimate? Last;
    public Task<bool> ConfirmAsync(CostEstimate estimate, CancellationToken ct)
    {
        Asked++;
        Last = estimate;
        return Task.FromResult(Answer);
    }
}

public class MediaClientTests
{
    private const string Secret = "sk-secret-do-not-leak";

    private static MediaProvider Video() => new()
    {
        Kind = MediaKind.Video, Protocol = MediaProtocol.ArkVideoTasks, BaseUrl = "https://ark.example/api/v3/",
        ApiKey = Secret, Model = "seedance-x", Name = "方舟",
    };

    [Fact]
    public void Ark_body_uses_same_image_for_first_and_last_frame()
    {
        byte[] frame = [0x89, 0x50, 0x4E, 0x47, .. Enumerable.Range(0, 200).Select(i => (byte)i)];
        var p = Video();
        p.ExtraJson = """{"seed": 42, "watermark": true}""";
        var body = ArkVideoTaskClient.Body(p, new VideoJob("挥手", frame, frame, "720p", 8));
        var content = body["content"]!.AsArray();
        Assert.Equal("text", (string)content[0]!["type"]!);
        Assert.Equal("first_frame", (string)content[1]!["role"]!);
        Assert.Equal("last_frame", (string)content[2]!["role"]!);
        Assert.StartsWith("data:image/png;base64,", (string)content[1]!["image_url"]!["url"]!);
        Assert.Equal("adaptive", (string)body["ratio"]!);
        Assert.False((bool)body["generate_audio"]!);
        Assert.Equal(42, (int)body["seed"]!);
        Assert.True((bool)body["watermark"]!);   // ExtraJson 覆盖默认值
        Assert.DoesNotContain(Secret, body.ToJsonString());

        var redacted = MediaHttp.Redacted(body);
        Assert.DoesNotContain(Convert.ToBase64String(frame), redacted.ToJsonString());
        Assert.EndsWith("base64 省略)", (string)redacted["content"]![2]!["image_url"]!["url"]!);
    }

    [Fact]
    public void Bad_extra_json_is_reported_not_ignored()
    {
        var p = Video();
        p.ExtraJson = "[1,2]";
        Assert.Throws<GenerationException>(() => ArkVideoTaskClient.Body(p, new VideoJob("x", [1], null, "720p", 5)));
    }

    [Fact]
    public async Task Ark_submit_poll_and_errors()
    {
        var http = new FakeHttp((req, body) => req.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"cgt-1"}""")
            : FakeHttp.Json(HttpStatusCode.OK, """{"id":"cgt-1","status":"succeeded","content":{"video_url":"https://v/1.mp4"},"usage":{"completion_tokens":216900}}"""));
        var client = new ArkVideoTaskClient(new HttpClient(http));
        Assert.Equal("cgt-1", await client.SubmitAsync(Video(), new VideoJob("x", [1], [1], "720p", 5), default));
        var status = await client.GetAsync(Video(), "cgt-1", default);
        Assert.Equal(VideoTaskState.Succeeded, status.State);
        Assert.Equal("https://v/1.mp4", status.VideoUrl);
        Assert.Equal(216900, status.UsageTokens);
        Assert.Equal("https://ark.example/api/v3/contents/generations/tasks", http.Requests[0].Url);
        Assert.Equal($"Bearer {Secret}", http.Requests[0].Auth);

        var failing = new ArkVideoTaskClient(new HttpClient(new FakeHttp((_, _) =>
            FakeHttp.Json(HttpStatusCode.BadRequest, """{"error":{"code":"InvalidParameter","message":"duration 不支持"}}"""))));
        var e = await Assert.ThrowsAsync<GenerationException>(() => failing.SubmitAsync(Video(), new VideoJob("x", [1], [1], "720p", 99), default));
        Assert.Equal(400, e.Status);
        Assert.Contains("duration 不支持", e.Message);   // 服务端原文
    }

    [Fact]
    public void Ark_failed_status_carries_error()
    {
        var s = ArkVideoTaskClient.Parse("t", JsonNode.Parse("""{"status":"failed","error":{"code":"OutputVideoSensitive","message":"敏感"}}""")!);
        Assert.Equal(VideoTaskState.Failed, s.State);
        Assert.Contains("敏感", s.Error);
        var noUrl = ArkVideoTaskClient.Parse("t", JsonNode.Parse("""{"status":"succeeded"}""")!);
        Assert.Equal(VideoTaskState.Failed, noUrl.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"task not found"}}""", true)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"AuthenticationError","message":"bad key"}}""", false)]
    [InlineData(HttpStatusCode.NotFound, "<html>nginx</html>", false)]
    public async Task Connection_test_does_not_submit(HttpStatusCode status, string body, bool ok)
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json(status, body));
        var client = new ArkVideoTaskClient(new HttpClient(http));
        if (ok) Assert.Contains("没问题", await client.TestAsync(Video(), default));
        else await Assert.ThrowsAsync<GenerationException>(() => client.TestAsync(Video(), default));
        Assert.All(http.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));   // 只查，不提交
    }

    [Fact]
    public async Task OpenAI_uses_edits_with_references_and_generations_without()
    {
        var png = Convert.ToBase64String(PngCodec.Encode(RgbaImage.Blank(2, 2), alpha: true));
        var http = new FakeHttp((_, _) => FakeHttp.Json(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{png}}"}]}"""));
        var client = new OpenAIImagesClient(new HttpClient(http));
        var p = new MediaProvider { Kind = MediaKind.Image, Protocol = MediaProtocol.OpenAIImages, BaseUrl = "https://x/v1", ApiKey = Secret, Model = "gpt-image" };

        var result = await client.GenerateAsync(p, new ImageJob("画一只猫", [[1, 2, 3]], "1024x1536", 2), default);
        Assert.Equal(2, result.Images.Count);
        Assert.All(http.Requests, r => Assert.EndsWith("/images/edits", r.Url));
        Assert.Contains("name=\"image[]\"", http.Requests[0].Body.Replace("name=image[]", "name=\"image[]\""));

        http.Requests.Clear();
        await client.GenerateAsync(p, new ImageJob("画一只猫", [], "1024x1536", 1), default);
        Assert.EndsWith("/images/generations", http.Requests.Single().Url);
        Assert.Equal("gpt-image", (string)JsonNode.Parse(http.Requests[0].Body)!["model"]!);
    }

    [Fact]
    public void Seedance_fast_and_mini_stop_at_720p_while_2_0_and_2_5_reach_1080p()
    {
        var ids = MediaPresets.NextCoWorkVideoModels.Select(m => m.Id).ToList();
        Assert.Equal(["doubao-seedance-2-5-260628", "doubao-seedance-2-0-260128", "doubao-seedance-2-0-fast-260128", "doubao-seedance-2-0-mini-260615"], ids);
        Assert.Equal("1080p", MediaPresets.VideoModelOf("doubao-seedance-2-5-260628")!.Best);
        Assert.Equal("1080p", MediaPresets.VideoModelOf("doubao-seedance-2-0-260128")!.Best);
        Assert.Equal("720p", MediaPresets.VideoModelOf("doubao-seedance-2-0-fast-260128")!.Best);
        Assert.Equal("720p", MediaPresets.VideoModelOf("doubao-seedance-2-0-mini-260615")!.Best);
        // 每个模型能出的分辨率都有单价。
        Assert.All(MediaPresets.NextCoWorkVideoModels, m => Assert.All(m.Resolutions, r => Assert.True(m.Prices.ContainsKey(r))));

        var mini = new MediaProvider { Model = "doubao-seedance-2-0-mini-260615" };
        Assert.Equal("720p", MediaPresets.FitResolution(mini, "1080p"));
        Assert.Equal("480p", MediaPresets.FitResolution(mini, "480p"));
        Assert.Equal("1080p", MediaPresets.FitResolution(new MediaProvider { Model = "some-other" }, "1080p"));
    }

    [Fact]
    public void Custom_action_wraps_the_description_in_a_looping_timeline()
    {
        var t = StudioPrompts.Custom(" 吃甜甜圈 ", "拿出一个甜甜圈，开心地咬了几口。\n", ["click", "happy"], 8);
        var action = t.Plan();
        Assert.Equal(StudioPrompts.CustomTemplate, action.Template);
        Assert.Equal("吃甜甜圈", action.Name);
        Assert.Equal(["click", "happy"], action.Roles);
        Assert.StartsWith("0~1秒，", action.Prompt);
        Assert.Contains("拿出一个甜甜圈，开心地咬了几口。动作在原地完成", action.Prompt);
        Assert.Contains("~8秒，道具和特效消失，回到与第一帧完全一致的初始姿势", action.Prompt);
        // 描述空着时用名字顶上，不会生成一段空的时间轴。
        Assert.Contains("挥手。", StudioPrompts.Custom("挥手", "  ", ["click"], 6).Render(6));
        // 每个可选的触发时机都是合法角色名，且不重复。
        Assert.Equal(StudioPrompts.RoleNames.Count, StudioPrompts.RoleNames.Select(r => r.Role).Distinct().Count());
    }

    [Fact]
    public async Task NextCoWork_presets_hit_the_documented_gateway_paths()
    {
        var png = Convert.ToBase64String(PngCodec.Encode(RgbaImage.Blank(2, 2), alpha: true));
        var http = new FakeHttp((req, _) => req.RequestUri!.AbsolutePath.Contains("/images/")
            ? FakeHttp.Json(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{png}}"}]}""")
            : FakeHttp.Json(HttpStatusCode.OK, """{"id":"cgt-1"}"""));

        var video = MediaPresets.Find("nextcowork-video")!.Create();
        video.ApiKey = Secret;
        await new ArkVideoTaskClient(new HttpClient(http)).SubmitAsync(video, new VideoJob("x", [1], [1], "720p", 5), default);
        Assert.Equal("https://nextco.work/api/v3/contents/generations/tasks", http.Requests[0].Url);
        Assert.Equal("doubao-seedance-2-5-260628", (string)JsonNode.Parse(http.Requests[0].Body)!["model"]!);

        var image = MediaPresets.Find("nextcowork-image")!.Create();
        image.ApiKey = Secret;
        await new OpenAIImagesClient(new HttpClient(http)).GenerateAsync(image, new ImageJob("猫", [], "1536x1024", 1), default);
        Assert.Equal("https://nextco.work/v1/images/generations", http.Requests[1].Url);
        Assert.Equal("gpt-image-2.5-flare", (string)JsonNode.Parse(http.Requests[1].Body)!["model"]!);

        Assert.Equal("gpt-image-2.5", MediaPresets.Find("openai-images")!.Model);
        Assert.Equal(MediaPresets.HostOf(ProviderPresets.All.First(p => p.Id == "nextcowork").BaseUrl), MediaPresets.HostOf(video.BaseUrl));
        Assert.Null(MediaPresets.HostOf("不是网址"));
    }

    [Fact]
    public async Task Partial_image_failures_keep_the_successes()
    {
        var n = 0;
        var png = Convert.ToBase64String(PngCodec.Encode(RgbaImage.Blank(2, 2), alpha: true));
        var http = new FakeHttp((_, _) => Interlocked.Increment(ref n) == 1
            ? FakeHttp.Json(HttpStatusCode.BadRequest, """{"error":{"message":"内容不合规"}}""")
            : FakeHttp.Json(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{png}}"}]}"""));
        var p = new MediaProvider { Kind = MediaKind.Image, Protocol = MediaProtocol.ArkImages, BaseUrl = "https://x", ApiKey = "k", Model = "m" };
        var result = await new ArkImagesClient(new HttpClient(http)).GenerateAsync(p, new ImageJob("x", [[9]], "2048x2048", 3), default);
        Assert.Equal(2, result.Images.Count);
        Assert.Contains(result.Errors, e => e.Contains("内容不合规"));
        var body = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal("b64_json", (string)body["response_format"]!);
        Assert.StartsWith("data:", (string)body["image"]![0]!);
    }
}

public class StudioProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-studio-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Project_round_trips_and_starts_with_base_kit()
    {
        var p = StudioProject.Create(_root, "柴柴");
        Assert.Equal(16, p.Data.Actions.Count);   // 五类 + 地面常用 6 个 + 墙组 5 个
        Assert.Contains("贴墙", p.PlanProblem());   // 墙组要先有贴墙关键帧
        Directory.CreateDirectory(p.PathOf("keyframes"));
        File.WriteAllBytes(p.PathOf(StudioProject.FrameFile(Stances.Wall)), [1]);
        Assert.Null(p.PlanProblem());
        p.Data.Description = "橘色柴犬";
        p.Save();
        var loaded = StudioProject.Load(p.Directory);
        Assert.Equal("橘色柴犬", loaded.Data.Description);
        Assert.Single(StudioProject.List(_root));
    }

    [Fact]
    public void Video_durations_stay_within_what_the_model_accepts()
    {
        // Seedance 2.x 只接受 4～15 秒：模板不能低于下限，老工程里的 3 秒动作读进来要提到 4 秒，提交时还要再夹一次。
        Assert.All(StudioPrompts.AllTemplates, t => Assert.InRange(t.Duration, StudioPrompts.MinDuration, 15));

        var p = StudioProject.Create(_root, "x");
        var land = p.Data.Actions.First(a => a.Template == "land");
        var template = StudioPrompts.Template("land")!;
        land.Duration = 3;
        land.Prompt = template.Render(3);
        var edited = p.Data.Actions.First(a => a.Template == "let-go");
        edited.Duration = 3;
        edited.Prompt = "我自己写的提示词";
        p.Save();

        var loaded = StudioProject.Load(p.Directory);
        var fixedLand = loaded.Data.Actions.First(a => a.Template == "land");
        Assert.Equal(4, fixedLand.Duration);
        Assert.Equal(template.Render(4), fixedLand.Prompt);   // 没改过的提示词按新时长重写
        var fixedEdited = loaded.Data.Actions.First(a => a.Template == "let-go");
        Assert.Equal(4, fixedEdited.Duration);
        Assert.Equal("我自己写的提示词", fixedEdited.Prompt);   // 改过的不动

        var video = MediaPresets.Find("nextcowork-video")!.Create();
        Assert.Equal(4, MediaPresets.FitDuration(video, 3));
        Assert.Equal(15, MediaPresets.FitDuration(video, 20));
        Assert.Equal(8, MediaPresets.FitDuration(video, 8));
        var custom = MediaPresets.Find("ark-video-custom")!.Create();
        Assert.Equal(3, MediaPresets.FitDuration(custom, 3));   // 不认识的模型交给服务商决定
        var estimate = StudioEngine.VideoEstimate(loaded, [new PlannedAction { Name = "短", Duration = 3 }], video);
        Assert.Contains("4 秒", estimate.Items[0].Label);
    }

    [Fact]
    public void Steps_unlock_in_order_and_go_stale_when_upstream_changes()
    {
        var p = StudioProject.Create(_root, "x");
        Assert.Equal(StepState.Ready, p.StateOf(StepId.Bind, imageReady: false));
        Assert.Equal(StepState.Locked, p.StateOf(StepId.Describe, imageReady: false));
        Assert.Equal(StepState.Ready, p.StateOf(StepId.Describe, true));
        Assert.Equal(StepState.Locked, p.StateOf(StepId.Sheet, true));

        p.Data.Description = "一只猫";
        Assert.Equal(StepState.Ready, p.StateOf(StepId.Sheet, true));
        Directory.CreateDirectory(p.PathOf("sheet"));
        File.WriteAllBytes(p.PathOf("sheet/cand-001.png"), [1, 2, 3]);
        p.Data.Sheet.Selected = "sheet/cand-001.png";
        p.Data.Sheet.Basis = p.DescribeHash();
        Assert.Equal(StepState.Done, p.StateOf(StepId.Sheet, true));
        Assert.Equal(StepState.Ready, p.StateOf(StepId.Figure, true));

        p.Data.Description = "一只黑猫";   // 改了上游：设定图过期，但不删
        Assert.Equal(StepState.Stale, p.StateOf(StepId.Sheet, true));
        Assert.Contains(StepId.Sheet, p.Invalidated(StepId.Describe));
        Assert.True(File.Exists(p.PathOf("sheet/cand-001.png")));
    }

    [Fact]
    public void Plan_requires_the_five_roles()
    {
        var p = StudioProject.Create(_root, "x");
        p.Data.Actions.RemoveAll(a => a.Roles.Contains(Roles.Drag));
        Assert.Contains("drag", p.PlanProblem());
    }

    [Fact]
    public void Template_timeline_scales_with_duration()
    {
        var t = StudioPrompts.Template("idle")!;
        Assert.Equal(3, t.Render(10).Split('；').Length);
        Assert.StartsWith("0~3秒", t.Render(10));
        Assert.Contains("7~10秒", t.Render(10));
        Assert.Contains("2~4秒", t.Render(6));
        Assert.EndsWith("适合无缝循环", t.Render(6));
    }

    [Fact]
    public void Prefix_uses_key_color_and_companion()
    {
        var data = new ProjectData { Description = "橘色柴犬", KeyColor = "magenta", Companion = "一只小黄鸭" };
        var prefix = StudioPrompts.Prefix(data);
        Assert.Contains("#FF00FF", prefix);
        Assert.Contains("品红色", prefix);
        Assert.Contains("小黄鸭", prefix);
        Assert.EndsWith("动作：", prefix);
        Assert.Contains("#FF00FF", StudioPrompts.Figure(data));
    }

    [Fact]
    public void Ledger_survives_a_torn_last_line()
    {
        var path = Path.Combine(_root, "ledger.jsonl");
        Ledger.Append(path, new LedgerEntry { Step = "sheet", Estimate = 0.4 });
        Ledger.Append(path, new LedgerEntry { Step = "generate", Estimate = 15.1, TaskId = "cgt-1" });
        File.AppendAllText(path, "{\"time\":\"20");
        var entries = Ledger.Read(path);
        Assert.Equal(2, entries.Count);
        Assert.Equal(15.5, entries.Sum(e => e.Estimate ?? 0), 6);
    }
}

public class VideoTaskQueueTests : IDisposable
{
    private const string Secret = "sk-queue-secret";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-queue-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private readonly MediaProvider _provider = new()
    {
        Id = "video-1", Name = "方舟", Kind = MediaKind.Video, Protocol = MediaProtocol.ArkVideoTasks,
        BaseUrl = "https://ark.example/api/v3", ApiKey = Secret, Model = "seedance",
    };

    private static readonly byte[] Mp4 = Encoding.ASCII.GetBytes("fake mp4 bytes");

    private VideoTaskQueue Queue(FakeVideo video, HttpStatusCode download = HttpStatusCode.OK) =>
        new(_ => video, id => id == _provider.Id ? _provider : null,
            new HttpClient(new FakeHttp((_, _) => new HttpResponseMessage(download) { Content = new ByteArrayContent(Mp4) })), _time);

    /// <summary>拨快假时钟，直到任务完成。</summary>
    private async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            await Task.Delay(5);
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.True(task.IsCompleted, "任务没跑完");
        return await task;
    }

    private async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.True(condition());
    }

    private (StudioProject, PlannedAction, VideoJob) Setup()
    {
        var project = StudioProject.Create(_root, "x");
        var action = project.Data.Actions[0];
        return (project, action, new VideoJob("prompt", [1, 2, 3], [1, 2, 3], "720p", 10));
    }

    [Fact]
    public async Task Submits_once_writes_record_then_polls_and_downloads()
    {
        var (project, action, job) = Setup();
        var video = new FakeVideo
        {
            Status = n => n < 3
                ? new VideoTaskStatus("", VideoTaskState.Running, null, null, null)
                : new VideoTaskStatus("", VideoTaskState.Succeeded, "https://cdn/v.mp4", 216900, null),
        };
        using var queue = Queue(video);
        var record = await Drive(queue.EnqueueAsync(project, action, _provider, job, "basis", 15.1, default));

        Assert.Equal(1, video.Submits);
        Assert.Equal(3, video.Polls);
        Assert.True(record.Downloaded);
        Assert.Equal(Mp4, File.ReadAllBytes(project.RawVideo(action.Id, 1)));
        Assert.False(File.Exists(project.RawVideo(action.Id, 1) + ".part"));

        var saved = TaskRecord.TryLoad(project.TaskFile(action.Id, 1))!;
        Assert.Equal("cgt-1", saved.TaskId);
        Assert.Equal(VideoTaskState.Succeeded, saved.State);
        Assert.Equal(216900, saved.UsageTokens);

        var ledger = Ledger.Read(project.PathOf(StudioProject.LedgerFile));
        Assert.Single(ledger, e => e.Step == "generate" && e.TaskId == "cgt-1" && e.Estimate == 15.1);

        // 密钥不进任何文件（05 §4）
        foreach (var file in Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, File.ReadAllText(file));
    }

    [Fact]
    public async Task Submit_failure_is_not_retried_and_leaves_no_record()
    {
        var (project, action, job) = Setup();
        var video = new FakeVideo { SubmitError = new GenerationException("HTTP 500：服务繁忙", 500) };
        using var queue = Queue(video);
        await Assert.ThrowsAsync<GenerationException>(() => Drive(queue.EnqueueAsync(project, action, _provider, job, "b", 1, default)));
        Assert.Equal(1, video.Submits);
        Assert.Empty(project.Takes(action.Id));
        Assert.Empty(Ledger.Read(project.PathOf(StudioProject.LedgerFile)));
        Assert.Equal(0, queue.WaitingCount(project, action.Id));
    }

    [Fact]
    public async Task Resume_after_force_quit_polls_the_same_task_without_resubmitting()
    {
        var (project, action, job) = Setup();
        // 第一次：提交后一直在跑，然后「强退」（Dispose 掉队列）。
        var first = new FakeVideo { Status = _ => new VideoTaskStatus("", VideoTaskState.Running, null, null, null) };
        var queue = Queue(first);
        var pending = queue.EnqueueAsync(project, action, _provider, job, "b", 1, default);
        await Until(() => first.Polls >= 1);
        queue.Dispose();
        await pending;
        Assert.Equal(1, first.Submits);
        Assert.True(TaskRecord.TryLoad(project.TaskFile(action.Id, 1))!.NeedsWork);

        // 重开：同一个任务接着查，完成后下载，不再提交。
        var reopened = StudioProject.Load(project.Directory);
        var second = new FakeVideo();
        using var again = Queue(second);
        var changed = new List<TaskRecord>();
        again.Changed += r => { lock (changed) changed.Add(r); };
        Assert.Equal(1, again.Resume(reopened));
        await Until(() => TaskRecord.TryLoad(reopened.TaskFile(action.Id, 1))!.Downloaded);
        Assert.Equal(0, second.Submits);
        Assert.Equal(1, Ledger.Read(reopened.PathOf(StudioProject.LedgerFile)).Count(e => e.Step == "generate"));
        Assert.Equal(0, again.Resume(reopened));   // 已经完成的不再恢复
    }

    [Fact]
    public async Task Poll_errors_back_off_and_retry_but_auth_errors_stop()
    {
        var (project, action, job) = Setup();
        var calls = 0;
        var flaky = new FlakyVideo(() => ++calls switch
        {
            1 => throw new GenerationException("HTTP 429：限流", 429),
            2 => throw new GenerationException("连不上"),
            _ => new VideoTaskStatus("", VideoTaskState.Succeeded, "https://cdn/v.mp4", null, null),
        });
        using var queue = new VideoTaskQueue(_ => flaky, _ => _provider,
            new HttpClient(new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4) })), _time);
        var record = await Drive(queue.EnqueueAsync(project, action, _provider, job, "b", 1, default));
        Assert.True(record.Downloaded);
        Assert.Equal(3, calls);

        var denied = new FlakyVideo(() => throw new GenerationException("HTTP 401：bad key", 401));
        using var q2 = new VideoTaskQueue(_ => denied, _ => _provider, null, _time);
        var r2 = await Drive(q2.EnqueueAsync(project, action, _provider, job, "b", 1, default));
        Assert.True(r2.NeedsWork);   // 停下来，下次打开再试
        Assert.Contains("401", r2.Error);
    }

    [Fact]
    public async Task Expired_link_is_marked_for_regeneration()
    {
        var (project, action, job) = Setup();
        using var queue = Queue(new FakeVideo(), HttpStatusCode.Forbidden);
        var record = await Drive(queue.EnqueueAsync(project, action, _provider, job, "b", 1, default));
        Assert.False(record.Downloaded);
        Assert.Contains("重新生成", record.DownloadError);
        Assert.False(record.NeedsWork);
    }

    [Fact]
    public async Task Concurrency_limit_holds_later_jobs_locally()
    {
        var (project, _, job) = Setup();
        var video = new FakeVideo { Status = _ => new VideoTaskStatus("", VideoTaskState.Running, null, null, null) };
        using var queue = new VideoTaskQueue(_ => video, _ => _provider, null, _time, concurrency: 2);
        var tasks = project.Data.Actions.Take(3).Select(a => queue.EnqueueAsync(project, a, _provider, job, "b", 1, default)).ToList();
        await Until(() => video.Polls >= 2);
        Assert.Equal(2, video.Submits);
        Assert.Equal(1, queue.WaitingCount(project, project.Data.Actions[2].Id));
    }

    private sealed class FlakyVideo(Func<VideoTaskStatus> poll) : IVideoGenerator
    {
        private int _n;
        public Task<string> SubmitAsync(MediaProvider p, VideoJob job, CancellationToken ct) => Task.FromResult($"cgt-{++_n}");
        public Task<VideoTaskStatus> GetAsync(MediaProvider p, string id, CancellationToken ct) => Task.FromResult(poll());
        public Task CancelAsync(MediaProvider p, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<string> TestAsync(MediaProvider p, CancellationToken ct) => Task.FromResult("");
    }
}

public class FigureAndPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-figure-" + Guid.NewGuid().ToString("N"));
    public FigureAndPackTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    /// <summary>纯色底上一个实心椭圆的「站姿」。</summary>
    private static RgbaImage Figure(int w, int h, (byte, byte, byte) background, (byte, byte, byte) body, bool transparent = false)
    {
        var img = RgbaImage.Blank(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = (x - w / 2.0) / (w * 0.2);
                var dy = (y - h / 2.0) / (h * 0.35);
                var inside = dx * dx + dy * dy <= 1;
                var (r, g, b) = inside ? body : background;
                var i = (y * w + x) * 4;
                img.Pixels[i] = r;
                img.Pixels[i + 1] = g;
                img.Pixels[i + 2] = b;
                img.Pixels[i + 3] = (byte)(inside || !transparent ? 255 : 0);
            }
        return img;
    }

    [Fact]
    public void Png_round_trip()
    {
        var img = Figure(37, 23, (0, 255, 0), (200, 100, 50), transparent: true);
        var decoded = PngCodec.Instance.Decode(PngCodec.Instance.EncodePng(img));
        Assert.Equal(img.Pixels, decoded.Pixels);

        var opaque = Figure(16, 9, (0, 255, 0), (1, 2, 3));
        var rgb = PngCodec.Encode(opaque, alpha: false);
        Assert.Equal(2, rgb[25]);   // IHDR 颜色类型：RGB
        Assert.Equal(opaque.Pixels, PngCodec.Instance.Decode(rgb).Pixels);
    }

    [Fact]
    public void Decodes_real_png_from_the_repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AstraPet.slnx"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "characters", "小t", "reference", "first_frame.png");
        if (!File.Exists(file)) return;
        var img = PngCodec.Instance.Decode(File.ReadAllBytes(file));
        Assert.Equal((1280, 720), (img.Width, img.Height));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, img.Pixels[..4]);   // 左上角是纯绿
    }

    [Fact]
    public void Green_character_gets_a_non_green_key()
    {
        var white = (byte.MaxValue, byte.MaxValue, byte.MaxValue);
        Assert.Equal("green", FigureTools.ChooseKey(Figure(200, 300, white, (230, 150, 80))).Key);
        Assert.NotEqual("green", FigureTools.ChooseKey(Figure(200, 300, white, (40, 200, 60))).Key);
        Assert.Equal("green", FigureTools.ChooseKey(Figure(200, 300, white, (40, 60, 220))).Key);
    }

    [Fact]
    public void First_frame_puts_head_at_20_and_feet_at_85_percent()
    {
        var keyed = FigureTools.Extract(Figure(400, 600, (0, 0, 255), (230, 150, 80)), "blue");
        Assert.Empty(FigureTools.Check(keyed, companion: false));
        var frame = FigureTools.ComposeFirstFrame(keyed, "blue");
        Assert.Equal((1280, 720), (frame.Width, frame.Height));
        int top = -1, bottom = -1;
        for (var y = 0; y < 720; y++)
        {
            var i = (y * 1280 + 640) * 4;
            var body = frame.Pixels[i] > 128;   // 角色是橙色，底是蓝色
            if (body && top < 0) top = y;
            if (body) bottom = y;
        }
        Assert.InRange(top, 142, 147);
        Assert.InRange(bottom, 609, 613);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, frame.Pixels[..4]);
    }

    [Fact]
    public void Check_flags_cut_off_and_multiple_subjects()
    {
        var cut = RgbaImage.Blank(100, 100);
        for (var i = 0; i < 100 * 60; i++) cut.Pixels[i * 4 + 3] = 255;   // 从顶边开始
        Assert.Contains(FigureTools.Check(cut, false), p => p.Contains("边缘"));

        var two = RgbaImage.Blank(200, 100);
        foreach (var cx in new[] { 50, 150 })
            for (var y = 30; y < 70; y++)
                for (var x = cx - 20; x < cx + 20; x++)
                    two.Pixels[(y * 200 + x) * 4 + 3] = 255;
        Assert.Contains(FigureTools.Check(two, false), p => p.Contains("不止"));
        Assert.Empty(FigureTools.Check(two, companion: true));
    }

    [Fact]
    public void Transparent_upload_skips_keying()
    {
        var keyed = FigureTools.Extract(Figure(100, 150, (0, 0, 0), (40, 200, 60), transparent: true), "green");
        // 绿色的角色没有被当成绿幕抠掉
        Assert.Equal(255, keyed.Pixels[(75 * 100 + 50) * 4 + 3]);
    }

    private PackSpec Spec()
    {
        var clips = new List<PackAction>();
        foreach (var role in Roles.Required)
        {
            var file = Path.Combine(_root, role + ".webm");
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("webm " + role));
            clips.Add(new PackAction(role + "-a", "动作" + role, "描述", [role], file, role == "click" ? ["嗨"] : [], true,
                role == "move" ? 220 : null, role == "move" ? [0, 0.5, 1] : null));
        }
        return new PackSpec
        {
            Id = "shiba", Name = "柴柴", Author = "我", License = "CC BY 4.0", Actions = clips,
            Persona = new JsonObject { ["name"] = "柴柴", ["personality"] = "懒" },
            Preview = PngCodec.Encode(RgbaImage.Blank(4, 4), true),
            Sources = new Dictionary<string, byte[]> { ["studio.json"] = "{}"u8.ToArray(), ["sheet.png"] = [1] },
        };
    }

    [Fact]
    public void Writes_a_format_2_pack_that_passes_inspection_and_is_reproducible()
    {
        var a = Path.Combine(_root, "a.astrapet");
        var b = Path.Combine(_root, "b.astrapet");
        PetPackWriter.Write(Spec(), a);
        PetPackWriter.Write(Spec(), b);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));

        var (manifest, problem) = PetPack.Inspect(a);
        Assert.Null(problem);
        Assert.Equal(2, manifest!.Format);
        Assert.Equal("柴柴", manifest.Name);

        using var zip = ZipFile.OpenRead(a);
        Assert.Equal(zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal), zip.Entries.Select(e => e.FullName));
        Assert.Contains(zip.Entries, e => e.FullName == "clips/move-a/a.webm");
        Assert.Contains(zip.Entries, e => e.FullName == "source/studio.json");
        var json = JsonNode.Parse(zip.GetEntry("pet.json")!.Open())!;
        var move = json["actions"]!.AsArray().First(x => (string)x!["id"]! == "move-a")!;
        Assert.Equal(220, (double)move["gait"]!["stride"]!);
        Assert.Equal(640, (int)json["render"]!["width"]!);
        Assert.Equal("懒", (string)json["persona"]!["personality"]!);
    }
}

/// <summary>工作台的宠物体系扩展（design/pet-life/05）。</summary>
public class StudioLifeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-studio-life-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Ground_common_and_wall_group_are_required_in_the_studio()
    {
        var p = StudioProject.Create(_root, "x");
        Directory.CreateDirectory(p.PathOf("keyframes"));
        File.WriteAllBytes(p.PathOf(StudioProject.FrameFile(Stances.Wall)), [1]);
        Assert.Null(p.PlanProblem());
        Assert.Empty(p.PlanWarnings());
        // 老工程（只有五类）：缺必需动作，不能确认规划。
        p.Data.Actions.RemoveAll(a => a.Template is "eat" or "climb");
        var problem = p.PlanProblem();
        Assert.Contains("吃东西", problem);
        Assert.Contains("攀爬", problem);
        // 改了 id 也按模板认。
        p.Data.Actions.Add(StudioPrompts.Template("eat")!.Plan());
        p.Data.Actions[^1].Id = "munch";
        p.Data.Actions.Add(StudioPrompts.Template("climb")!.Plan());
        Assert.Null(p.PlanProblem());
        // 加载时仍只要求原来的五类（老角色包照样能用）。
        Assert.Equal([Roles.Idle, Roles.Click, Roles.Drag, Roles.Move, Roles.Turn], Roles.Required);
    }

    [Fact]
    public void Transition_prompts_use_their_own_prefix_and_frames()
    {
        var p = StudioProject.Create(_root, "x");
        var enter = StudioPrompts.Template("wall-enter")!.Plan();
        Assert.Equal(Stances.Ground, enter.StartStance);
        Assert.Equal(Stances.Wall, enter.EndStance);
        Assert.Contains("过渡动作", p.FullPrompt(enter));
        var letGo = StudioPrompts.Template("let-go")!.Plan();
        Assert.Equal(Stances.Wall, letGo.EndStance);   // 松手的尾帧还是贴墙
        var idle = p.Data.Actions[0];
        Assert.StartsWith(p.Data.Prefix, p.FullPrompt(idle));
    }

    [Fact]
    public void Manifest_with_stances_and_profile_loads_back()
    {
        var dir = Path.Combine(_root, "pack");
        Directory.CreateDirectory(dir);
        PackAction A(string id, string[] roles, string stance = Stances.Ground, string? to = null, double? contact = null, bool vertical = false,
            double? stride = null) => new(id, id, "", roles, "", [], true, stride) { Stance = stance, To = to, ContactX = contact, Vertical = vertical };
        var spec = new PackSpec
        {
            Id = "t", Name = "测试",
            Actions =
            [
                A("idle", ["idle"]), A("wave", ["click"]), A("drag", ["drag"]), A("run", ["move"], stride: 200), A("look", ["turn"]),
                A("wall-idle", ["idle"], Stances.Wall, contact: 118), A("climb", ["climb"], Stances.Wall, vertical: true, stride: 100),
                A("wall-enter", [], Stances.Ground, Stances.Wall, contact: 120), A("let-go", [], Stances.Wall, Stances.Air),
            ],
            Profile = new JsonObject { ["traits"] = new JsonObject { ["greedy"] = 90 }, ["diet"] = new JsonObject { ["likes"] = new JsonArray("fish") } },
        };
        var manifest = PetPackWriter.Manifest(spec);
        foreach (var a in spec.Actions)
        {
            Directory.CreateDirectory(Path.Combine(dir, "clips", a.Id));
            File.WriteAllBytes(Path.Combine(dir, "clips", a.Id, "a.webm"), []);
        }
        File.WriteAllText(Path.Combine(dir, "pet.json"), manifest.ToJsonString());

        var lib = PetLibrary.Load(dir, new FakeDecoder());
        Assert.Contains(Stances.Wall, lib.Stances.Available);
        Assert.Equal(new StanceTransition(Stances.Ground, Stances.Wall), lib.Actions.Find("wall-enter")!.Transition);
        Assert.Equal(120, lib.Actions.Find("wall-enter")!.ContactX);
        Assert.Equal(GaitAxis.Y, lib.Actions.Find("climb")!.Gait!.Axis);
        Assert.Equal(Stances.Wall, lib.Actions.Find("wall-idle")!.Stance);
        Assert.Equal(90, lib.Profile.Traits.Greedy);
        Assert.Equal(["fish"], lib.Profile.Diet.Likes);
    }

    [Fact]
    public void Keyframe_compose_puts_the_wall_figure_on_the_left_and_feet_on_the_line()
    {
        var keyed = new RgbaImage(100, 200, new byte[100 * 200 * 4]);
        for (var y = 20; y < 180; y++)
            for (var x = 30; x < 70; x++)
                keyed.Pixels[(y * 100 + x) * 4 + 3] = 255;
        var frame = FigureTools.Compose(keyed, "green", Stances.Wall);
        int? left = null, bottom = null;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var i = (y * frame.Width + x) * 4;
                if (frame.Pixels[i + 1] < 200)   // 不是绿底
                {
                    left = Math.Min(left ?? x, x);
                    bottom = Math.Max(bottom ?? y, y);
                }
            }
        Assert.InRange(left!.Value, (int)(1280 * FigureTools.WallLeft) - 2, (int)(1280 * FigureTools.WallLeft) + 2);
        Assert.InRange(bottom!.Value, (int)(720 * FigureTools.Feet) - 3, (int)(720 * FigureTools.Feet));
        Assert.Empty(FigureTools.CheckWall(keyed));
    }

    [Fact]
    public void Frame_locked_placement_maps_the_frame_layout_onto_the_canvas()
    {
        var p = Placement.FrameLocked(1280, 720);
        Assert.InRange(p.Dy + 720 * FigureTools.Feet * p.Scale, PetClip.FeetY - 1, PetClip.FeetY + 1);   // 取整误差
        Assert.InRange(p.Dx + 640 * p.Scale, PetClip.CenterX - 1, PetClip.CenterX + 1);
    }
}
