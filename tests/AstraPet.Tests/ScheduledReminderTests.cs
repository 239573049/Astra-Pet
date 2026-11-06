using System.Collections.ObjectModel;
using System.Text.Json;
using AstraPet.Ai;
using AstraPet.Core;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

public class ScheduledReminderTests
{
    // 2026-11-06 是星期五。
    private static readonly DateTime Now = new(2026, 11, 6, 14, 30, 0);

    private static (ReminderTool Tool, ObservableCollection<ScheduledReminder> List) Tool(DateTime? now = null)
    {
        var list = new ObservableCollection<ScheduledReminder>();
        return (new ReminderTool(() => list, () => now ?? Now), list);
    }

    private static ToolResult Call(ReminderTool tool, string json) =>
        tool.InvokeAsync(JsonDocument.Parse(json).RootElement, default).Result;

    [Fact]
    public void Add_with_relative_minutes_and_clock_time()
    {
        var (tool, list) = Tool();
        Assert.False(Call(tool, """{"action":"add","text":"拿外卖","in_minutes":30}""").IsError);
        Assert.False(Call(tool, """{"action":"add","text":"吃药","at":"08:00","repeat":"daily"}""").IsError);

        Assert.Equal(Now.AddMinutes(30), list[0].DueAt);
        Assert.Equal(new DateTime(2026, 11, 7, 8, 0, 0), list[1].DueAt);   // 今天 8 点过了 → 明天
        Assert.Equal(ReminderRepeat.Daily, list[1].Repeat);
        Assert.NotEqual(list[0].Id, list[1].Id);
    }

    [Fact]
    public void Add_rejects_past_once_time_and_bad_input()
    {
        var (tool, list) = Tool();
        Assert.True(Call(tool, """{"action":"add","text":"开会","at":"2026-11-06 09:00"}""").IsError);
        Assert.True(Call(tool, """{"action":"add","text":"开会"}""").IsError);
        Assert.True(Call(tool, """{"action":"add","at":"16:00"}""").IsError);
        Assert.True(Call(tool, """{"action":"add","text":"开会","at":"明天下午"}""").IsError);
        Assert.Empty(list);
    }

    [Fact]
    public void Weekday_reminder_set_on_friday_evening_skips_the_weekend()
    {
        var (tool, list) = Tool();
        Call(tool, """{"action":"add","text":"打卡","at":"09:00","repeat":"weekdays"}""");
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), list[0].DueAt);   // 下周一
    }

    [Fact]
    public void Update_changes_only_given_fields_and_cancel_removes()
    {
        var (tool, list) = Tool();
        Call(tool, """{"action":"add","text":"吃药","at":"20:00"}""");
        var id = list[0].Id;

        Assert.False(Call(tool, $$"""{"action":"update","id":"{{id}}","at":"21:30"}""").IsError);
        Assert.Equal("吃药", list[0].Text);
        Assert.Equal(new DateTime(2026, 11, 6, 21, 30, 0), list[0].DueAt);

        Assert.False(Call(tool, $$"""{"action":"update","id":"{{id}}","text":"吃维生素","repeat":"daily"}""").IsError);
        Assert.Equal("吃维生素", list[0].Text);
        Assert.Equal(ReminderRepeat.Daily, list[0].Repeat);

        Assert.Contains("吃维生素", Call(tool, """{"action":"list"}""").Content);
        Assert.True(Call(tool, """{"action":"cancel","id":"nope"}""").IsError);
        Assert.False(Call(tool, $$"""{"action":"cancel","id":"{{id}}"}""").IsError);
        Assert.Empty(list);
        Assert.Equal("现在没有设任何提醒", Call(tool, """{"action":"list"}""").Content);
    }

    [Theory]
    [InlineData(ReminderRepeat.Daily, "2026-11-07 08:00")]
    [InlineData(ReminderRepeat.Weekly, "2026-11-13 08:00")]
    [InlineData(ReminderRepeat.Weekdays, "2026-11-09 08:00")]
    public void Next_occurrence_after_missed_days(ReminderRepeat repeat, string expected)
    {
        var due = new DateTime(2026, 10, 30, 8, 0, 0);   // 上周五
        Assert.Equal(DateTime.Parse(expected), ReminderSchedule.Next(due, repeat, Now));
        Assert.Null(ReminderSchedule.Next(due, ReminderRepeat.Once, Now));
    }

    [Fact]
    public async Task Agent_sets_reminder_in_chat_and_lists_it_in_prompt()
    {
        var settings = new PetSettings { ContextTurns = 2, AutoMemory = false };
        settings.Providers.Add(new LlmProvider { Name = "t", BaseUrl = "http://x", Model = "m", ToolSupport = ToolSupport.On });
        settings.Normalize();
        var time = new FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));
        var llm = new FakeLlm()
            .Reply(new TextPart("好，半小时后叫你～"), new ToolCallPart("c1", ReminderTool.ToolName, """{"action":"add","text":"拿外卖","in_minutes":30}"""))
            .Reply("嗯嗯");
        var agent = new PetAgent(settings, llm, time);

        await agent.AskAsync("半小时后提醒我拿外卖");
        Assert.Single(settings.Reminders);
        Assert.Single(llm.Requests);   // 说了话、工具成功：不再请求
        Assert.Contains(llm.Requests[0].Tools!, t => t.Name == ReminderTool.ToolName);
        Assert.Contains("【定提醒】", llm.Requests[0].System);

        await agent.AskAsync("还有吗");
        Assert.Contains("【你答应的提醒】", llm.Requests[1].System);
        Assert.Contains("拿外卖", llm.Requests[1].System);
    }

    [Fact]
    public async Task Agent_gets_tool_error_back_and_can_fix_it()
    {
        var settings = new PetSettings { ContextTurns = 2, AutoMemory = false };
        settings.Providers.Add(new LlmProvider { Name = "t", BaseUrl = "http://x", Model = "m", ToolSupport = ToolSupport.On });
        settings.Normalize();
        var time = new FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));
        var llm = new FakeLlm()
            .Reply(new TextPart("好的"), new ToolCallPart("c1", ReminderTool.ToolName, """{"action":"add","text":"开会","at":"后天"}"""))
            .Reply(new TextPart("改好啦"), new ToolCallPart("c2", ReminderTool.ToolName, """{"action":"add","text":"开会","at":"2026-11-08 10:00"}"""));
        var agent = new PetAgent(settings, llm, time);

        await agent.AskAsync("后天十点提醒我开会");
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(new DateTime(2026, 11, 8, 10, 0, 0), Assert.Single(settings.Reminders).DueAt);
    }

    [Fact]
    public void Prompt_without_tools_keeps_alarm_boundary()
    {
        var input = new PromptInputs { Persona = PetPersona.Generic("A"), Now = Now };
        Assert.Contains("设置闹钟", SystemPrompt.Build(input));
        Assert.DoesNotContain("【定提醒】", SystemPrompt.Build(input));
        Assert.DoesNotContain("设置闹钟", SystemPrompt.Build(input with { CanRemind = true }));
    }

    [Fact]
    public void Due_reminders_are_spoken_by_the_agent_and_rescheduled()
    {
        using var rig = new HostRig(new DateTimeOffset(Now, TimeSpan.Zero));
        rig.Commands.AgentAvailable = true;
        rig.Settings.Reminders.Add(new ScheduledReminder { Id = "r1", Text = "拿外卖", DueAt = Now.AddMinutes(5) });
        rig.Settings.Reminders.Add(new ScheduledReminder { Id = "r2", Text = "喝水", DueAt = Now.AddMinutes(5), Repeat = ReminderRepeat.Daily });
        rig.Settings.Reminders.Add(new ScheduledReminder { Id = "r3", Text = "很久以前的", DueAt = Now.AddDays(-2) });
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(4));
        Assert.DoesNotContain(rig.Commands.Reminders, c => c.Contains("拿外卖"));
        Assert.DoesNotContain(rig.Settings.Reminders, r => r.Id == "r3");   // 错过太久：删掉不提
        Assert.DoesNotContain(rig.Commands.Reminders, c => c.Contains("很久以前"));

        rig.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains(rig.Commands.Reminders, c => c.Contains("拿外卖"));
        Assert.Contains(rig.Commands.Reminders, c => c.Contains("喝水"));
        var daily = Assert.Single(rig.Settings.Reminders);
        Assert.Equal(Now.AddMinutes(5).AddDays(1), daily.DueAt);
    }

    [Fact]
    public void Without_agent_falls_back_to_fixed_line()
    {
        using var rig = new HostRig(new DateTimeOffset(Now, TimeSpan.Zero));
        rig.Settings.Reminders.Add(new ScheduledReminder { Id = "r1", Text = "拿外卖", DueAt = Now.AddMinutes(1) });
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains(rig.Voice.Said, s => s.Contains("拿外卖"));
    }

    [Fact]
    public void Reminders_survive_settings_round_trip()
    {
        var settings = new PetSettings();
        settings.Reminders.Add(new ScheduledReminder { Id = "r1", Text = "吃药", DueAt = Now, Repeat = ReminderRepeat.Weekdays });
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.PetSettings);
        Assert.Contains("\"Weekdays\"", json);
        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PetSettings)!;
        Assert.Equal("吃药", Assert.Single(back.Reminders).Text);
    }
}
