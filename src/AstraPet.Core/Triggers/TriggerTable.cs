namespace AstraPet.Core;

/// <summary>
/// 合并好的规则表：内置 → 全局（设置里的）→ 宠物（覆盖层里的），同 id 后者覆盖前者。
/// 换宠物、改规则后调 <see cref="Reload"/>。
/// </summary>
public sealed class TriggerTable
{
    private List<TriggerRule> _rules = DefaultTriggers.All.ToList();

    public IReadOnlyList<TriggerRule> Rules => _rules;

    public void Reload(IEnumerable<TriggerRule>? global, IEnumerable<TriggerRule>? pet) =>
        _rules = Merge(DefaultTriggers.All, global ?? [], pet ?? []);

    public IEnumerable<TriggerRule> Matching(string eventType) => _rules.Where(r => r.Enabled && r.Matches(eventType));

    public static List<TriggerRule> Merge(params IEnumerable<TriggerRule>[] layers)
    {
        var result = new List<TriggerRule>();
        foreach (var layer in layers)
            foreach (var rule in layer)
            {
                if (rule.Id.Length == 0 || rule.On.Length == 0) continue;
                var index = result.FindIndex(r => r.Id == rule.Id);
                if (index >= 0) result[index] = rule;
                else result.Add(rule);
            }
        return result;
    }
}
