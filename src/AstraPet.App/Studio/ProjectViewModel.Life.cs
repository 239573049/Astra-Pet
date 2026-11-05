using System.Collections.ObjectModel;
using AstraPet.Core;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using Avalonia.Media.Imaging;

namespace AstraPet.Studio;

/// <summary>① 里的一个性格滑块。</summary>
public sealed class StudioTrait(StudioProfile profile, string key, string label) : Observable
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public double Value
    {
        get => profile.Traits.GetValueOrDefault(Key, 50);
        set { profile.Traits[Key] = Math.Round(Math.Clamp(value, 0, 100)); Raise(nameof(Value)); Raise(nameof(ValueText)); }
    }
    public string ValueText => $"{Value:0}";
}

/// <summary>① 里的一个食物：普通 / 喜欢 / 讨厌。</summary>
public sealed class StudioFood(StudioProfile profile, Food food) : Observable
{
    public string Label { get; } = $"{food.Icon} {food.Name}";
    public string Taste
    {
        get => profile.Likes.Contains(food.Id) ? "like" : profile.Dislikes.Contains(food.Id) ? "dislike" : "normal";
        set
        {
            profile.Likes.Remove(food.Id);
            profile.Dislikes.Remove(food.Id);
            if (value == "like") profile.Likes.Add(food.Id);
            else if (value == "dislike") profile.Dislikes.Add(food.Id);
            Raise(nameof(Taste));
        }
    }
}

/// <summary>③ 里的一个选装姿态：贴墙、坐、睡（design/pet-life/05 §1 ③）。</summary>
public sealed class KeyframeRow(string stance, string name) : Observable
{
    private bool _busy;
    private string _problems = "";
    private Bitmap? _frame;

    public string Stance { get; } = stance;
    public string Name { get; } = name;
    public string GenerateText => $"生成{Name}关键帧";
    public void Refresh() => Raise(nameof(NeedsPick));
    public ObservableCollection<ImageItem> Candidates { get; } = [];
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string Problems { get => _problems; set { if (Set(ref _problems, value)) Raise(nameof(HasProblems)); } }
    public bool HasProblems => _problems.Length > 0;
    public Bitmap? Frame { get => _frame; set { if (Set(ref _frame, value)) { Raise(nameof(HasFrame)); Raise(nameof(NeedsPick)); } } }
    /// <summary>生成了候选但还没选定：提示点一张（选定后才合成首尾帧）。</summary>
    public bool NeedsPick => Candidates.Count > 0 && !Candidates.Any(c => c.IsSelected);
    public bool HasFrame => _frame is not null;
    public string Hint => Stance switch
    {
        Stances.Wall => "必需。侧身扒在左边一道看不见的墙上，手脚在一条竖线上。墙组动作（贴墙待机、攀爬、上墙、下墙、松手）是必需的，要先有它。",
        Stances.Sit => "原地坐下的姿势。做「坐下 / 坐着待机 / 起身」用。",
        Stances.Lie => "趴着或蜷着睡觉的姿势。做「入睡 / 睡着 / 醒来」用，晚上困了会去睡觉。",
        _ => "倒挂在画面顶部看不见的天花板上。要先有贴墙；做「倒挂待机 / 倒挂爬行 / 翻上天花板 / 回到墙上 / 松手」用，爬到墙顶会翻上去。",
    };
}

public sealed partial class ProjectViewModel
{
    // ───── ① 性格与口味（design/pet-life/05 §1 ①） ─────

    public IReadOnlyList<StudioTrait> TraitRows { get; private set; } = [];
    public IReadOnlyList<StudioFood> FoodRows { get; private set; } = [];

    public IReadOnlyList<Choice> SpeciesChoices { get; } =
    [
        new("humanoid", "人形"), new("quadruped", "四足"), new("flying", "会飞"), new("blob", "软体"), new("other", "其他"),
    ];

    public string ProfileSpecies
    {
        get => Data.Profile.Species;
        set { Data.Profile.Species = value ?? "humanoid"; Raise(nameof(ProfileSpecies)); }
    }

    // ───── ③ 选装姿态 ─────

    public IReadOnlyList<KeyframeRow> KeyframeRows { get; private set; } = [];

    private void InitLife()
    {
        TraitRows = PetTraits.All.Select(t => new StudioTrait(Data.Profile, t.Key, t.Label)).ToList();
        FoodRows = Foods.All.Select(f => new StudioFood(Data.Profile, f)).ToList();
        KeyframeRows = StudioPrompts.KeyframeStances.Select(k => new KeyframeRow(k.Stance, k.Name)).ToList();
        LoadKeyframes();
    }

    private void LoadKeyframes()
    {
        foreach (var row in KeyframeRows)
        {
            row.Candidates.Clear();
            if (Data.Keyframes.TryGetValue(row.Stance, out var step))
                foreach (var c in step.Candidates.Where(c => File.Exists(Project.PathOf(c))))
                    row.Candidates.Add(new ImageItem(c, Project.PathOf(c)) { IsSelected = c == step.Selected });
            row.Frame = Thumbs.Load(Project.PathOf(StudioProject.FrameFile(row.Stance)), 640);
            row.Refresh();
        }
    }

    public async Task GenerateKeyframesAsync(KeyframeRow row)
    {
        if (_owner.ImageProvider is not { IsConfigured: true } provider) { Say("先在 ⓪ 配好图片模型", error: true); return; }
        if (!HasFirstFrame) { Say("先选好站姿（上面）", error: true); return; }
        row.Busy = true;
        try
        {
            IReadOnlyList<string> saved = [];
            await RunTaskAsync(StepId.Figure, async () =>
                saved = await Engine.GenerateKeyframesAsync(Project, provider, row.Stance, _imageCount, _life.Token));
            LoadKeyframes();
            Say(saved.Count == 0 ? "已取消，没有发出请求" : $"生成了 {saved.Count} 张{row.Name}关键帧，点一张选定");
        }
        catch (Exception e) when (e is GenerationException or IOException or InvalidDataException or InvalidOperationException)
        {
            Say($"{row.Name}关键帧生成失败：{e.Message}", error: true);
        }
        catch (OperationCanceledException)
        {
            Say("已取消", error: true);
        }
        finally
        {
            row.Busy = false;
        }
    }

    public async Task SelectKeyframeAsync(KeyframeRow row, ImageItem item)
    {
        try
        {
            var outcome = await RunAsync(StepId.Figure, () => Engine.SelectKeyframe(Project, row.Stance, item.Rel));
            LoadKeyframes();
            row.Problems = string.Join("\n", outcome.Problems);
            Raise(nameof(PlanProblem));
            Say(outcome.Problems.Count == 0 ? $"{row.Name}关键帧做好了，可以在 ④ 加{row.Name}这一组动作" : $"{row.Name}关键帧做好了，但检查发现了问题",
                outcome.Problems.Count > 0);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            row.Problems = "";
            Say(e.Message, error: true);
        }
    }

    public async Task ImportKeyframeAsync(KeyframeRow row, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await RunAsync(StepId.Figure, () => Engine.ImportCandidate(Project, $"keyframes/{row.Stance}", path));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Say($"「{Path.GetFileName(path)}」读不出来：{e.Message}", error: true);
            }
        }
        SaveNow();
        LoadKeyframes();
    }

    /// <summary>④ 把缺的必需动作（基础五类 + 地面常用 + 墙组）一次加上。老工程升级后用它补齐。</summary>
    public void AddRequired()
    {
        foreach (var t in StudioPrompts.RequiredTemplates)
            if (Data.Actions.All(a => a.Template != t.Id && a.Id != t.Id)) AddTemplate(t);
    }

    /// <summary>④ 一次加一整组（地面上的常用 / 墙组 / 坐 / 睡）：墙组缺一个就不能用，所以整组加。</summary>
    public void AddLifeGroup(string group)
    {
        var ids = group switch
        {
            "ground" => new[] { "eat", "fall", "land", "dizzy", "hungry", "yawn" },
            "wall" => ["wall-idle", "climb", "wall-enter", "wall-exit", "let-go"],
            "sit" => ["sit-enter", "sit-idle", "sit-exit"],
            "ceiling" => ["ceiling-idle", "ceiling-crawl", "ceiling-enter", "ceiling-exit", "ceiling-drop"],
            _ => ["sleep-enter", "sleep-loop", "sleep-exit", "sit-enter", "sit-idle", "sit-exit"],
        };
        foreach (var t in StudioPrompts.LifeTemplates.Where(t => ids.Contains(t.Id)))
            if (Data.Actions.All(a => a.Id != t.Id)) AddTemplate(t);
    }
}
