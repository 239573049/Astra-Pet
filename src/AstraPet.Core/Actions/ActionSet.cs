namespace AstraPet.Core;

/// <summary>
/// 一只宠物的全部动作（包里的 + 用户覆盖后的），按 id、按角色查询，按权重随机。
/// 设计见 design/pet-studio/01-action-model.md §3.4。
/// </summary>
public sealed class ActionSet
{
    private readonly List<ActionDef> _all;
    private readonly Dictionary<string, ActionDef> _byId = new();

    public ActionSet(IEnumerable<ActionDef> actions)
    {
        _all = new List<ActionDef>();
        foreach (var action in actions)
        {
            // id 重复时第一个生效：调用方（清单解析、覆盖层合并）负责改名并记日志。
            if (!_byId.TryAdd(action.Id, action)) continue;
            _all.Add(action);
        }
    }

    public IReadOnlyList<ActionDef> All => _all;

    public ActionDef? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>启用的、在这个姿态下、有这个角色的动作（不走回退链）。<paramref name="stance"/> 为 null 时不限姿态。</summary>
    public IReadOnlyList<ActionDef> WithRole(string role, string? stance = Stances.Ground) =>
        _all.Where(a => a.Enabled && a.HasRole(role) && (stance is null || a.Stance == stance)).ToList();

    public bool HasRole(string role, string? stance = Stances.Ground) =>
        _all.Any(a => a.Enabled && a.HasRole(role) && (stance is null || a.Stance == stance));

    /// <summary>缺动作的必需角色（只看地面姿态）；空表示能用。</summary>
    public IReadOnlyList<string> MissingRequired() => Roles.Required.Where(r => !HasRole(r)).ToList();

    /// <summary>
    /// 按角色找一个动作：在 <paramref name="stance"/> 姿态下先找角色本身，没有就走回退链，同一层里等概率。
    /// <paramref name="filter"/> 可以再排除一些（比如没解锁的）。全都没有返回 null。
    /// </summary>
    public ActionDef? PickByRole(string role, Random random, string stance = Stances.Ground, Func<ActionDef, bool>? filter = null)
    {
        foreach (var r in Roles.Chain(role))
        {
            var candidates = WithRole(r, stance);
            if (filter is not null) candidates = candidates.Where(filter).ToList();
            if (candidates.Count > 0)
                return candidates[random.Next(candidates.Count)];
        }
        return null;
    }

    /// <summary>
    /// 动作 id 或 <c>role:&lt;角色&gt;</c>；也接受直接写角色名（Agent 传的情绪）。
    /// 动作 id 优先：禁用的动作按 id 点名时也不播，退回按同名角色找。
    /// 角色按 <paramref name="stance"/> 找；当前姿态下没有、而角色是「要换个地方做」的（<see cref="Roles.Navigable"/>）时，
    /// 返回别的姿态里的动作，由 Director 播过渡动作走过去。
    /// </summary>
    public ActionDef? Resolve(string reference, Random random, string stance = Stances.Ground, Func<ActionDef, bool>? filter = null)
    {
        if (reference.StartsWith("role:", StringComparison.Ordinal))
            return ResolveRole(reference[5..], random, stance, filter);
        if (Find(reference) is { Enabled: true } action && (filter is null || filter(action)))
            return action;
        return ResolveRole(reference, random, stance, filter);
    }

    private ActionDef? ResolveRole(string role, Random random, string stance, Func<ActionDef, bool>? filter)
    {
        if (PickByRole(role, random, stance, filter) is { } here) return here;
        if (!Roles.Navigable.Contains(role)) return null;
        var elsewhere = _all.Where(a => a.Enabled && a.HasRole(role) && (filter is null || filter(a))).ToList();
        // 优先挑过渡动作（比如「入睡」本身就把宠物带进睡觉的姿态），其次挑地面上的。
        var pick = elsewhere.Where(a => a.Transition is not null && a.Stance == stance).ToList() is { Count: > 0 } t ? t
            : elsewhere.Where(a => a.Stance == Stances.Ground).ToList() is { Count: > 0 } g ? g : elsewhere;
        return pick.Count > 0 ? pick[random.Next(pick.Count)] : null;
    }

    /// <summary>这个姿态下启用的动作。</summary>
    public IEnumerable<ActionDef> InStance(string stance) => _all.Where(a => a.Enabled && a.Stance == stance);

    /// <summary>
    /// 按 <see cref="ActionDef.Ambient"/> 权重从允许的动作里抽一个；权重全为 0 时返回 null（调用方回待机）。
    /// </summary>
    /// <param name="weight">权重调制（design/pet-life/06 §3.3）：返回乘在 <see cref="ActionDef.Ambient"/> 上的倍率；null 不调制。</param>
    public ActionDef? PickAmbient(Random random, Func<ActionDef, bool> allowed, Func<ActionDef, double>? weight = null)
    {
        double W(ActionDef a) => weight is null ? a.Ambient : a.Ambient * Math.Max(0, weight(a));

        var total = 0.0;
        foreach (var a in _all)
            if (a.Enabled && a.Ambient > 0 && allowed(a))
                total += W(a);
        if (total <= 0) return null;

        var roll = random.NextDouble() * total;
        ActionDef? last = null;
        foreach (var a in _all)
        {
            if (!a.Enabled || a.Ambient <= 0 || !allowed(a)) continue;
            var w = W(a);
            if (w <= 0) continue;
            last = a;
            if (roll < w) return a;
            roll -= w;
        }
        return last;   // 浮点误差兜底
    }
}
