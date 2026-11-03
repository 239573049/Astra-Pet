using AstraPet.Ai;
using AstraPet.Core;

namespace AstraPet.Tests;

/// <summary>按顺序返回预设回复的假模型；记下收到的请求。</summary>
internal sealed class FakeLlm(params string[] replies) : ILlmClient
{
    private readonly Queue<string> _replies = new(replies);
    public List<ChatRequest> Requests { get; } = new();

    public Task<string> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : "[]");
    }
}

public class PetAgentTests
{
    private static PetSettings Configured(bool autoMemory = false)
    {
        var settings = new PetSettings { MemoryEnabled = true, AutoMemory = autoMemory, ContextTurns = 2 };
        settings.Providers.Add(new LlmProvider { Name = "test", BaseUrl = "http://localhost", Model = "m" });
        return settings;
    }

    [Fact]
    public async Task Sends_only_recent_turns_and_records_history()
    {
        var llm = new FakeLlm("r1", "r2", "r3", "r4");
        var agent = new PetAgent(Configured(), llm);
        foreach (var q in new[] { "q1", "q2", "q3", "q4" }) await agent.AskAsync(q);

        // 上下文 2 轮 = 最近 4 条历史 + 这一句
        var last = llm.Requests[^1];
        Assert.Equal(["q2", "r2", "q3", "r3", "q4"], last.Messages.Select(m => m.Content));
        Assert.Equal("assistant", agent.History[^1].Role);
    }

    [Fact]
    public async Task Not_configured_throws_readable_error()
    {
        var agent = new PetAgent(new PetSettings(), new FakeLlm());
        var e = await Assert.ThrowsAsync<LlmException>(() => agent.AskAsync("hi"));
        Assert.Contains("还没有配置模型", e.Message);
    }

    [Fact]
    public void System_prompt_includes_memories()
    {
        var settings = Configured();
        var agent = new PetAgent(settings, new FakeLlm());
        agent.AddMemory("用户喜欢猫", auto: false);
        var prompt = agent.BuildSystemPrompt();
        Assert.StartsWith(settings.SystemPrompt.Trim(), prompt);
        Assert.Contains("- 用户喜欢猫", prompt);
    }

    [Fact]
    public void Memories_are_deduplicated_and_capped()
    {
        var settings = Configured();
        var agent = new PetAgent(settings, new FakeLlm());
        Assert.True(agent.AddMemory("用户喜欢猫", auto: false));
        Assert.False(agent.AddMemory("  用户喜欢猫 ", auto: false));
        Assert.False(agent.AddMemory("   ", auto: false));
        for (var i = 0; i < PetAgent.MaxMemories + 5; i++) agent.AddMemory($"记忆 {i}", auto: true);
        Assert.Equal(PetAgent.MaxMemories, settings.Memories.Count);
        Assert.DoesNotContain(settings.Memories, m => m.Text == "用户喜欢猫");   // 最早的被挤掉
    }

    [Fact]
    public async Task Auto_memory_extracts_facts_from_wrapped_json()
    {
        var settings = Configured(autoMemory: true);
        // 第二个回复是提取记忆的请求：模型把 JSON 包在代码块里，前后还有话。
        var llm = new FakeLlm("好呀", "好的：\n```json\n[\"用户养了一只叫团子的猫\"]\n```");
        var agent = new PetAgent(settings, llm);
        var remembered = new TaskCompletionSource<IReadOnlyList<string>>();
        agent.Remembered += added => remembered.TrySetResult(added);

        await agent.AskAsync("我养了一只猫叫团子");
        var added = await remembered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["用户养了一只叫团子的猫"], added);
        Assert.Contains(settings.Memories, m => m.Auto && m.Text == "用户养了一只叫团子的猫");
    }
}
