using System.Globalization;
using System.Text.Json.Serialization;

namespace AstraPet.Core;

/// <summary>定时提醒怎么重复。</summary>
public enum ReminderRepeat { Once, Daily, Weekdays, Weekly }

/// <summary>
/// 宠物答应用户的一条定时提醒（Agent 用 remind 工具设的）。到点时不是另起一个智能体，
/// 而是由 <see cref="Behaviors.ScheduledReminderBehavior"/> 请宠物自己的 Agent 主动开口（和聊天同一个上下文），按性格把这件事说出来。
/// 时间都是本机本地时间。随设置一起存盘。
/// </summary>
public sealed class ScheduledReminder : Observable
{
    private string _text = "";
    private DateTime _dueAt;
    private ReminderRepeat _repeat;

    /// <summary>短编号，写进提示词，Agent 取消时用。</summary>
    public string Id { get; set; } = "";
    /// <summary>要提醒用户的事，如「吃药」「给妈妈打电话」。</summary>
    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) Raise(nameof(Meta)); } }
    /// <summary>下一次到点的时间（重复的提醒每响一次往后挪）。</summary>
    public DateTime DueAt { get => _dueAt; set { if (Set(ref _dueAt, value)) Raise(nameof(Meta)); } }
    public ReminderRepeat Repeat { get => _repeat; set { if (Set(ref _repeat, value)) Raise(nameof(Meta)); } }
    public DateTime Created { get; set; } = DateTime.Now;

    [JsonIgnore] public string Meta => ReminderSchedule.Describe(this);
}

/// <summary>定时提醒的时间规则：解析 Agent 给的时间、算下一次、写成人话。</summary>
public static class ReminderSchedule
{
    public const int MaxReminders = 50;
    public const int MaxText = 200;
    /// <summary>相对时间最多一年。</summary>
    public const int MaxMinutes = 366 * 24 * 60;

    /// <summary>
    /// 解析「2026-11-07 08:00」「11-07 08:00」「08:00」。只给时刻且今天已经过了，就是明天（每周重复的照样按今天的星期算）。
    /// 失败返回 null。
    /// </summary>
    public static DateTime? ParseAt(string text, DateTime now)
    {
        text = text.Trim().Replace('T', ' ').Replace('/', '-').Replace('：', ':');
        string[] full = ["yyyy-MM-dd HH:mm", "yyyy-M-d H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-M-d H:mm:ss"];
        if (DateTime.TryParseExact(text, full, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return at;
        if (DateTime.TryParseExact(text, ["M-d H:mm", "MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
        {
            var guess = new DateTime(now.Year, md.Month, md.Day, md.Hour, md.Minute, 0);
            return guess < now.AddDays(-1) ? guess.AddYears(1) : guess;   // 年初说「12-31」是明年的事不太可能，跨年的「01-02」才是
        }
        if (TimeSpan.TryParseExact(text, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1))
        {
            var today = now.Date + time;
            return today <= now ? today.AddDays(1) : today;
        }
        return null;
    }

    /// <summary>重复的提醒在 <paramref name="now"/> 之后的下一次；一次性的返回 null。</summary>
    public static DateTime? Next(DateTime due, ReminderRepeat repeat, DateTime now)
    {
        if (repeat == ReminderRepeat.Once) return null;
        var step = repeat == ReminderRepeat.Weekly ? 7 : 1;
        var next = due;
        if (next <= now)
        {
            // 关了很久再打开时直接跳到最近，不一天一天加。
            var days = (int)Math.Floor((now - next).TotalDays / step) * step;
            next = next.AddDays(days);
        }
        while (next <= now || repeat == ReminderRepeat.Weekdays && !IsWeekday(next)) next = next.AddDays(step);
        return next;
    }

    /// <summary>工作日提醒设在周末时，挪到下一个工作日。</summary>
    public static DateTime Align(DateTime due, ReminderRepeat repeat)
    {
        while (repeat == ReminderRepeat.Weekdays && !IsWeekday(due)) due = due.AddDays(1);
        return due;
    }

    private static bool IsWeekday(DateTime d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static string RepeatLabel(ReminderRepeat repeat) => repeat switch
    {
        ReminderRepeat.Daily => "每天",
        ReminderRepeat.Weekdays => "工作日",
        ReminderRepeat.Weekly => "每周",
        _ => "一次",
    };

    public static ReminderRepeat? ParseRepeat(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "once" or "none" => ReminderRepeat.Once,
        "daily" => ReminderRepeat.Daily,
        "weekdays" => ReminderRepeat.Weekdays,
        "weekly" => ReminderRepeat.Weekly,
        _ => null,
    };

    /// <summary>「11-07 周六 08:00」；同一年不写年份。</summary>
    public static string When(DateTime at, DateTime? now = null)
    {
        string[] week = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
        var date = at.Year == (now ?? DateTime.Now).Year ? at.ToString("MM-dd", CultureInfo.InvariantCulture) : at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{date} {week[(int)at.DayOfWeek]} {at:HH:mm}";
    }

    /// <summary>控制台列表的副标题：「每天 08:00 · 下次 11-07 周六 08:00」或「11-07 周六 08:00」。</summary>
    public static string Describe(ScheduledReminder r) => r.Repeat switch
    {
        ReminderRepeat.Once => When(r.DueAt),
        ReminderRepeat.Weekly => $"每周{"日一二三四五六"[(int)r.DueAt.DayOfWeek]} {r.DueAt:HH:mm} · 下次 {When(r.DueAt)}",
        _ => $"{RepeatLabel(r.Repeat)} {r.DueAt:HH:mm} · 下次 {When(r.DueAt)}",
    };

    /// <summary>写进提示词的一行：「r7k2：11-07 周六 08:00（每天）提醒「吃药」」。</summary>
    public static string PromptLine(ScheduledReminder r, DateTime now) =>
        $"{r.Id}：{When(r.DueAt, now)}" + (r.Repeat == ReminderRepeat.Once ? "" : $"（{RepeatLabel(r.Repeat)}）") + $" 提醒「{r.Text}」";

    /// <summary>四位小写字母数字的编号，不和已有的重复。</summary>
    public static string NewId(IEnumerable<ScheduledReminder> existing, Random? random = null)
    {
        random ??= Random.Shared;
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var used = existing.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var id = "r" + new string(Enumerable.Range(0, 3).Select(_ => chars[random.Next(chars.Length)]).ToArray());
            if (!used.Contains(id)) return id;
        }
    }
}
