using AstraPet.Ai;
using AstraPet.Core;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

public class ConversationStoreTests
{
    private static PetSettings Configured()
    {
        var settings = new PetSettings { MemoryEnabled = false, CompactContext = false };
        settings.Providers.Add(new LlmProvider { Name = "test", BaseUrl = "http://localhost", Model = "m", ToolSupport = ToolSupport.Off });
        settings.Normalize();
        return settings;
    }

    [Fact]
    public void Round_trips_text_tool_calls_results_and_summary()
    {
        var snapshot = new ConversationSnapshot(
        [
            [
                ChatMessage.User("记住我喜欢猫"),
                new ChatMessage("assistant", [new TextPart("好～"), new ToolCallPart("c1", "remember", """{"action":"remember","text":"用户喜欢猫"}""")]),
                new ChatMessage("user", [new ToolResultPart("c1", "已记住", IsError: false)]),
            ],
            [ChatMessage.User("嗨"), ChatMessage.Assistant("嗨呀")],
        ], "之前聊了天气", new DateTimeOffset(2026, 11, 5, 21, 0, 0, TimeSpan.Zero));

        var back = ConversationStore.FromJson(ConversationStore.ToJson(snapshot).ToJsonString())!;
        Assert.Equal("之前聊了天气", back.Summary);
        Assert.Equal(snapshot.LastAt, back.LastAt);
        Assert.Equal(2, back.Turns.Count);
        Assert.Equal(snapshot.Turns[0], back.Turns[0], (a, b) => a.Role == b.Role && a.Parts.SequenceEqual(b.Parts));
    }

    [Fact]
    public void Broken_or_unknown_files_load_as_nothing_and_partial_turns_are_dropped()
    {
        Assert.Null(ConversationStore.FromJson("{oops"));
        Assert.Null(ConversationStore.FromJson("""{"version":99,"turns":[]}"""));
        var back = ConversationStore.FromJson("""{"version":1,"turns":[[{"role":"assistant","parts":[{"type":"text","text":"孤零零的回复"}]}],[{"role":"user","parts":[{"type":"text","text":"q"}]}]]}""")!;
        Assert.Equal("q", Assert.Single(back.Turns)[0].Text);
    }

    [Fact]
    public void Save_and_load_per_pet_and_empty_deletes_the_file()
    {
        using var dir = new TempLibrary();
        var a = ConversationStore.PathFor(dir.Dir, "pet/a");
        var b = ConversationStore.PathFor(dir.Dir, "pet-b");
        Assert.NotEqual(a, b);
        Assert.StartsWith(Path.Combine(dir.Dir, "conversations"), a);   // id 里的 / 不会跑出目录

        ConversationStore.Save(a, new ConversationSnapshot([[ChatMessage.User("q"), ChatMessage.Assistant("r")]], null, DateTimeOffset.Now));
        Assert.Single(ConversationStore.Load(a)!.Turns);
        Assert.Null(ConversationStore.Load(b));

        ConversationStore.Save(a, ConversationSnapshot.Empty);
        Assert.False(File.Exists(a));
    }

    [Fact]
    public async Task Agent_continues_a_restored_conversation_and_mentions_the_gap()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        var llm = new FakeLlm("r1", "r2");
        var first = new PetAgent(Configured(), llm, time);
        var changes = 0;
        first.HistoryChanged += () => changes++;
        await first.AskAsync("我叫小明");
        Assert.Equal(1, changes);
        var saved = ConversationStore.FromJson(ConversationStore.ToJson(first.Snapshot()).ToJsonString());

        time.Advance(TimeSpan.FromHours(5));   // 「重启」
        var second = new PetAgent(Configured(), llm, time);
        second.Restore(saved);
        await second.AskAsync("我叫什么？");

        var request = llm.Requests[^1];
        Assert.Equal(["我叫小明", "r1", "我叫什么？"], request.Messages.Select(m => m.Text));
        Assert.Contains("上次和用户聊天是 5 小时前", request.System);

        second.ClearHistory();
        Assert.True(second.Snapshot().IsEmpty);
    }
}
