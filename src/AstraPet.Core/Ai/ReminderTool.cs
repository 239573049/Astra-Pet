using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>
/// <c>remind</c>：宠物答应用户「到点提醒你」。这里只把提醒记下来（<see cref="PetSettings.Reminders"/>），
/// 到点时由 <see cref="Core.Behaviors.ScheduledReminderBehavior"/> 请同一个 Agent 主动开口，而不是另起一个智能体去执行。
/// 用户在聊天里让宠物设、改、删、查提醒，全靠这一个工具（action：add / update / cancel / list）。
/// 已设的提醒带编号写在系统提示词里，改和删时用编号。
/// 成功时算「表现类」（模型同一次回复里已经说了「好，八点叫你」）；参数不对时把错误喂回去让它改。
/// </summary>
public sealed class ReminderTool(Func<ObservableCollection<ScheduledReminder>> reminders, Func<DateTime> now) : IAgentTool
{
    public const string ToolName = "remind";

    public string Name => ToolName;
    public bool IsPresentational => true;

    /// <summary>新加了一条（参数是那条提醒）或取消了一条，日志、界面用。</summary>
    public event Action<ScheduledReminder, bool>? Changed;

    public ToolDefinition Definition()
    {
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("add", "update", "cancel", "list"),
                    ["description"] = "add 设新提醒；update 改已设的提醒（只填要改的字段）；cancel 删掉一个；list 查看全部已设的提醒",
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "add / update：到时候要提醒用户的事，简短写清楚，如「吃药」「给妈妈打电话」「下楼拿外卖」",
                },
                ["in_minutes"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 1,
                    ["description"] = "add / update：从现在起多少分钟后提醒（用户说「半小时后」「过 10 分钟」这类相对时间时用）。和 at 二选一",
                },
                ["at"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "add / update：具体时刻，本地时间，格式「yyyy-MM-dd HH:mm」，或只写「HH:mm」（今天过了就是明天）。和 in_minutes 二选一",
                },
                ["repeat"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("once", "daily", "weekdays", "weekly"),
                    ["description"] = "add / update：重复方式，add 时默认 once。每天 daily、工作日 weekdays、每周（at 那天的星期几）weekly",
                },
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "update / cancel：提醒编号（见系统提示词【你答应的提醒】，或先 list）",
                },
            },
            ["required"] = new JsonArray("action"),
            ["additionalProperties"] = false,
        };
        return new ToolDefinition(ToolName,
            "定时提醒用户：记下要提醒的事和时间，到点时你会自己开口提醒TA（这时你还在，不需要用户再做什么）。" +
            "用户说「X 点提醒我…」「过半小时叫我…」「每天早上提醒我…」时调用 add；要改时间、内容或重复方式时用 update；" +
            "说不用提醒了、取消时用 cancel；问「我有哪些提醒」时看【你答应的提醒】回答，拿不准再 list。" +
            "只是聊到某个时间、用户没让你提醒时不要自作主张设。" +
            "调用后照常用文字回复，顺口确认一下时间就好。", parameters);
    }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct) => Task.FromResult(Invoke(args));

    private ToolResult Invoke(JsonElement args)
    {
        var list = reminders();
        var current = now();
        string? Str(string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!.Trim() is { Length: > 0 } s ? s : null
                : null;

        var action = Str("action") ?? "add";
        if (action == "list")
            return new ToolResult(list.Count == 0 ? "现在没有设任何提醒"
                : string.Join("\n", list.OrderBy(r => r.DueAt).Select(r => ReminderSchedule.PromptLine(r, current))));

        ScheduledReminder? Find(out ToolResult? error)
        {
            var id = Str("id");
            var found = list.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            error = found is null ? new ToolResult($"没有编号为 {id} 的提醒。已设的提醒和编号见【你答应的提醒】（或先 list）", IsError: true) : null;
            return found;
        }

        if (action == "update")
        {
            if (Find(out var notFound) is not { } item) return notFound!;
            var text = Str("text") ?? item.Text;
            if (text.Length > ReminderSchedule.MaxText) text = text[..ReminderSchedule.MaxText];
            var repeat = item.Repeat;
            if (Str("repeat") is { } repeatText)
            {
                if (ReminderSchedule.ParseRepeat(repeatText) is not { } r)
                    return new ToolResult("repeat 只能是 once、daily、weekdays、weekly", IsError: true);
                repeat = r;
            }
            var due = item.DueAt;
            if (HasTime())
            {
                if (ResolveTime(repeat, out var error) is not { } d) return error!;
                due = d;
            }
            else if (repeat != item.Repeat && due <= current)
            {
                due = ReminderSchedule.Next(due, repeat, current) ?? due;
            }
            due = ReminderSchedule.Align(due, repeat);
            item.Text = text;
            item.DueAt = due;
            item.Repeat = repeat;
            PetLog.Write($"改提醒 {item.Id}：{ReminderSchedule.Describe(item)} {item.Text}");
            Changed?.Invoke(item, true);
            return new ToolResult($"已改好（{item.Id}）：{ReminderSchedule.When(due, current)}" +
                                  (repeat == ReminderRepeat.Once ? "" : $"起{ReminderSchedule.RepeatLabel(repeat)}") + $"提醒「{text}」");
        }

        if (action == "cancel")
        {
            if (Find(out var notFound) is not { } target) return notFound!;
            list.Remove(target);
            PetLog.Write($"取消提醒 {target.Id}：{target.Text}");
            Changed?.Invoke(target, false);
            return new ToolResult($"已取消：{target.Text}");
        }

        if (action != "add") return new ToolResult("action 只能是 add、update、cancel、list", IsError: true);
        var newText = Str("text");
        if (newText is null) return new ToolResult("text 不能为空：写上到时候要提醒用户什么", IsError: true);
        if (newText.Length > ReminderSchedule.MaxText) newText = newText[..ReminderSchedule.MaxText];
        if (ReminderSchedule.ParseRepeat(Str("repeat")) is not { } newRepeat)
            return new ToolResult("repeat 只能是 once、daily、weekdays、weekly", IsError: true);
        if (!HasTime()) return new ToolResult("要给 in_minutes 或 at 其中一个，说明什么时候提醒", IsError: true);
        if (ResolveTime(newRepeat, out var timeError) is not { } resolved) return timeError!;
        return Add(list, newText, ReminderSchedule.Align(resolved, newRepeat), newRepeat, current);

        bool HasTime() => Str("at") is not null
                          || args.ValueKind == JsonValueKind.Object && args.TryGetProperty("in_minutes", out var v) && v.ValueKind != JsonValueKind.Null;

        // in_minutes 优先；at 已经过了的：一次性的报错，重复的挪到下一次。
        DateTime? ResolveTime(ReminderRepeat repeat, out ToolResult? error)
        {
            error = null;
            if (args.TryGetProperty("in_minutes", out var m) && m.ValueKind != JsonValueKind.Null)
            {
                var minutes = m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out var d) ? d
                    : m.ValueKind == JsonValueKind.String && double.TryParse(m.GetString(), out var ds) ? ds : double.NaN;
                if (double.IsNaN(minutes) || minutes < 1 || minutes > ReminderSchedule.MaxMinutes)
                {
                    error = new ToolResult($"in_minutes 要是 1 到 {ReminderSchedule.MaxMinutes} 之间的整数", IsError: true);
                    return null;
                }
                return current.AddMinutes(Math.Round(minutes));
            }
            var at = Str("at")!;
            if (ReminderSchedule.ParseAt(at, current) is not { } parsed)
            {
                error = new ToolResult($"看不懂时间「{at}」，请写成「yyyy-MM-dd HH:mm」或「HH:mm」", IsError: true);
                return null;
            }
            if (parsed > current) return parsed;
            if (repeat == ReminderRepeat.Once)
            {
                error = new ToolResult($"{ReminderSchedule.When(parsed, current)} 已经过去了（现在是 {ReminderSchedule.When(current, current)}）", IsError: true);
                return null;
            }
            return ReminderSchedule.Next(parsed, repeat, current);
        }
    }

    private ToolResult Add(ObservableCollection<ScheduledReminder> list, string text, DateTime due, ReminderRepeat repeat, DateTime current)
    {
        if (list.FirstOrDefault(r => r.DueAt == due && string.Equals(r.Text, text, StringComparison.OrdinalIgnoreCase)) is { } same)
            return new ToolResult($"这个提醒已经设过了（{same.Id}）");
        if (list.Count >= ReminderSchedule.MaxReminders)
            return new ToolResult($"已经有 {list.Count} 个提醒了，先取消几个不需要的再设", IsError: true);

        var item = new ScheduledReminder
        {
            Id = ReminderSchedule.NewId(list), Text = text, DueAt = due, Repeat = repeat, Created = current,
        };
        list.Add(item);
        PetLog.Write($"设提醒 {item.Id}：{ReminderSchedule.Describe(item)} {item.Text}");
        Changed?.Invoke(item, true);
        return new ToolResult($"已设好（编号 {item.Id}）：{ReminderSchedule.When(due, current)}" +
                              (repeat == ReminderRepeat.Once ? "" : $"起{ReminderSchedule.RepeatLabel(repeat)}") + $"提醒「{text}」");
    }
}
