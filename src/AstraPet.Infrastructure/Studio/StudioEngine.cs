using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>③ 应用一张站姿的结果：自动检查发现的问题（空表示没问题）。</summary>
public sealed record FigureOutcome(IReadOnlyList<string> Problems);

/// <summary>
/// 工作台每一步的实际操作（04 §3）。界面只管点按钮、显示状态；这里调生成接口、写文件、记账。
/// 所有付费调用都先过 <see cref="ICostGate"/>，确认了才发；图片在调用线程上同步等结果，视频交给 <see cref="VideoTaskQueue"/>。
/// </summary>
public sealed class StudioEngine(
    IImageCodec codec, ICostGate gate, VideoTaskQueue queue, string ffmpeg,
    Func<MediaProvider, IImageGenerator>? images = null)
{
    public const string SheetSize = "1536x1024";
    public const string FigureSize = "1024x1536";
    /// <summary>移动动作的默认步幅（640×360 素材坐标，每循环一圈），⑦ 里再调。小t 是 240。</summary>
    public const double DefaultStride = 220;

    private readonly Func<MediaProvider, IImageGenerator> _images = images ?? (p => MediaClients.Image(p.Protocol));

    public VideoTaskQueue Queue => queue;
    public IImageCodec Codec => codec;

    // ───── ① ─────

    /// <summary>参考图复制进 inputs/（不引用原路径），统一转成 PNG。</summary>
    public string AddReference(StudioProject project, string path)
    {
        var image = codec.Decode(File.ReadAllBytes(path));
        Directory.CreateDirectory(project.PathOf("inputs"));
        string rel;
        for (var i = 1; ; i++)
        {
            rel = $"inputs/ref-{i}.png";
            if (!File.Exists(project.PathOf(rel))) break;
        }
        File.WriteAllBytes(project.PathOf(rel), codec.EncodePng(FigureTools.Thumbnail(image, 2048)));
        project.Data.References.Add(rel);
        project.Save();
        return rel;
    }

    // ───── ② ③ 图片 ─────

    public static CostEstimate ImageEstimate(MediaProvider provider, string what, int count)
    {
        var price = provider.PriceOf("*");
        return new CostEstimate($"生成{what}", [new CostItem($"{what} × {count}", price * count)], price * count, provider.Name, provider.Model);
    }

    /// <summary>② 生成设定图候选。用户在确认框里取消返回空列表。</summary>
    public Task<IReadOnlyList<string>> GenerateSheetsAsync(StudioProject project, MediaProvider provider, int count, CancellationToken ct)
    {
        var refs = project.Data.References.Select(r => File.ReadAllBytes(project.PathOf(r))).ToList();
        var prompt = project.Data.Sheet.Prompt.Trim() is { Length: > 0 } p ? p : StudioPrompts.Sheet(project.Data, refs.Count > 0);
        project.Data.Sheet.Prompt = prompt;
        var basis = project.DescribeHash();
        return GenerateImagesAsync(project, provider, "设定图", "sheet", new ImageJob(prompt, refs, SheetSize, count), ct,
            saved => project.Data.Sheet.Candidates.AddRange(saved), () => project.Data.Sheet.Basis = basis);
    }

    /// <summary>③ 生成站姿候选。参考图：选中的设定图 + 用户的参考图（最多 3 张）。</summary>
    public Task<IReadOnlyList<string>> GenerateFiguresAsync(StudioProject project, MediaProvider provider, int count, CancellationToken ct)
    {
        if (project.Data.Sheet.Selected is not { } sheet) throw new InvalidOperationException("先在第 ② 步选一张设定图");
        var refs = new[] { sheet }.Concat(project.Data.References).Take(3).Select(r => File.ReadAllBytes(project.PathOf(r))).ToList();
        var prompt = project.Data.Figure.Prompt.Trim() is { Length: > 0 } p ? p : StudioPrompts.Figure(project.Data);
        project.Data.Figure.Prompt = prompt;
        return GenerateImagesAsync(project, provider, "站姿", "figure", new ImageJob(prompt, refs, FigureSize, count), ct,
            saved => project.Data.Figure.Candidates.AddRange(saved), () => { });
    }

    private async Task<IReadOnlyList<string>> GenerateImagesAsync(StudioProject project, MediaProvider provider, string what,
        string folder, ImageJob job, CancellationToken ct, Action<List<string>> add, Action onSaved)
    {
        var estimate = ImageEstimate(provider, what, job.Count);
        if (!await gate.ConfirmAsync(estimate, ct)) return [];
        var result = await _images(provider).GenerateAsync(provider, job, ct);
        project.AppendLedger(new LedgerEntry
        {
            Step = folder, Provider = provider.Name, Model = provider.Model,
            Summary = $"{what} × {job.Count}（成功 {result.Images.Count}）{job.Size}",
            Estimate = provider.PriceOf("*") * result.Images.Count, Usage = result.Usage,
        });
        var saved = new List<string>();
        foreach (var bytes in result.Images)
        {
            var image = codec.Decode(bytes);
            var rel = project.NextCandidate(folder);
            File.WriteAllBytes(project.PathOf(rel), codec.EncodePng(image));
            saved.Add(rel);
        }
        add(saved);
        onSaved();
        project.Save();
        if (result.Errors.Count > 0) PetLog.Write($"工作台：{what}部分失败：{string.Join("；", result.Errors)}");
        return saved;
    }

    /// <summary>上传自己画好的图当候选（② 设定图或 ③ 站姿），跳过生成。</summary>
    public string ImportCandidate(StudioProject project, string folder, string path)
    {
        var image = codec.Decode(File.ReadAllBytes(path));
        var rel = project.NextCandidate(folder);
        File.WriteAllBytes(project.PathOf(rel), codec.EncodePng(image));
        var step = folder == "sheet" ? project.Data.Sheet
            : folder.StartsWith("keyframes/", StringComparison.Ordinal) ? KeyframeStep(project, folder["keyframes/".Length..])
            : project.Data.Figure;
        step.Candidates.Add(rel);
        if (folder == "sheet") project.Data.Sheet.Basis = project.DescribeHash();
        project.Save();
        return rel;
    }

    /// <summary>② 选定设定图，顺带按设定图里角色的颜色选抠像色（③ 的提示词要用）。</summary>
    public void SelectSheet(StudioProject project, string rel)
    {
        project.Data.Sheet.Selected = rel;
        project.Data.Sheet.Basis = project.DescribeHash();
        var (key, damage) = FigureTools.ChooseKey(codec.Decode(File.ReadAllBytes(project.PathOf(rel))));
        if (key != project.Data.KeyColor) PetLog.Write($"工作台：抠像色改成 {key}（{string.Join("，", damage.Select(d => $"{d.Key} {d.Value:0.0%}"))}）");
        project.Data.KeyColor = key;
        project.Data.Figure.Prompt = "";   // 提示词里有抠像色，下次按新的重新生成
        project.Save();
    }

    /// <summary>
    /// ③ 选定站姿：抠出来、检查、合成首尾帧（不调模型，04 §3 ③ 第 4 条）。检查发现问题也照样合成，由用户决定要不要重做。
    /// 背景不是纯色抠不出来时抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public FigureOutcome SelectFigure(StudioProject project, string rel)
    {
        var figure = codec.Decode(File.ReadAllBytes(project.PathOf(rel)));
        var keyed = FigureTools.Extract(figure, project.Data.KeyColor);
        var problems = FigureTools.Check(keyed, project.Data.Companion.Trim().Length > 0);
        var frame = FigureTools.ComposeFirstFrame(keyed, project.Data.KeyColor);
        Directory.CreateDirectory(project.PathOf("figure"));
        File.WriteAllBytes(project.PathOf(StudioProject.KeyedFigureFile), codec.EncodePng(keyed));
        File.WriteAllBytes(project.PathOf(StudioProject.FirstFrameFile), PngCodec.Encode(frame, alpha: false));
        project.Data.Figure.Selected = rel;
        project.Data.Figure.Basis = project.FigureBasis();
        project.Data.FirstFrameBasis = project.FirstFrameBasisNow();
        if (project.Data.Prefix.Length == 0 || project.Data.PlanBasis.Length == 0) project.Data.Prefix = StudioPrompts.Prefix(project.Data);
        project.Save();
        return new FigureOutcome(problems);
    }

    /// <summary>③ 选装姿态的关键帧候选（design/pet-life/05 §1 ③）：以抠好的站姿为参考做图片编辑。</summary>
    public Task<IReadOnlyList<string>> GenerateKeyframesAsync(StudioProject project, MediaProvider provider, string stance, int count,
        CancellationToken ct)
    {
        if (!File.Exists(project.PathOf(StudioProject.FirstFrameFile))) throw new InvalidOperationException("先在第 ③ 步选好站姿");
        var step = KeyframeStep(project, stance);
        var refs = new List<byte[]> { File.ReadAllBytes(project.PathOf(project.Data.Figure.Selected!)) };
        if (project.Data.Sheet.Selected is { } sheet) refs.Add(File.ReadAllBytes(project.PathOf(sheet)));
        var prompt = step.Prompt.Trim() is { Length: > 0 } p ? p : StudioPrompts.Keyframe(project.Data, stance);
        step.Prompt = prompt;
        return GenerateImagesAsync(project, provider, StudioPrompts.StanceName(stance) + "关键帧", $"keyframes/{stance}",
            new ImageJob(prompt, refs, FigureSize, count), ct, saved => step.Candidates.AddRange(saved), () => { });
    }

    private static ImageStep KeyframeStep(StudioProject project, string stance)
    {
        if (!project.Data.Keyframes.TryGetValue(stance, out var step))
            project.Data.Keyframes[stance] = step = new ImageStep();
        return step;
    }

    /// <summary>
    /// ③ 选定某个姿态的关键帧：抠出来、检查（贴墙要手脚在一条竖线上）、按姿态合成首尾帧。不调模型。
    /// </summary>
    public FigureOutcome SelectKeyframe(StudioProject project, string stance, string rel)
    {
        var image = codec.Decode(File.ReadAllBytes(project.PathOf(rel)));
        var keyed = FigureTools.Extract(image, project.Data.KeyColor);
        var problems = stance == Stances.Wall ? FigureTools.CheckWall(keyed) : [];
        var frame = FigureTools.Compose(keyed, project.Data.KeyColor, stance);
        Directory.CreateDirectory(project.PathOf("keyframes"));
        File.WriteAllBytes(project.PathOf(StudioProject.KeyedKeyframeFile(stance)), codec.EncodePng(keyed));
        File.WriteAllBytes(project.PathOf(StudioProject.FrameFile(stance)), PngCodec.Encode(frame, alpha: false));
        var step = KeyframeStep(project, stance);
        step.Selected = rel;
        step.Basis = project.FirstFrameHash();
        project.Save();
        return new FigureOutcome(problems);
    }

    /// <summary>把一组宠物体系的模板加进规划（墙组、坐、睡要先有关键帧）。已经有的同 id 动作不重复加。</summary>
    public static int AddTemplates(StudioProject project, IEnumerable<ActionTemplate> templates)
    {
        var added = 0;
        foreach (var t in templates)
        {
            if (project.Data.Actions.Any(a => a.Id == t.Id)) continue;
            project.Data.Actions.Add(t.Plan());
            added++;
        }
        if (added > 0) project.Save();
        return added;
    }

    // ───── ④ ─────

    /// <summary>④ 确认规划：记下基于哪张首尾帧，之后换首尾帧规划就标过期。</summary>
    public string? ConfirmPlan(StudioProject project)
    {
        if (project.PlanProblem() is { } problem) return problem;
        if (project.Data.Prefix.Trim().Length == 0) project.Data.Prefix = StudioPrompts.Prefix(project.Data);
        project.Data.PlanBasis = project.FirstFrameHash();
        project.Save();
        return null;
    }

    public static CostEstimate VideoEstimate(StudioProject project, IReadOnlyList<PlannedAction> actions, MediaProvider provider)
    {
        var resolution = MediaPresets.FitResolution(provider, project.Data.Resolution);
        var rate = provider.PriceOf(resolution);
        // 估价和提交用同一个时长：超出模型范围的按夹过之后的算。
        var durations = actions.Select(a => (a.Name, Seconds: MediaPresets.FitDuration(provider, a.Duration))).ToList();
        var items = durations.Select(d => new CostItem($"{d.Name} · {d.Seconds} 秒", rate * d.Seconds)).ToList();
        var seconds = durations.Sum(d => d.Seconds);
        return new CostEstimate($"生成 {actions.Count} 段视频（共 {seconds} 秒，{resolution}）", items,
            rate * seconds, provider.Name, provider.Model);
    }

    // ───── ⑤ ─────

    /// <summary>
    /// ⑤ 生成视频：一次确认整批，然后逐段排队提交。返回每段的任务（下载完成时完成）；用户取消确认返回空。
    /// 提交失败的那段在它的任务里抛异常，不影响其他段，也不自动重试。
    /// </summary>
    public async Task<IReadOnlyList<(PlannedAction Action, Task<TaskRecord> Task)>> GenerateVideosAsync(
        StudioProject project, IReadOnlyList<PlannedAction> actions, MediaProvider provider, CancellationToken ct)
    {
        if (actions.Count == 0) return [];
        if (!File.Exists(project.PathOf(StudioProject.FirstFrameFile))) throw new InvalidOperationException("还没有首尾帧（第 ③ 步）");
        var estimate = VideoEstimate(project, actions, provider);
        if (!await gate.ConfirmAsync(estimate, ct)) return [];

        var frames = new Dictionary<string, byte[]>();
        async Task<byte[]> FrameOf(string stance)
        {
            if (frames.TryGetValue(stance, out var bytes)) return bytes;
            var file = project.PathOf(StudioProject.FrameFile(stance));
            if (!File.Exists(file)) throw new InvalidOperationException($"还没有「{StudioPrompts.StanceName(stance)}」关键帧（第 ③ 步）");
            return frames[stance] = await File.ReadAllBytesAsync(file, ct);
        }
        // 模型不支持所选分辨率（Fast / Mini 只到 720p）时按它支持的最高分辨率提交，和估价一致。
        var resolution = MediaPresets.FitResolution(provider, project.Data.Resolution);
        var rate = provider.PriceOf(resolution);
        var result = new List<(PlannedAction, Task<TaskRecord>)>();
        foreach (var action in actions)
        {
            // 首尾帧：首尾同图的用它所在姿态的关键帧；过渡动作首帧是起点姿态、尾帧是终点姿态（D7 方案 C）。
            // 时长夹到模型支持的范围（Seedance 2.x 是 4～15 秒），不然提交直接被拒。
            var seconds = MediaPresets.FitDuration(provider, action.Duration);
            var job = new VideoJob(project.FullPrompt(action), await FrameOf(action.StartStance), await FrameOf(action.EndStance),
                resolution, seconds);
            result.Add((action, queue.EnqueueAsync(project, action, provider, job, project.TakeBasis(action), rate * seconds, ct)));
        }
        return result;
    }

    // ───── ⑥ ─────

    public static (double Start, double End)? WalkWindow(PlannedAction action) =>
        action.Roles.Contains(Roles.Move) ? (action.Duration * 0.1, action.Duration * 0.9) : null;

    /// <summary>有步态的动作：地面走路和墙上攀爬。</summary>
    public static bool HasGait(PlannedAction action) => action.Roles.Contains(Roles.Move) || action.Roles.Contains(Roles.Climb);

    /// <summary>⑥ 处理一个 take：抠像、定位、编码成 webm，写质检结果和缩略图。本地运算，免费。</summary>
    public async Task<ClipReport> ProcessTakeAsync(StudioProject project, PlannedAction action, int take,
        IProgress<double>? progress, CancellationToken ct)
    {
        var report = await new ClipProcessor(ffmpeg).ProcessAsync(project.RawVideo(action.Id, take), project.ClipFile(action.Id, take),
            new ClipOptions
            {
                Key = FigureTools.ToKey(project.Data.KeyColor),
                IsMove = HasGait(action),
                IsDrag = action.Roles.Contains(Roles.Drag) || action.Roles.Contains(Roles.Fall),
                WalkWindow = WalkWindow(action),
                FrameLocked = action.Stance != Stances.Ground || action.To is not null,
                Transition = action.To is not null,
                Wall = action.Stance == Stances.Wall && action.To is null,
                // 倒挂片段左右会动（爬行），只按画面锁定，不查贴墙。
            }, progress, ct);
        SaveReport(project.QaFile(action.Id, take), report);
        if (report.Preview is { } preview)
        {
            var thumb = ThumbFile(project, action.Id, take);
            Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
            await File.WriteAllBytesAsync(thumb, codec.EncodePng(new RgbaImage(PetClip.Width, PetClip.Height, preview)), ct);
        }
        return report;
    }

    public static string ThumbFile(StudioProject project, string action, int take) => project.PathOf($"thumbs/{action}-take-{take:000}.png");

    public static void SaveReport(string path, ClipReport r)
    {
        var json = new JsonObject
        {
            ["frames"] = r.Frames, ["fps"] = r.Fps, ["loopIou"] = r.LoopIou, ["driftX"] = r.DriftX, ["driftFeet"] = r.DriftFeet,
            ["edgeLeak"] = r.EdgeLeak, ["haze"] = r.Haze, ["scaleChange"] = r.ScaleChange, ["missingFrames"] = r.MissingFrames,
            ["scale"] = r.Scale, ["key"] = r.Key?.ToString(),
            ["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray()),
        };
        if (r.Curve is { } curve) json["curve"] = new JsonArray(curve.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        if (r.ContactX is { } contact) json["contactX"] = contact;
        json["contactSpread"] = r.ContactSpread;
        if (r.LastContactX is { } last) json["lastContactX"] = last;
        if (r.FirstBounds is { } b) json["bounds"] = new JsonArray(b.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>读 ⑥ 写的质检结果；没有返回 null。</summary>
    public static JsonObject? LoadReport(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    // ───── ⑧ ─────

    /// <summary>包 id：[a-z0-9-]，从名字里挑能用的字符，挑不出来用 pet-&lt;工程 id&gt;。</summary>
    public static string SuggestId(string name, string projectId)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var id = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (id.Length > 32) id = id[..32].TrimEnd('-');
        return id.Length >= 2 ? id : "pet-" + projectId.Replace("_", "-").ToLowerInvariant();
    }

    public PackSpec BuildSpec(StudioProject project)
    {
        var data = project.Data;
        var export = data.Export;
        var name = export.Name.Trim() is { Length: > 0 } n ? n : data.Card.Name.Trim() is { Length: > 0 } cn ? cn : data.Name;
        // 不完整的姿态组（比如只做了贴墙待机没做上墙）先去掉，不让坏包出门（design/pet-life/05 §1 ⑧）。
        var keep = StanceGraph.Prune(project.Sketch()).Select(a => a.Id).ToHashSet();
        var actions = new List<PackAction>();
        foreach (var a in data.Actions.Where(a => keep.Contains(a.Id)))
        {
            var take = project.AdoptedTake(a) ?? throw new InvalidOperationException($"「{a.Name}」还没有采用的片段（第 ⑥ 步）");
            var report = LoadReport(project.QaFile(a.Id, take.Take));
            IReadOnlyList<double>? curve = null;
            double? stride = null;
            if (HasGait(a))
            {
                stride = a.Stride ?? (a.Roles.Contains(Roles.Climb) && a.Stance == Stances.Wall ? DefaultClimbStride : DefaultStride);
                // 步态曲线按脚底附近的腿部区域量；倒挂时腿在画面上方量不到，按帧数匀速走。
                curve = a.Stance != Stances.Ceiling && report?["curve"] is JsonArray c ? c.Select(v => (double)v!).ToList() : null;
            }
            // 贴墙的接触线：墙上的动作量整段的中位数，上墙过渡量最后一帧。
            double? contact = a.Stance == Stances.Wall && a.To is null ? (double?)report?["contactX"]
                : a.To == Stances.Wall ? (double?)report?["lastContactX"] : null;
            actions.Add(new PackAction(a.Id, a.Name, ClipText(a.Description, ActionManifest.MaxDescription), a.Roles,
                project.ClipFile(a.Id, take.Take), ActionManifest.CleanLines(a.Lines), a.Mirror, stride, curve)
            {
                Stance = a.Stance, To = a.To, ContactX = contact, Vertical = a.Vertical,
            });
        }

        var card = data.Card;
        var persona = new JsonObject
        {
            ["name"] = name,
            ["identity"] = StudioPrompts.Appearance(data),
            ["personality"] = card.Personality,
            ["speech"] = card.Speech,
            ["likes"] = new JsonArray(card.Likes.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
            ["catchphrases"] = new JsonArray(),
            ["greeting"] = card.Greeting,
        };

        var sources = new Dictionary<string, byte[]>();
        if (export.IncludeSource)
        {
            void Add(string? rel, string name)
            {
                if (rel is not null && File.Exists(project.PathOf(rel))) sources[name] = File.ReadAllBytes(project.PathOf(rel));
            }
            Add(data.Sheet.Selected, "sheet.png");
            Add(StudioProject.KeyedFigureFile, "figure.png");
            Add(StudioProject.FirstFrameFile, "first_frame.png");
            foreach (var (stance, _) in StudioPrompts.KeyframeStances)
                Add(StudioProject.FrameFile(stance), $"keyframe-{stance}.png");
            sources["studio.json"] = System.Text.Encoding.UTF8.GetBytes(StudioSource(project).ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }

        var videoModel = project.AllTakes().Select(t => t.Model).FirstOrDefault(m => m.Length > 0);
        return new PackSpec
        {
            Id = export.Id.Trim() is { Length: > 0 } id ? id : SuggestId(name, project.Id),
            Name = name,
            Author = export.Author.Trim(),
            License = export.License.Trim(),
            LicenseUrl = export.LicenseUrl.Trim(),
            Homepage = export.Homepage.Trim(),
            Version = export.Version.Trim() is { Length: > 0 } ver ? ver : "1.0.0",
            Credits = ["用 Astra Pet 工作台制作" + (videoModel is null ? "" : $"；视频由 {videoModel} 生成")],
            MinAppVersion = AppVersion.Current.Split('-')[0],
            Persona = persona,
            PromptMarkdown = data.Prompt.Trim() is { Length: > 0 } prompt ? prompt : null,
            Profile = Profile(project),
            Actions = actions,
            Preview = PreviewGrid(project),
            Icon = Icon(project),
            Sources = sources,
        };
    }

    private static string ClipText(string text, int max) => text.Length <= max ? text : text[..max];

    public const double DefaultClimbStride = 100;

    /// <summary>
    /// pet.json 的 profile（design/pet-life/01）：① 里填的性格、物种、口味；身体范围从待机片段第一帧量，
    /// 贴墙的接触线和最低点从贴墙待机量。
    /// </summary>
    public static JsonObject Profile(StudioProject project)
    {
        var d = project.Data;
        var traits = new JsonObject();
        foreach (var (key, _, _, _) in PetTraits.All)
            traits[key] = Math.Round(d.Profile.Traits.GetValueOrDefault(key, 50));
        var profile = new JsonObject
        {
            ["species"] = PetProfile.SpeciesList.Contains(d.Profile.Species) ? d.Profile.Species : "humanoid",
            ["traits"] = traits,
            ["diet"] = new JsonObject
            {
                ["likes"] = new JsonArray(d.Profile.Likes.Where(Foods.Exists).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                ["dislikes"] = new JsonArray(d.Profile.Dislikes.Where(Foods.Exists).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            },
        };

        JsonObject? ReportOf(Func<PlannedAction, bool> match) =>
            d.Actions.FirstOrDefault(a => match(a) && project.AdoptedTake(a) is not null) is { } a
                ? LoadReport(project.QaFile(a.Id, project.AdoptedTake(a)!.Take)) : null;

        var body = new JsonObject();
        if (ReportOf(a => a.Stance == Stances.Ground && a.To is null && a.Roles.Contains(Roles.Idle))?["bounds"] is JsonArray bounds)
            body["bounds"] = bounds.DeepClone();
        if (ReportOf(a => a.Stance == Stances.Wall && a.To is null && a.Roles.Contains(Roles.Idle)) is { } wall)
        {
            var w = new JsonObject();
            if (wall["contactX"] is JsonNode cx) w["contactX"] = Math.Round((double)cx, 1);
            if (wall["bounds"] is JsonArray wb && wb.Count == 4) w["feetY"] = (double)wb[3]!;
            body["wall"] = w;
        }
        if (ReportOf(a => a.Stance == Stances.Ceiling && a.To is null && a.Roles.Contains(Roles.Idle))?["bounds"] is JsonArray cb && cb.Count == 4)
            body["ceiling"] = new JsonObject { ["contactY"] = (double)cb[1]! };
        if (body.Count > 0) profile["body"] = body;
        return profile;
    }

    /// <summary>source/studio.json：设定卡、公共前缀、每个动作的提示词和参数。不含提供商地址、密钥、账本（07 §3）。</summary>
    public static JsonObject StudioSource(StudioProject project)
    {
        var d = project.Data;
        return new JsonObject
        {
            ["version"] = 1,
            ["description"] = d.Description,
            ["style"] = d.Style,
            ["headRatio"] = d.HeadRatio,
            ["companion"] = d.Companion,
            ["card"] = JsonSerializer.SerializeToNode(d.Card, StudioJsonContext.Default.CharacterCard),
            ["keyColor"] = d.KeyColor,
            ["prefix"] = d.Prefix,
            ["resolution"] = d.Resolution,
            ["sheetPrompt"] = d.Sheet.Prompt,
            ["figurePrompt"] = d.Figure.Prompt,
            ["actions"] = new JsonArray(d.Actions.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id, ["name"] = a.Name, ["roles"] = new JsonArray(a.Roles.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
                ["prompt"] = a.Prompt, ["duration"] = a.Duration, ["template"] = a.Template,
                ["stance"] = a.Stance, ["to"] = a.To,
            }).ToArray()),
            ["profile"] = JsonSerializer.SerializeToNode(d.Profile, StudioJsonContext.Default.StudioProfile),
        };
    }

    /// <summary>预览图：每个动作的缩略图拼成网格（等同 tools/petgen/preview.py），透明底。</summary>
    public byte[]? PreviewGrid(StudioProject project)
    {
        var thumbs = project.Data.Actions
            .Select(a => project.AdoptedTake(a) is { } t ? ThumbFile(project, a.Id, t.Take) : null)
            .Where(f => f is not null && File.Exists(f)).Select(f => codec.Decode(File.ReadAllBytes(f!))).ToList();
        if (thumbs.Count == 0) return null;
        const int cellW = 320, cellH = 180;
        var cols = Math.Min(4, thumbs.Count);
        var rows = (thumbs.Count + cols - 1) / cols;
        var grid = RgbaImage.Blank(cols * cellW, rows * cellH);
        for (var i = 0; i < thumbs.Count; i++)
        {
            var t = FigureTools.Thumbnail(thumbs[i], cellW);
            int ox = i % cols * cellW + (cellW - t.Width) / 2, oy = i / cols * cellH + (cellH - t.Height) / 2;
            for (var y = 0; y < t.Height; y++)
                Array.Copy(t.Pixels, y * t.Width * 4, grid.Pixels, ((oy + y) * grid.Width + ox) * 4, t.Width * 4);
        }
        return codec.EncodePng(grid);
    }

    /// <summary>256×256 头像：抠好的站姿上半身（头身比再小也能看清脸），透明底。</summary>
    public byte[]? Icon(StudioProject project)
    {
        var path = project.PathOf(StudioProject.KeyedFigureFile);
        if (!File.Exists(path)) return null;
        var keyed = codec.Decode(File.ReadAllBytes(path));
        if (FigureTools.Bounds(keyed, 16) is not { } box) return null;
        var bw = box.Right - box.Left + 1;
        var side = Math.Min(Math.Max(bw, (box.Bottom - box.Top + 1) / 2), keyed.Height - box.Top);
        var left = Math.Clamp((box.Left + box.Right) / 2 - side / 2, 0, Math.Max(0, keyed.Width - side));
        var square = RgbaImage.Blank(side, side);
        for (var y = 0; y < side && box.Top + y < keyed.Height; y++)
        {
            var count = Math.Min(side, keyed.Width - left);
            Array.Copy(keyed.Pixels, ((box.Top + y) * keyed.Width + left) * 4, square.Pixels, y * side * 4, count * 4);
        }
        return codec.EncodePng(FigureTools.Thumbnail(square, 256));
    }

    /// <summary>
    /// ⑧ 导出 .astrapet，并用和程序加载角色完全一样的路径校验一遍（<see cref="PetPack.Inspect"/> + <see cref="PetLibrary.Load"/>）。
    /// 校验不过删掉产物并抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static bool IsValidVersion(string v) => System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,5}\.\d{1,5}\.\d{1,5}$");

    /// <summary>补丁号加一：1.0.3 → 1.0.4。</summary>
    public static string NextVersion(string v)
    {
        var p = v.Split('.');
        return $"{p[0]}.{p[1]}.{int.Parse(p[2]) + 1}";
    }

    public void Export(StudioProject project, string path, IClipDecoder decoder, string cacheRoot)
    {
        if (project.PlanProblem() is { } planProblem) throw new InvalidDataException(planProblem);
        var spec = BuildSpec(project);
        if (!IsValidVersion(spec.Version)) throw new InvalidDataException($"版本号「{spec.Version}」不合法：要写成 1.0.0 这样的三段数字");
        if (!ActionDef.IsValidId(spec.Id)) throw new InvalidDataException($"包 id「{spec.Id}」不合法：只能用小写字母、数字和短横线，最长 32 个字符");
        if (spec.PromptMarkdown is { } md && System.Text.Encoding.UTF8.GetByteCount(md) > PetPack.MaxPromptBytes)
            throw new InvalidDataException($"自定义提示词太长了（上限 {PetPack.MaxPromptBytes / 1024 / 1024} MB）");
        PetPackWriter.Write(spec, path);
        try
        {
            var (_, problem) = PetPack.Inspect(path);
            if (problem is not null) throw new InvalidDataException(problem);
            var dir = PetPack.Extract(path, cacheRoot);
            var logs = new List<string>();
            var library = PetLibrary.Load(dir, decoder, logs.Add);
            var skipped = logs.Where(l => l.StartsWith("跳过", StringComparison.Ordinal)).ToList();
            if (skipped.Count > 0 || library.Actions.All.Count != spec.Actions.Count)
                throw new InvalidDataException("有动作没通过加载检查：" + string.Join("；", skipped));
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
        project.Data.Export.Id = spec.Id;
        project.Data.Export.Version = NextVersion(spec.Version);
        project.Data.Export.Basis = project.ExportBasis();
        project.Data.Export.LastPath = path;
        project.Save();
    }
}
