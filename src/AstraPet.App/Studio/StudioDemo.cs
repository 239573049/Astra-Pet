#if DEBUG
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;

namespace AstraPet.Studio;

/// <summary>开发用的演示工程：同 tests 里 StudioEndToEndTests.Seed，⑤ 用角色目录 raw/ 下现成的视频代替。</summary>
internal static class StudioDemo
{
    private static readonly IReadOnlyDictionary<string, string> Raw = new Dictionary<string, string>
    {
        ["idle"] = "idle/待机呼吸.mp4",
        ["drag"] = "drag/被拎起悬空.mp4",
        ["wave"] = "click/开心挥手.mp4",
        ["trot"] = "move/原地小跑.mp4",
        ["look"] = "turn/东张西望.mp4",
    };

    public static StudioProject Seed(StudioEngine engine, string root, string character)
    {
        var project = StudioProject.Create(root, "小t（演示）");
        project.Data.Description = "穿蓝色卫衣的小女孩，抱着一只水豚";
        project.Data.Card.Name = "小t";
        project.Data.Card.Personality = "元气、爱撒娇";
        project.Save();
        engine.SelectSheet(project, engine.ImportCandidate(project, "sheet", Path.Combine(character, "reference", "sheet.png")));
        engine.ImportCandidate(project, "sheet", Path.Combine(character, "reference", "front_view.png"));
        project.Data.KeyColor = "green";
        engine.SelectFigure(project, engine.ImportCandidate(project, "figure", Path.Combine(character, "reference", "figure.png")));
        // 墙组是必需的：演示里贴墙关键帧用站姿图凑一张，宠物体系的动作用待机视频代替。
        engine.SelectKeyframe(project, Core.Stances.Wall, engine.ImportCandidate(project, "keyframes/wall", Path.Combine(character, "reference", "figure.png")));
        engine.ConfirmPlan(project);
        foreach (var action in project.Data.Actions)
        {
            var raw = Raw.GetValueOrDefault(action.Id, Raw["idle"]);
            Directory.CreateDirectory(project.RawDir(action.Id));
            File.Copy(Path.Combine(character, "raw", raw), project.RawVideo(action.Id, 1));
            new TaskRecord
            {
                Action = action.Id, Take = 1, TaskId = "demo-" + action.Id, State = VideoTaskState.Succeeded, Downloaded = true,
                Provider = "演示", Model = "demo", Prompt = project.FullPrompt(action), Resolution = project.Data.Resolution,
                Duration = action.Duration, Basis = project.TakeBasis(action), SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now,
            }.Save(project.TaskFile(action.Id, 1));
        }
        return project;
    }
}
#endif
