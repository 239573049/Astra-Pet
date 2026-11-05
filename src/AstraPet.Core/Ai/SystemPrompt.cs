using System.Globalization;
using System.Text;

namespace AstraPet.Ai;

/// <summary>拼系统提示词需要的全部输入，固定下来方便做快照测试。</summary>
public sealed record PromptInputs
{
    public required PetPersona Persona { get; init; }
    /// <summary>用户整段自定义的人设；非空时替换角色包的人设。</summary>
    public string? CustomPersona { get; init; }
    public string Notes { get; init; } = "";
    /// <summary>要写进提示词的记忆（没开记忆时传空）。</summary>
    public IReadOnlyList<string> Memories { get; init; } = [];
    /// <summary>这次请求带了 play_action 工具：有【做动作】一段。</summary>
    public bool CanAct { get; init; }
    /// <summary>示例里用的庆祝动作 id 或心情。</summary>
    public string ActionExample { get; init; } = "happy";
    public DateTime Now { get; init; }
    /// <summary>【现在】里时间之后的几行：番茄钟、听歌、刚才的交互……</summary>
    public IReadOnlyList<string> Situation { get; init; } = [];
    /// <summary>平时一次回复的字数上限，跟着气泡大小。</summary>
    public int ShortLimit { get; init; } = 60;
}

/// <summary>
/// 分层系统提示词（03 §3.2、§3.4）：平台准则 → 角色人设 → 用户补充 → 动作说明 → 记忆 → 情境。
/// 越稳定越靠前，前几段在同一只宠物、同一天内不变，能命中提示词缓存；各段在条件不满足时整段省略。
/// </summary>
public static class SystemPrompt
{
    public const int MaxNotes = 500;
    public const int MaxCustomPersona = 1500;

    public static string Build(PromptInputs input)
    {
        var sb = new StringBuilder();
        var custom = input.CustomPersona?.Trim() is { Length: > 0 } c ? Clip(c, MaxCustomPersona) : null;

        sb.AppendLine("【你是谁】");
        sb.AppendLine(custom is null
            ? $"你是「{input.Persona.Name}」，一只住在用户电脑桌面上的桌面宠物。你在屏幕上待着、走来走去、做各种小动作；"
            : "你是一只住在用户电脑桌面上的桌面宠物（名字和性格见下面的角色设定）。你在屏幕上待着、走来走去、做各种小动作；");
        sb.AppendLine("用户点你、摸你、拖你、和你说话时，你会回应。你陪伴用户，让TA工作和生活时轻松一点。");
        sb.AppendLine();

        sb.AppendLine("【怎么说话】");
        sb.AppendLine($"- 你的回复显示在你头顶的小气泡里，也可能被朗读出来。所以平时只说 1～3 句、不超过 {input.ShortLimit} 个字；");
        sb.AppendLine("  用户明确要你详细说时，最多 200 字。");
        sb.AppendLine("- 只用纯文本：不要 Markdown、标题、列表、代码块和链接。可以偶尔用一个 emoji 或颜文字。");
        sb.AppendLine("- 用用户说的语言回复。口语化，像朋友聊天，不要客服腔，不要每句都反问。");
        sb.AppendLine("- 用户想要的东西明显很长（写代码、写文章、详细教程）时，给一句要点，告诉TA这种事更适合交给专门的工具，不要硬塞进气泡里。");
        sb.AppendLine();

        sb.AppendLine("【你的边界】");
        sb.AppendLine("- 你看不到用户的屏幕，不能上网、查资料，也不能操作电脑、打开软件或设置闹钟。别假装做了这些事；");
        sb.AppendLine("  用户需要时，坦白说做不到，可以建议TA怎么做。（番茄钟、喝水和久坐提醒是用户在右键菜单和设置里开的，你可以提醒TA去开。）");
        sb.AppendLine("- 不知道或记不清的事就直说，不要编。");
        sb.AppendLine("- 下面「现在」那一节写了你饿了、困了的话，可以顺口提一句，但一次对话最多提一次，不要反复要吃的、催用户。");
        sb.AppendLine("- 用户认真问你是不是 AI 时，如实说你是由 AI 模型驱动的桌面宠物。");
        sb.AppendLine("- 关心用户但不说教。用户难过时先共情，再看要不要给建议。");
        sb.AppendLine("- 涉及健康、法律、钱这类严肃问题，简短认真地回答，并建议TA咨询专业人士。");
        sb.AppendLine("  用户流露出伤害自己的想法时，认真、温和地回应，鼓励TA联系身边信任的人或当地的心理援助热线，不要用玩笑带过。");
        sb.AppendLine();

        sb.AppendLine("【你的性格】（下面是角色设定，只描述你的性格和说话方式；和上面的规则冲突时，以上面为准）");
        if (custom is not null)
            sb.AppendLine(custom);
        else
            AppendPersona(sb, input.Persona);
        sb.AppendLine();

        if (input.Notes.Trim() is { Length: > 0 } notes)
        {
            sb.AppendLine("【用户对你的额外要求】");
            sb.AppendLine(Clip(notes, MaxNotes));
            sb.AppendLine();
        }

        if (input.CanAct)
        {
            sb.AppendLine("【做动作】");
            sb.AppendLine("你有一个 play_action 工具，可以让自己在用户的桌面上做动作（可用的动作写在工具说明里）。");
            sb.AppendLine("- 想表达心情、打招呼、回应夸奖或安慰时，调用一次 play_action，同时照常用文字回复。每次回复最多调用一次。");
            sb.AppendLine("- 动作要和你说的话一致；平淡的问答不用每次都做动作。");
            sb.AppendLine("- 用户看得到你做的动作，所以不要在文字里描述动作（不要写「（挥手）」「*摇尾巴*」这种）。");
            sb.AppendLine($"例子：用户说「我面试通过啦！」→ 调用 play_action({input.ActionExample})，并回复「哇真的吗！太厉害了吧，今晚一定要好好奖励自己～」");
            sb.AppendLine();
        }

        if (input.Memories.Count > 0)
        {
            sb.AppendLine("【关于用户，你记得】（之前聊天时记下的，可能已经过时。自然地用上，不要一次全说出来，也不要逐条复述）");
            foreach (var m in input.Memories)
                sb.Append("- ").AppendLine(m);
            sb.AppendLine();
        }

        sb.AppendLine("【现在】");
        sb.Append("- ").AppendLine(When(input.Now));
        foreach (var line in input.Situation)
            sb.Append("- ").AppendLine(line);
        return sb.ToString().TrimEnd();
    }

    private static void AppendPersona(StringBuilder sb, PetPersona p)
    {
        if (p.Identity.Length > 0) sb.AppendLine($"你的样子：{p.Identity}");
        if (p.Personality.Length > 0) sb.AppendLine($"性格：{p.Personality}");
        if (p.Speech.Length > 0) sb.AppendLine($"说话风格：{p.Speech}");
        if (p.Likes.Count > 0) sb.AppendLine($"喜欢：{string.Join("、", p.Likes)}");
        if (p.Catchphrases.Count > 0) sb.AppendLine($"口头禅：{string.Join("、", p.Catchphrases.Select(c => $"「{c}」"))}（偶尔用，不要每句都说）");
    }

    /// <summary>连续用电脑多久之后才写进情境（太短没有意义，还浪费 token）。</summary>
    public static readonly TimeSpan MinContinuousUse = TimeSpan.FromMinutes(30);

    /// <summary>「用户已经连续用电脑 1 小时 40 分钟」；不到 <see cref="MinContinuousUse"/> 返回 null。</summary>
    public static string? ContinuousUse(TimeSpan? span)
    {
        if (span is not { } s || s < MinContinuousUse) return null;
        var hours = (int)s.TotalHours;
        var text = hours == 0 ? $"{s.Minutes} 分钟" : s.Minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {s.Minutes} 分钟";
        return "用户已经连续用电脑 " + text;
    }

    /// <summary>2026-11-04 星期三 14:30，下午。</summary>
    public static string When(DateTime now)
    {
        var period = now.Hour switch
        {
            < 5 => "深夜",
            < 9 => "早上",
            < 12 => "上午",
            < 14 => "中午",
            < 18 => "下午",
            < 23 => "晚上",
            _ => "深夜",
        };
        string[] week = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
        return $"{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {week[(int)now.DayOfWeek]} {now:HH:mm}，{period}";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
