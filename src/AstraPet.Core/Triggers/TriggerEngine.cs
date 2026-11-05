using AstraPet.Core.Behaviors;

namespace AstraPet.Core;

/// <summary>
/// 触发引擎：收到事件 → 停掉等这个事件的循环 → 逐条匹配规则（条件、冷却、概率）→ 请求动作、说台词。
/// 同一事件命中多条规则时都执行，动作在 Director 里按优先级自然取舍。设计见 02-triggers.md §6。
/// </summary>
public sealed class TriggerEngine : PetBehavior
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new();

    protected override void OnStart() => Context.Events.Published += OnEvent;

    /// <summary>最近一次命中的规则 id（测试和日志用）。</summary>
    public IReadOnlyCollection<string> Fired => _lastFired.Keys;

    private void OnEvent(PetEvent e)
    {
        Context.Director.StopLoop(e.Type);
        // 规则表很小，每次按当前设置和当前宠物的覆盖层重新合并，换宠物、改规则后不用另外通知。
        Context.Triggers.Reload(Settings.Triggers, Context.Director.Library.Overlay.Triggers);
        var now = Now;
        var state = State();
        foreach (var rule in Context.Triggers.Matching(e.Type).ToList())
        {
            if (!TriggerConditions.Hold(rule.When, e, Settings, now, state)) continue;
            if (rule.Cooldown > 0 && _lastFired.TryGetValue(rule.Id, out var last)
                && (now - last).TotalSeconds < rule.Cooldown) continue;
            if (rule.Chance < 1 && Context.Random.NextDouble() >= rule.Chance) continue;
            _lastFired[rule.Id] = now;
            Fire(rule, e);
            if (rule.Final) break;
            state = State();   // 规则可能改了姿态
        }
    }

    private TriggerState State() => Context.Life?.TriggerState()
        ?? new TriggerState(Context.Director.Stance, Context.Director.Sleeping, null, _ => null);

    private int BondLevel => Context.Life is { Enabled: true } life ? life.Level : 1;

    private void Fire(TriggerRule rule, PetEvent e)
    {
        ActionDef? action = null;
        var director = Context.Director;
        var reference = rule.Do == TriggerRule.EventActionToken ? e["action"] : rule.Do;
        if (reference is { Length: > 0 } && reference.StartsWith(TriggerRule.StancePrefix, StringComparison.Ordinal))
            director.RequestStance(reference[TriggerRule.StancePrefix.Length..], rule.PriorityValue, "rule:" + rule.Id);
        else if (reference is { Length: > 0 } and not TriggerRule.None)
        {
            action = director.Library.Actions.Resolve(reference, Context.Random, director.NextStance, director.Usable);
            if (action is not null)
                director.Request(new ActionRequest(action, rule.PriorityValue, "rule:" + rule.Id,
                    UntilEvents: rule.Until is { Count: > 0 } ? rule.Until : null));
        }

        var lines = rule.Say is { Count: > 0 } ? ExpandLines(rule.Say, e) : action?.LinesFor(BondLevel) ?? [];
        lines = lines.Select(Personalize).Where(l => l is not null).Select(l => l!).ToList();
        if (lines.Count > 0) Context.Voice.React(Pick(lines), rule.Speak);
    }

    /// <summary>{user} 换成设置里的称呼（亲密度 5 级起才用；没设或不够就去掉这句），{name} 换成食物等事件数据里的名字。</summary>
    private string? Personalize(string line)
    {
        if (!line.Contains('{')) return line;
        if (line.Contains("{user}"))
        {
            if (Settings.UserCallName.Length == 0 || BondLevel < 5) return null;
            line = line.Replace("{user}", Settings.UserCallName);
        }
        return line;
    }

    private List<string> ExpandLines(IEnumerable<string> say, PetEvent e)
    {
        var lines = new List<string>();
        foreach (var line in say)
        {
            switch (line)
            {
                case TriggerRule.ClickLinesToken: lines.AddRange(Settings.ClickLineList()); break;
                case TriggerRule.PettingLinesToken: lines.AddRange(InteractionBehavior.PettingLines); break;
                case TriggerRule.BondLinesToken: lines.AddRange(LifeLines.LevelUp(int.TryParse(e["level"], out var lv) ? lv : BondLevel)); break;
                default:
                    if (line.Length == 0) break;
                    // 事件数据里的值可以写进台词：{food} 之类。
                    var text = line;
                    if (e.Data is { } data)
                        foreach (var (key, value) in data)
                            text = text.Replace("{" + key + "}", value);
                    lines.Add(text);
                    break;
            }
        }
        return lines;
    }

    public override void Dispose()
    {
        if (Context is not null) Context.Events.Published -= OnEvent;
        base.Dispose();
    }
}
