using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraPet.Core.Studio;

/// <summary>工作台的九步（design/pet-studio/04 §2）。</summary>
public enum StepId { Bind, Describe, Sheet, Figure, Plan, Generate, Process, Tryout, Export }

/// <summary>步骤状态：由输入哈希和产物是否存在推出来，不单独存（04 §6）。</summary>
public enum StepState { Locked, Ready, Running, Done, Stale }

/// <summary>角色设定卡（04 §3 ①）：后面所有提示词从这里填。对话模型「帮我完善」生成，用户可改。</summary>
public sealed class CharacterCard
{
    public string Name { get; set; } = "";
    public string Species { get; set; } = "";
    /// <summary>一句话外观，填进视频提示词的公共前缀。</summary>
    public string Appearance { get; set; } = "";
    public List<string> Palette { get; set; } = [];
    public List<string> Props { get; set; } = [];
    public string Personality { get; set; } = "";
    public string Speech { get; set; } = "";
    public List<string> Likes { get; set; } = [];
    public string Greeting { get; set; } = "";
}

/// <summary>一步图片生成（设定图、站姿）：候选、选中的一张、生成时的上游哈希。</summary>
public sealed class ImageStep
{
    public string Prompt { get; set; } = "";
    /// <summary>相对工程目录的路径，比如 sheet/cand-001.png。</summary>
    public List<string> Candidates { get; set; } = [];
    public string? Selected { get; set; }
    /// <summary>选中的这张是基于哪个上游版本做的（<see cref="StudioProject.DescribeHash"/> 等）。上游变了这一步就过期。</summary>
    public string Basis { get; set; } = "";
}

/// <summary>④ 规划里的一个动作。</summary>
public sealed class PlannedAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Roles { get; set; } = [];
    /// <summary>动作提示词（不含公共前缀）。</summary>
    public string Prompt { get; set; } = "";
    public int Duration { get; set; } = 8;
    public List<string> Lines { get; set; } = [];
    public string Template { get; set; } = "";
    /// <summary>⑥ 采用的 take 编号；null 表示还没选。</summary>
    public int? Adopted { get; set; }
    /// <summary>移动动作的步幅（像素 / 循环），⑦ 里调。</summary>
    public double? Stride { get; set; }
    public bool Mirror { get; set; } = true;
    /// <summary>在哪个姿态下播（design/pet-life/05 §1 ④）；过渡动作是起点姿态。</summary>
    public string Stance { get; set; } = Stances.Ground;
    /// <summary>过渡动作的终点姿态；null 表示首尾帧是同一张。</summary>
    public string? To { get; set; }
    /// <summary>步态沿 Y 轴（攀爬）。</summary>
    public bool Vertical { get; set; }

    /// <summary>首帧 / 尾帧用哪个姿态的关键帧（松手的终点是空中，尾帧仍是贴墙）。</summary>
    public string StartStance => Stance;
    public string EndStance => To is { } to && to != Stances.Air ? to : Stance;
}

/// <summary>① 里填的档案（design/pet-life/05 §1 ①）：性格、物种、口味，导出时写进 pet.json 的 profile。</summary>
public sealed class StudioProfile
{
    public string Species { get; set; } = "humanoid";
    public Dictionary<string, double> Traits { get; set; } = new();
    public List<string> Likes { get; set; } = [];
    public List<string> Dislikes { get; set; } = [];
}

/// <summary>⑧ 导出的元数据。</summary>
public sealed class ExportInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string License { get; set; } = "保留所有权利";
    public string LicenseUrl { get; set; } = "";
    public string Homepage { get; set; } = "";
    public bool IncludeSource { get; set; } = true;
    /// <summary>上次导出时的成品哈希；和现在一样说明导出步骤已完成。</summary>
    public string Basis { get; set; } = "";
    public string LastPath { get; set; } = "";
}

/// <summary>project.json 的内容。<see cref="Version"/> 以后改结构时迁移。</summary>
public sealed class ProjectData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    // ① 描述角色
    public string Description { get; set; } = "";
    /// <summary>用户的参考图，复制进 inputs/，这里存相对路径。</summary>
    public List<string> References { get; set; } = [];
    public string Style { get; set; } = StudioPrompts.Styles[0];
    /// <summary>Q 版头身比；0 表示动物 / 非人形。</summary>
    public double HeadRatio { get; set; } = 3.5;
    public string Companion { get; set; } = "";
    public CharacterCard Card { get; set; } = new();
    public StudioProfile Profile { get; set; } = new();

    // ② ③
    public ImageStep Sheet { get; set; } = new();
    public ImageStep Figure { get; set; } = new();
    /// <summary>抠像色：green / blue / magenta，③ 自动选，可改。</summary>
    public string KeyColor { get; set; } = "green";
    /// <summary>first_frame.png 是用哪张站姿、哪个抠像色合成的。</summary>
    public string FirstFrameBasis { get; set; } = "";
    /// <summary>③ 选装姿态的关键帧（贴墙、坐、睡）：候选和选中的一张。合成好的帧在 keyframes/&lt;姿态&gt;.png。</summary>
    public Dictionary<string, ImageStep> Keyframes { get; set; } = new();

    // ④
    public string Prefix { get; set; } = "";
    public string Resolution { get; set; } = "720p";
    public List<PlannedAction> Actions { get; set; } = [];
    /// <summary>规划确认时的首尾帧哈希；换了首尾帧，公共前缀里的外观和抠像色可能要改，规划标成过期。</summary>
    public string PlanBasis { get; set; } = "";

    // ⑧
    public ExportInfo Export { get; set; } = new();
}

/// <summary>
/// 一个工作台工程：<c>&lt;数据目录&gt;/studio/&lt;id&gt;/</c> 下的 project.json 和产物文件（04 §4）。
/// 步骤状态从文件和哈希推出来，见 <see cref="StateOf"/>。只在 UI 线程上改 <see cref="Data"/>。
/// </summary>
public sealed class StudioProject
{
    public const string FileName = "project.json";
    public const string FirstFrameFile = "first_frame.png";
    public const string KeyedFigureFile = "figure/keyed.png";
    public const string LedgerFile = "ledger.jsonl";

    private StudioProject(string directory, ProjectData data)
    {
        Directory = directory;
        Data = data;
    }

    public string Directory { get; }
    public ProjectData Data { get; }
    public string Id => Data.Id;

    /// <summary>某个姿态合成好的首尾帧（1280×720 纯色底）：地面是 first_frame.png，其他在 keyframes/。</summary>
    public static string FrameFile(string stance) => stance == Stances.Ground ? FirstFrameFile : $"keyframes/{stance}.png";

    public static string KeyedKeyframeFile(string stance) => $"keyframes/{stance}-keyed.png";

    public bool HasFrame(string stance) => File.Exists(PathOf(FrameFile(stance)));

    public string PathOf(string relative) => System.IO.Path.Combine(Directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static StudioProject Create(string root, string name)
    {
        var id = NewId(root);
        var dir = System.IO.Path.Combine(root, id);
        System.IO.Directory.CreateDirectory(dir);
        var project = new StudioProject(dir, new ProjectData { Id = id, Name = name.Trim() is { Length: > 0 } n ? n : "新宠物" });
        project.Data.Actions = StudioPrompts.BaseKit().ToList();
        project.Save();
        return project;
    }

    public static StudioProject Load(string directory)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(directory, FileName));
        var data = JsonSerializer.Deserialize(json, StudioJsonContext.Default.ProjectData)
                   ?? throw new InvalidDataException("project.json 是空的");
        if (data.Version > ProjectData.CurrentVersion)
            throw new InvalidDataException($"工程格式 {data.Version} 太新，请升级 Astra Pet");
        data.Id = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        FixShortDurations(data);
        return new StudioProject(directory, data);
    }

    /// <summary>
    /// 早期的落地、松手、从天花板松手模板是 3 秒，视频模型不接受（最少 4 秒），提交必定失败。
    /// 读工程时把不到 4 秒的动作提到 4 秒；提示词没改过的话按新时长重写时间轴。
    /// </summary>
    private static void FixShortDurations(ProjectData data)
    {
        foreach (var action in data.Actions.Where(a => a.Duration < StudioPrompts.MinDuration))
        {
            var template = StudioPrompts.Template(action.Template);
            var unedited = template is not null && action.Prompt.Trim() == template.Render(action.Duration);
            action.Duration = StudioPrompts.MinDuration;
            if (unedited) action.Prompt = template!.Render(action.Duration);
        }
    }

    /// <summary>工作台首页：所有能读的工程，新的在前。坏掉的跳过并记日志。</summary>
    public static List<StudioProject> List(string root)
    {
        var result = new List<StudioProject>();
        if (!System.IO.Directory.Exists(root)) return result;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(System.IO.Path.Combine(dir, FileName))) continue;
            try { result.Add(Load(dir)); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                PetLog.Write($"读工作台工程失败 {dir}: {e.Message}");
            }
        }
        return result.OrderByDescending(p => p.Data.Updated).ToList();
    }

    /// <summary>先写临时文件再改名，写到一半被杀不会把 project.json 写坏。</summary>
    public void Save()
    {
        Data.Updated = DateTime.Now;
        var path = System.IO.Path.Combine(Directory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Data, StudioJsonContext.Default.ProjectData));
        File.Move(temp, path, overwrite: true);
    }

    private static string NewId(string root)
    {
        System.IO.Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd");
        for (var i = 1; ; i++)
        {
            var id = $"{stamp}-{i}";
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(root, id))) return id;
        }
    }

    // ───── 文件 ─────

    /// <summary>下一个候选文件名：<c>sheet/cand-003.png</c>。</summary>
    public string NextCandidate(string folder)
    {
        System.IO.Directory.CreateDirectory(PathOf(folder));
        for (var i = 1; ; i++)
        {
            var rel = $"{folder}/cand-{i:000}.png";
            if (!File.Exists(PathOf(rel))) return rel;
        }
    }

    public string RawDir(string action) => PathOf($"raw/{action}");
    public string TakeStem(string action, int take) => $"take-{take:000}";
    public string TaskFile(string action, int take) => PathOf($"raw/{action}/take-{take:000}.task.json");
    public string RawVideo(string action, int take) => PathOf($"raw/{action}/take-{take:000}.mp4");
    public string ClipFile(string action, int take) => PathOf($"clips/{action}/take-{take:000}.webm");
    public string QaFile(string action, int take) => PathOf($"qa/{action}/take-{take:000}.json");

    /// <summary>这个动作的所有 take（按编号），从任务记录读。</summary>
    public List<TaskRecord> Takes(string action)
    {
        var dir = RawDir(action);
        if (!System.IO.Directory.Exists(dir)) return [];
        var takes = new List<TaskRecord>();
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "take-*.task.json"))
            if (TaskRecord.TryLoad(file) is { } record) takes.Add(record);
        return takes.OrderBy(t => t.Take).ToList();
    }

    /// <summary>所有动作的所有 take，程序启动时恢复轮询用。</summary>
    public IEnumerable<TaskRecord> AllTakes() => Data.Actions.SelectMany(a => Takes(a.Id));

    public int NextTake(string action) => Takes(action).Select(t => t.Take).DefaultIfEmpty(0).Max() + 1;

    public void AppendLedger(LedgerEntry entry) => Ledger.Append(PathOf(LedgerFile), entry);
    public double LedgerTotal() => Ledger.Read(PathOf(LedgerFile)).Sum(e => e.Estimate ?? 0);

    // ───── 哈希 ─────

    public static string Hash(params string[] parts)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\u0001", parts));
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    public string FileHash(string? relative)
    {
        if (relative is null) return "";
        var info = new FileInfo(PathOf(relative));
        if (!info.Exists) return "";
        var stamp = (info.Length, info.LastWriteTimeUtc);
        lock (_hashes)
            if (_hashes.TryGetValue(info.FullName, out var cached) && cached.Stamp == stamp) return cached.Hash;
        using var stream = info.OpenRead();
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
        lock (_hashes) _hashes[info.FullName] = (stamp, hash);
        return hash;
    }

    /// <summary>状态机反复要首尾帧、设定图的哈希，按大小和修改时间缓存。</summary>
    private readonly Dictionary<string, ((long, DateTime) Stamp, string Hash)> _hashes = new();

    /// <summary>① 的全部输入。</summary>
    public string DescribeHash() => Hash(
        Data.Description.Trim(), Data.Style, Data.HeadRatio.ToString("0.##"), Data.Companion.Trim(),
        JsonSerializer.Serialize(Data.Card, StudioJsonContext.Default.CharacterCard),
        string.Join(",", Data.References.Select(FileHash)));

    /// <summary>③ 站姿的上游：选中的设定图。</summary>
    public string FigureBasis() => Hash(FileHash(Data.Sheet.Selected));

    /// <summary>首尾帧的上游：选中的站姿和抠像色。</summary>
    public string FirstFrameBasisNow() => Hash(FileHash(Data.Figure.Selected), Data.KeyColor);

    public string FirstFrameHash() => FileHash(FirstFrameFile);

    /// <summary>一个 take 的输入：首尾帧、完整提示词、时长、分辨率。这些都没变，take 就不过期。</summary>
    public string TakeBasis(PlannedAction action) => action.Stance == Stances.Ground && action.To is null
        ? Hash(FirstFrameHash(), FullPrompt(action), action.Duration.ToString(), Data.Resolution)
        : Hash(FileHash(FrameFile(action.StartStance)), FileHash(FrameFile(action.EndStance)), FullPrompt(action),
            action.Duration.ToString(), Data.Resolution);

    public string FullPrompt(PlannedAction action) => StudioPrompts.PrefixFor(Data, action) + action.Prompt.Trim();

    // ───── 状态机 ─────

    public bool HasDescription => Data.Description.Trim().Length > 0 || Data.References.Count > 0;

    /// <param name="imageReady">图片模型已经绑好。</param>
    /// <param name="running">这一步现在有操作在跑（UI 层知道）。</param>
    public StepState StateOf(StepId step, bool imageReady, Func<StepId, bool>? running = null)
    {
        if (running?.Invoke(step) == true) return StepState.Running;
        if (step == StepId.Bind) return imageReady ? StepState.Done : StepState.Ready;
        if (!imageReady) return StepState.Locked;

        var previous = step - 1;
        if (previous > StepId.Bind && StateOf(previous, imageReady) is StepState.Locked or StepState.Ready)
            return StepState.Locked;

        switch (step)
        {
            case StepId.Describe:
                return HasDescription ? StepState.Done : StepState.Ready;
            case StepId.Sheet:
                if (Data.Sheet.Selected is null || !File.Exists(PathOf(Data.Sheet.Selected))) return StepState.Ready;
                return Data.Sheet.Basis == DescribeHash() ? StepState.Done : StepState.Stale;
            case StepId.Figure:
                if (Data.Figure.Selected is null || !File.Exists(PathOf(FirstFrameFile))) return StepState.Ready;
                return Data.Figure.Basis == FigureBasis() && Data.FirstFrameBasis == FirstFrameBasisNow()
                    ? StepState.Done : StepState.Stale;
            case StepId.Plan:
                if (Data.PlanBasis.Length == 0 || PlanProblem() is not null) return StepState.Ready;
                return Data.PlanBasis == FirstFrameHash() ? StepState.Done : StepState.Stale;
            case StepId.Generate:
            {
                var states = Data.Actions.Select(a => (Action: a, Takes: Takes(a.Id))).ToList();
                if (states.Any(s => s.Takes.Count == 0 || !s.Takes.Any(t => t.HasVideo(this)))) return StepState.Ready;
                return states.All(s => s.Takes.Any(t => t.HasVideo(this) && t.Basis == TakeBasis(s.Action)))
                    ? StepState.Done : StepState.Stale;
            }
            case StepId.Process:
            case StepId.Tryout:
            {
                if (Data.Actions.Any(a => AdoptedTake(a) is null)) return StepState.Ready;
                return Data.Actions.All(a => AdoptedTake(a)!.Basis == TakeBasis(a)) ? StepState.Done : StepState.Stale;
            }
            case StepId.Export:
                return Data.Export.Basis.Length > 0 && Data.Export.Basis == ExportBasis() ? StepState.Done : StepState.Ready;
        }
        return StepState.Ready;
    }

    /// <summary>采用的 take：任务记录在、处理好的 webm 在。</summary>
    public TaskRecord? AdoptedTake(PlannedAction action) =>
        action.Adopted is { } n && File.Exists(ClipFile(action.Id, n)) ? Takes(action.Id).FirstOrDefault(t => t.Take == n) : null;

    /// <summary>导出的成品由什么决定：采用的片段和动作定义。</summary>
    public string ExportBasis() => Hash(Data.Actions.Select(a =>
        $"{a.Id}|{a.Name}|{a.Description}|{string.Join(",", a.Roles)}|{string.Join("/", a.Lines)}|{a.Stride}|{a.Mirror}|{a.Adopted}|{a.Stance}|{a.To}").ToArray()
        .Append(System.Text.Json.JsonSerializer.Serialize(Data.Profile, StudioJsonContext.Default.StudioProfile)).ToArray());

    /// <summary>④ 的问题：缺必需角色、id 重复、没写提示词。null 表示没问题。</summary>
    public string? PlanProblem()
    {
        if (Data.Actions.Count == 0) return "还没有动作";
        var dup = Data.Actions.GroupBy(a => a.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return $"动作 id 重复：{dup.Key}";
        if (Data.Actions.FirstOrDefault(a => !ActionDef.IsValidId(a.Id)) is { } bad) return $"动作 id 不合法：「{bad.Id}」";
        if (Data.Actions.FirstOrDefault(a => a.Prompt.Trim().Length == 0) is { } empty) return $"「{empty.Name}」还没写提示词";
        if (Data.Actions.FirstOrDefault(a => a.Roles.Count == 0 && a.To is null) is { } noRole) return $"「{noRole.Name}」没有角色";
        var missing = Core.Roles.Required.Where(r => Data.Actions.All(a => !(a.Roles.Contains(r) && a.Stance == Stances.Ground && a.To is null))).ToList();
        if (missing.Count > 0) return $"缺少必需角色：{string.Join("、", missing)}";
        // 工作台的必需动作（宠物体系的地面常用 + 墙组）：按模板认，改过名字和 id 也算。
        var missingLife = StudioPrompts.RequiredTemplates
            .Where(t => t.Stance != Stances.Ground || t.To is not null || !Core.Roles.Required.Intersect(t.Roles).Any())
            .Where(t => Data.Actions.All(a => a.Template != t.Id && a.Id != t.Id)).Select(t => t.Name).ToList();
        if (missingLife.Count > 0) return $"缺少必需动作：{string.Join("、", missingLife)}（点「补上必需动作」一键加上）";
        foreach (var a in Data.Actions)
            foreach (var stance in new[] { a.StartStance, a.EndStance }.Distinct())
                if (stance != Stances.Ground && !HasFrame(stance))
                    return Data.Keyframes.TryGetValue(stance, out var step) && step.Candidates.Count > 0
                        ? $"「{StudioPrompts.StanceName(stance)}」关键帧已经生成了候选，还没选定：回第 ③ 步在「{StudioPrompts.StanceName(stance)}」下面点一张"
                        : $"「{a.Name}」要用「{StudioPrompts.StanceName(stance)}」关键帧，先在第 ③ 步生成";
        return null;
    }

    /// <summary>规划成动作定义（只看姿态和角色，不含片段），给姿态图判断哪些组不完整。</summary>
    public IReadOnlyList<ActionDef> Sketch() => Data.Actions.Select(a => new ActionDef
    {
        Id = a.Id, Name = a.Name, Roles = a.Roles, Stance = a.Stance,
        Transition = a.To is { } to ? new StanceTransition(a.Stance, to) : null,
        Clips = [new PetClip(a.Id, a.Id, "", 24, 1)],
    }).ToList();

    /// <summary>④ 的提醒（不挡住确认）：不完整的姿态组导出时会被去掉（05 §1 ⑧）。</summary>
    public IReadOnlyList<string> PlanWarnings()
    {
        var sketch = Sketch();
        var graph = new StanceGraph(sketch);
        return sketch.Select(a => a.Stance).Concat(sketch.Where(a => a.Transition is not null).Select(a => a.Transition!.To))
            .Where(s => s != Stances.Air && !graph.IsAvailable(s)).Distinct()
            .Select(s => $"「{StudioPrompts.StanceName(s)}」这一组暂不可用（{graph.Explain(sketch, s)}），导出时会先去掉").ToList();
    }

    /// <summary>改了 <paramref name="changed"/>，下游哪些已经完成的步骤会过期（只标记，不删产物）。</summary>
    public IReadOnlyList<StepId> Invalidated(StepId changed, bool imageReady = true)
    {
        var result = new List<StepId>();
        for (var s = changed + 1; s <= StepId.Export; s++)
            if (StateOf(s, imageReady) == StepState.Stale) result.Add(s);
        return result;
    }
}

/// <summary>
/// 一个视频任务的记录（<c>raw/&lt;动作&gt;/take-NNN.task.json</c>）。<b>提交成功后立刻写</b>，再开始轮询；
/// 程序重启后照它接着查同一个任务，绝不重复提交。不含密钥和图片 base64。
/// </summary>
public sealed class TaskRecord
{
    public string Action { get; set; } = "";
    public int Take { get; set; }
    public string TaskId { get; set; } = "";
    public VideoTaskState State { get; set; } = VideoTaskState.Queued;
    /// <summary>视频已下载到 take-NNN.mp4。</summary>
    public bool Downloaded { get; set; }
    /// <summary>任务成功但下载失败（链接过期）。</summary>
    public string? DownloadError { get; set; }
    public string ProviderId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Resolution { get; set; } = "";
    public int Duration { get; set; }
    public string Basis { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long? UsageTokens { get; set; }
    public string? Error { get; set; }
    /// <summary>视频链接（约 24 小时有效），下载失败时可以再试。</summary>
    public string? VideoUrl { get; set; }

    [JsonIgnore] public string? FilePath { get; private set; }

    public bool IsFinished => State is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled or VideoTaskState.Expired;

    /// <summary>还要不要轮询：没结束，或成功了但还没下载、也没确定下载不了。</summary>
    public bool NeedsWork => !IsFinished || (State == VideoTaskState.Succeeded && !Downloaded && DownloadError is null);

    public bool HasVideo(StudioProject project) => Downloaded && File.Exists(project.RawVideo(Action, Take));

    public static TaskRecord? TryLoad(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllText(path), StudioJsonContext.Default.TaskRecord);
            if (record is not null) record.FilePath = path;
            return record;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            PetLog.Write($"读任务记录失败 {path}: {e.Message}");
            return null;
        }
    }

    public void Save(string path)
    {
        UpdatedAt = DateTime.Now;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, StudioJsonContext.Default.TaskRecord));
        File.Move(temp, path, overwrite: true);
        FilePath = path;
    }
}

/// <summary>账本的一行：一次付费调用（05 §4）。不写密钥。</summary>
public sealed class LedgerEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Step { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Summary { get; set; } = "";
    public double? Estimate { get; set; }
    public string? TaskId { get; set; }
    public string? Usage { get; set; }
}

public static class Ledger
{
    private static readonly Lock Gate = new();

    public static void Append(string path, LedgerEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, StudioJsonContext.Compact.LedgerEntry);
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + "\n");
        }
    }

    public static List<LedgerEntry> Read(string path)
    {
        var result = new List<LedgerEntry>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, StudioJsonContext.Compact.LedgerEntry) is { } e) result.Add(e);
            }
            catch (JsonException) { }   // 写到一半被杀的最后一行
        }
        return result;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectData))]
[JsonSerializable(typeof(TaskRecord))]
[JsonSerializable(typeof(LedgerEntry))]
[JsonSerializable(typeof(CharacterCard))]
[JsonSerializable(typeof(StudioProfile))]
public sealed partial class StudioJsonContext : JsonSerializerContext
{
    /// <summary>一行一条的账本用不缩进的。</summary>
    public static StudioJsonContext Compact { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });   // 账本里没有枚举
}
