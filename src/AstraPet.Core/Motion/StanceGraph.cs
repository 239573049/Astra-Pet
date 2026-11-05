namespace AstraPet.Core;

/// <summary>
/// 姿态图（design/pet-life/04 §3）：节点是姿态，边是过渡动作。地面姿态总是存在；
/// 其他姿态要「可用」才算数：至少有一个这个姿态下的 idle 动作，能从地面走进来，也能回到地面（直接回，或者经 air 掉回地面）。
/// 不可用的姿态整组忽略，所以只做了一半的墙组动作不会把宠物卡在墙上。
/// </summary>
public sealed class StanceGraph
{
    private readonly Dictionary<string, List<ActionDef>> _edges = new();
    private readonly HashSet<string> _available = new();

    public StanceGraph(IEnumerable<ActionDef> actions)
    {
        var list = actions.Where(a => a.Enabled).ToList();
        foreach (var a in list)
            if (a.Transition is { } t)
                (_edges.TryGetValue(t.From, out var e) ? e : _edges[t.From] = new()).Add(a);

        var withIdle = list.Where(a => a.Transition is null && a.HasRole(Roles.Idle)).Select(a => a.Stance).ToHashSet();
        withIdle.Add(Stances.Ground);

        // 从地面能走到的、并且能回到地面（或掉进 air）的、有待机的姿态。只沿着「有待机」的姿态走：中间站也要能停留。
        var reachable = Reach(Stances.Ground, withIdle);
        foreach (var stance in reachable.Where(withIdle.Contains))
        {
            if (stance == Stances.Ground) { _available.Add(stance); continue; }
            var back = Reach(stance, withIdle);
            if (back.Contains(Stances.Ground) || back.Contains(Stances.Air)) _available.Add(stance);
        }
        _available.Add(Stances.Ground);
    }

    /// <summary>可用的姿态（含地面）。</summary>
    public IReadOnlySet<string> Available => _available;

    public bool IsAvailable(string stance) => stance == Stances.Air || _available.Contains(stance);

    /// <summary>
    /// 从 <paramref name="from"/> 到 <paramref name="to"/> 要依次播的过渡动作（BFS，最短）；已经在那里返回空列表，走不到返回 null。
    /// 终点是 air 之外的物理姿态时不走（掉下来之后由物理接回地面）。
    /// </summary>
    public IReadOnlyList<ActionDef>? Path(string from, string to, Func<ActionDef, bool>? usable = null)
    {
        if (from == to) return [];
        var previous = new Dictionary<string, ActionDef>();
        var queue = new Queue<string>();
        queue.Enqueue(from);
        var seen = new HashSet<string> { from };
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (!_edges.TryGetValue(at, out var edges)) continue;
            foreach (var edge in edges)
            {
                var next = edge.Transition!.To;
                if (seen.Contains(next) || !IsAvailable(next) || (usable is not null && !usable(edge))) continue;
                seen.Add(next);
                previous[next] = edge;
                if (next == to)
                {
                    var path = new List<ActionDef>();
                    for (var s = to; s != from; s = previous[s].Transition!.From) path.Add(previous[s]);
                    path.Reverse();
                    return path;
                }
                if (next != Stances.Air) queue.Enqueue(next);
            }
        }
        return null;
    }

    private HashSet<string> Reach(string start, HashSet<string> stayable)
    {
        var seen = new HashSet<string> { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (!_edges.TryGetValue(at, out var edges)) continue;
            foreach (var next in edges.Select(e => e.Transition!.To))
            {
                if (!seen.Add(next)) continue;
                if (stayable.Contains(next)) queue.Enqueue(next);
            }
        }
        return seen;
    }

    /// <summary>
    /// 去掉不可用姿态里的动作（以及进出它们的过渡），返回保留下来的；每个被去掉的姿态记一条日志，说明缺什么。
    /// 加载角色和工作台导出前都用它，两边结论一致。
    /// </summary>
    public static List<ActionDef> Prune(IReadOnlyList<ActionDef> actions, Action<string>? log = null)
    {
        var graph = new StanceGraph(actions);
        var dropped = actions.Where(a => !graph.IsAvailable(a.Stance) || (a.Transition is { } t && !graph.IsAvailable(t.To)))
            .ToList();
        foreach (var stance in dropped.Select(a => a.Transition is { } t && graph.IsAvailable(t.From) ? t.To : a.Stance).Distinct())
            log?.Invoke($"姿态「{stance}」不可用（{graph.Explain(actions, stance)}），这一组动作先不用");
        return actions.Except(dropped).ToList();
    }

    /// <summary>这个姿态为什么不可用，给人看的。可用时返回空串。</summary>
    public string Explain(IReadOnlyList<ActionDef> actions, string stance)
    {
        if (IsAvailable(stance)) return "";
        var enabled = actions.Where(a => a.Enabled).ToList();
        var missing = new List<string>();
        if (!enabled.Any(a => a.Stance == stance && a.Transition is null && a.HasRole(Roles.Idle))) missing.Add("缺这个姿态的待机");
        if (!enabled.Any(a => a.Transition?.To == stance)) missing.Add("缺进入的过渡动作");
        if (!enabled.Any(a => a.Transition?.From == stance)) missing.Add("缺离开的过渡动作");
        return missing.Count > 0 ? string.Join("、", missing) : "从地面走不到或回不来";
    }
}
