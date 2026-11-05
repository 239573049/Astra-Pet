using System.Collections.ObjectModel;
using AstraPet.Core;
using AstraPet.Pet;
using Avalonia.Threading;

namespace AstraPet.ControlPanel;

/// <summary>
/// 「动作」页的一行：包装一个 <see cref="ActionDef"/>，属性的 setter 把改动写进覆盖层（不改角色包）。
/// </summary>
public sealed class ActionItem(ActionsViewModel owner, ActionDef def) : Observable
{
    public ActionDef Def { get; private set; } = def;

    public string Id => Def.Id;
    public string Name { get => Def.DisplayName; set => owner.Edit(this, o => o.Name = value.Trim() is { Length: > 0 } n ? n : null); }
    public string Description { get => Def.Description; set => owner.Edit(this, o => o.Description = value.Trim()); }

    /// <summary>角色，空格或逗号分隔。</summary>
    public string RolesText
    {
        get => string.Join(" ", Def.Roles);
        set => owner.Edit(this, o => o.Roles = value.Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.ToLowerInvariant()).Where(ActionDef.IsValidId).Distinct().ToList() is { Count: > 0 } roles ? roles : null);
    }

    public double Ambient { get => Math.Round(Def.Ambient, 1); set => owner.Edit(this, o => o.Ambient = Math.Round(Math.Max(0, value), 1)); }
    public double Cooldown { get => Def.Cooldown.TotalSeconds; set => owner.Edit(this, o => o.Cooldown = Math.Max(0, value)); }
    public bool Mirror { get => Def.Mirror; set => owner.Edit(this, o => o.Mirror = value); }

    public bool Enabled
    {
        get => Def.Enabled;
        set { if (value != Def.Enabled && (value || CanDisable)) owner.Edit(this, o => o.Enabled = value); }
    }

    /// <summary>台词，一行一句。</summary>
    public string LinesText
    {
        get => string.Join("\n", Def.Lines);
        set => owner.Edit(this, o => o.Lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    public bool IsUser => Def.Origin == ActionOrigin.User;
    public string Meta => $"{Id} · {RolesText}" + (IsUser ? " · 我的" : "") + (Def.Clips.Count > 1 ? $" · {Def.Clips.Count} 个片段" : "");
    public string AmbientText => Def.Ambient > 0 ? $"随机 {Def.Ambient:0.#}" : "不随机";

    /// <summary>某个必需角色只剩这一个启用的动作时不能禁用（D14）。</summary>
    public bool CanDisable => !Def.Enabled || owner.CanDisable(Def);
    public string DisableNote => CanDisable ? "" : "它是某个必需角色（待机 / 点击 / 拖拽 / 移动 / 转身）唯一的动作，不能禁用。";

    public bool HasOverride => owner.HasOverride(Id);

    /// <summary>哪些触发会播到它，给人看的一行一条。</summary>
    public string TriggersText => owner.DescribeTriggers(Def);

    public IReadOnlyList<ActionBinding> Bindings => owner.BindingsOf(Def);

    internal void Refresh(ActionDef def)
    {
        Def = def;
        foreach (var name in new[] { nameof(Name), nameof(Description), nameof(RolesText), nameof(Ambient), nameof(Cooldown),
                     nameof(Mirror), nameof(Enabled), nameof(LinesText), nameof(IsUser), nameof(Meta), nameof(AmbientText),
                     nameof(CanDisable), nameof(DisableNote), nameof(HasOverride), nameof(TriggersText), nameof(Bindings) })
            Raise(name);
    }
}

/// <summary>绑到某个动作上的一条宠物规则（「绑定触发」加的）。</summary>
public sealed record ActionBinding(string RuleId, string Label);

/// <summary>「绑定触发」下拉框的一项。</summary>
public sealed record EventOption(string Type, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 控制台「动作」页：列出当前宠物的动作，▶ 预览、启用 / 禁用、改名字描述角色权重台词、绑定触发。
/// 改动都写进覆盖层（<see cref="ActionOverlay"/>），停手半秒后存盘。设计见 design/pet-studio/01-action-model.md §4.4。
/// </summary>
public sealed class ActionsViewModel : Observable
{
    private readonly PetDirector _director;
    private readonly PetCompanion _companion;
    private readonly PetSettings _settings;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ActionItem? _selected;
    private EventOption? _bindEvent;
    private bool _applying;
    private bool _dirty;

    public ActionsViewModel(PetDirector director, PetCompanion companion, PetSettings settings, string ffmpeg)
    {
        _director = director;
        _companion = companion;
        _settings = settings;
        Import = new ActionImportViewModel(this, ffmpeg);
        _saveTimer.Tick += (_, _) => Save();
        EventOptions = PetEvents.Bindable.Select(b => new EventOption(b.Type, b.Label)).ToList();
        _bindEvent = EventOptions[0];
        Reload();
    }

    private PetLibrary Library => _director.Library;

    /// <summary>导入视频（01 §4.3）。</summary>
    public ActionImportViewModel Import { get; }

    internal string? OverlayDirectory => Library.OverlayDirectory;

    public ObservableCollection<ActionItem> Items { get; } = new();

    public ActionItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Raise(nameof(HasSelection)); }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>能不能自定义：开发素材（ASTRA_PET_VIDEOS）没有覆盖层。</summary>
    public bool CanCustomize => Library.OverlayDirectory is not null;
    public bool CannotCustomize => !CanCustomize;

    public string Summary => $"{Library.Actions.All.Count} 个动作，" +
                             $"{Library.Actions.All.Count(a => a.Enabled)} 个启用 · 格式 {Library.Format}" +
                             (Library.Format == 1 ? " · 老格式的角色没有动作描述，补一句 Agent 才知道每个动作是做什么的" : "");

    public IReadOnlyList<EventOption> EventOptions { get; }
    public EventOption? BindEvent { get => _bindEvent; set => Set(ref _bindEvent, value); }

    /// <summary>换了宠物：重建列表。换之前先调 <see cref="Save"/>，免得没存的改动写到新宠物的覆盖层里。</summary>
    public void Reload()
    {
        _dirty = false;
        Import.Discard();
        var selectedId = _selected?.Id;
        Items.Clear();
        foreach (var action in Library.Actions.All)
            Items.Add(new ActionItem(this, action));
        Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Items.FirstOrDefault();
        Raise(nameof(Summary));
        Raise(nameof(CanCustomize));
        Raise(nameof(CannotCustomize));
    }

    /// <summary>▶：立即在宠物上播一次（用户优先级，播完回待机）。</summary>
    public void Play(ActionItem item) =>
        _director.Request(new ActionRequest(item.Def with { Enabled = true }, ActionPriority.User, "console"));

    public void Say(ActionItem item)
    {
        if (item.Def.Lines.Count > 0) _companion.Say(item.Def.Lines[Random.Shared.Next(item.Def.Lines.Count)]);
    }

    /// <summary>恢复默认：去掉这个动作的全部覆盖和绑定的触发。</summary>
    public void ResetSelected()
    {
        if (_selected is not { } item) return;
        Library.Overlay.Overrides.Remove(item.Id);
        Library.Overlay.Triggers.RemoveAll(r => r.Do == item.Id);
        Apply();
    }

    public void BindSelected()
    {
        if (_selected is not { } item || _bindEvent is not { } e) return;
        var id = $"bind-{item.Id}-{e.Type.Replace('.', '-')}";
        Library.Overlay.Triggers.RemoveAll(r => r.Id == id);
        Library.Overlay.Triggers.Add(new TriggerRule
        {
            Id = id, On = e.Type, Do = item.Id,
            Priority = e.Type.StartsWith("pet.", StringComparison.Ordinal) ? "user" : "system",
        });
        Apply();
    }

    public void Unbind(string ruleId)
    {
        Library.Overlay.Triggers.RemoveAll(r => r.Id == ruleId);
        Apply();
    }

    /// <summary>导入的动作用的 id：my-1、my-2……，不和包里、覆盖层里、磁盘上已有的重复。</summary>
    internal string NewActionId()
    {
        for (var n = 1; ; n++)
        {
            var id = $"my-{n}";
            if (Library.Actions.Find(id) is null && Library.Overlay.Added.All(a => a.Id != id)
                && (OverlayDirectory is not { } dir || !Directory.Exists(Path.Combine(dir, "clips", id))))
                return id;
        }
    }

    /// <summary>导入的动作加进覆盖层，立即存盘、选中并在宠物上播一次。</summary>
    internal void AddImported(AddedAction added)
    {
        Library.Overlay.Added.Add(added);
        Apply();
        Save();
        Reload();
        if (Items.FirstOrDefault(i => i.Id == added.Id) is { } item)
        {
            Selected = item;
            Play(item);
        }
        else
            PetLog.Write($"导入的动作 {added.Id} 没加载上，见上面的日志");
    }

    /// <summary>删除用户自己加的动作（连同片段和绑定的触发）。包里的动作只能禁用。</summary>
    public void DeleteSelected()
    {
        if (_selected is not { IsUser: true } item || OverlayDirectory is not { } dir) return;
        if (Library.Overlay.Added.FirstOrDefault(a => a.Id == item.Id) is not { } added) return;
        Library.Overlay.Added.Remove(added);
        Library.Overlay.Overrides.Remove(item.Id);
        Library.Overlay.Triggers.RemoveAll(r => r.Do == item.Id);
        if (_director.CurrentAction.Id == item.Id) _director.Play(Roles.Idle, fromUser: false);
        Apply();
        Save();
        Selected = null;
        Reload();
        foreach (var clip in added.Clips)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, clip)));
            if (folder is null || !folder.StartsWith(Path.Combine(Path.GetFullPath(dir), "clips") + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PetLog.Write($"删除动作片段失败（可能还在播放，下次启动再删）: {e.Message}"); }
        }
    }

    internal void Edit(ActionItem item, Action<ActionOverride> change)
    {
        if (_applying || !CanCustomize) return;
        change(Library.Overlay.OverrideFor(item.Id));
        Apply();
    }

    internal bool HasOverride(string id) =>
        Library.Overlay.Overrides.TryGetValue(id, out var o) && !o.IsEmpty || Library.Overlay.Triggers.Any(r => r.Do == id);

    internal bool CanDisable(ActionDef action) =>
        Roles.Required.Where(action.HasRole).All(r => Library.Actions.WithRole(r).Count > 1);

    internal IReadOnlyList<ActionBinding> BindingsOf(ActionDef action) =>
        Library.Overlay.Triggers.Where(r => r.Do == action.Id)
            .Select(r => new ActionBinding(r.Id, PetEvents.LabelOf(r.On))).ToList();

    internal string DescribeTriggers(ActionDef action)
    {
        var table = TriggerTable.Merge(DefaultTriggers.All, _settings.Triggers, Library.Overlay.Triggers);
        var lines = new List<string>();
        foreach (var rule in table.Where(r => r.Enabled))
        {
            if (rule.Do == action.Id)
                lines.Add($"{PetEvents.LabelOf(rule.On)}（绑定）");
            else if (rule.Do.StartsWith("role:", StringComparison.Ordinal) && action.HasRole(rule.Do[5..]))
                lines.Add($"{PetEvents.LabelOf(rule.On)}（来自角色 {rule.Do[5..]}）");
        }
        if (action.Ambient > 0) lines.Add("空闲时随机");
        if (action.Roles.Any(r => Roles.Emotions.Contains(r) || r is Roles.Click or Roles.Greet or Roles.Celebrate))
            lines.Add("Agent 可以点名");
        return lines.Count == 0 ? "只能手动播放" : string.Join(" · ", lines.Distinct());
    }

    /// <summary>合并覆盖层并刷新各行；停手后存盘。</summary>
    private void Apply()
    {
        try
        {
            _applying = true;
            Library.ApplyOverlay(Library.Overlay, save: false);
            foreach (var item in Items)
                if (Library.Actions.Find(item.Id) is { } def)
                    item.Refresh(def);
            Raise(nameof(Summary));
        }
        catch (Exception e)
        {
            PetLog.Write($"应用动作设置失败: {e.Message}");
        }
        finally
        {
            _applying = false;
        }
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即存盘（关控制台、换宠物前也调）。</summary>
    public void Save()
    {
        _saveTimer.Stop();
        if (!_dirty || Library.OverlayDirectory is not { } dir) return;
        _dirty = false;
        try { Library.Overlay.Save(dir); }
        catch (Exception e) { PetLog.Write($"保存动作设置失败: {e.Message}"); }
    }
}
