using System.Diagnostics.CodeAnalysis;

namespace AstraPet.Core;

/// <summary>
/// 动画状态机，不碰窗口：决定下一段播什么、管理解码流、数帧号、记朝向。
/// 规则照原版 window.py：一段播完按 <see cref="PetSettings.Chain"/> 的权重抽下一段；
/// 用户操作触发的片段播完先回待机，待机播完再进随机链；转向片段播完才翻转朝向。
/// 窗口每个 tick 调 <see cref="Tick"/> 取一帧，在 <see cref="ClipStarted"/> 里处理走路之类和位置有关的事。
/// </summary>
public sealed class PetDirector : IDisposable
{
    private PetLibrary _library;
    private readonly PetSettings _settings;
    private readonly Random _random = new();

    private ClipStream? _queued;               // 预先开好的下一段，切换时首帧已经在队列里
    private DateTime _restUntil;               // 动作间隔：这之前随机链只抽待机

    public PetDirector(PetLibrary library, PetSettings settings)
    {
        _library = library;
        _settings = settings;
    }

    public PetLibrary Library => _library;

    /// <summary>正在播的片段。调过 <see cref="Start"/> 之后才有。</summary>
    public ClipStream Current { get; private set; } = null!;

    /// <summary>最近一次取到的帧在当前片段里的帧号，换片段时为 -1。</summary>
    public int Frame { get; private set; }

    /// <summary>-1 朝左（素材原始朝向），+1 朝右。</summary>
    public int Direction { get; set; } = -1;

    /// <summary>当前片段由用户操作触发，播完回待机。</summary>
    public bool FromUser { get; private set; }

    /// <summary>按住拖拽期间为 true，下一段一律接拖拽动画。</summary>
    public bool Dragging { get; set; }

    /// <summary>新片段开始播（还没取首帧）。</summary>
    public event Action<ClipStream>? ClipStarted;

    public bool Mirrored => Direction > 0 && _library.CanMirror(Current.Clip);

    public void Start() => Play(PetLibrary.Idle, fromUser: false);

    /// <summary>换角色：丢掉旧角色在播和预取的解码流，换上新素材库，从待机重新开始。</summary>
    public void UseLibrary(PetLibrary library)
    {
        _queued?.Dispose();
        _queued = null;
        Current?.Dispose();
        _library = library;
        Start();
    }

    /// <summary>取下一帧。解码没跟上返回 null，调用方停在当前帧；当前片段播完就接下一段。</summary>
    public byte[]? Tick()
    {
        var frame = Current.TryTake();
        if (frame is null)
        {
            if (!Current.Completed) return null;
            Finish();
            frame = Current.TryTake();
            if (frame is null) return null;
        }
        Frame++;
        return frame;
    }

    /// <summary>立即打断当前片段，改播 category 里随机一段。</summary>
    public void Play(string category, bool fromUser) => Play(_library.Pick(category, _random), fromUser);

    /// <summary>立即打断当前片段，改播指定片段。</summary>
    public void Play(PetClip clip, bool fromUser)
    {
        _queued?.Dispose();
        _queued = null;
        Current?.Dispose();
        Begin(_library.Open(clip), fromUser);
    }

    private void Finish()
    {
        if (Current.Clip.Category == PetLibrary.Turn)
            Direction = -Direction;
        if (Current.Clip.Category != PetLibrary.Idle && !FromUser)
            _restUntil = DateTime.Now.AddSeconds(_settings.ActionInterval);

        var next = _queued ?? _library.Open(_library.Pick(NextCategory(), _random));
        _queued = null;
        Current.Dispose();
        Begin(next, fromUser: false);
    }

    [MemberNotNull(nameof(Current))]
    private void Begin(ClipStream stream, bool fromUser)
    {
        Current = stream;
        FromUser = fromUser;
        Frame = -1;
        ClipStarted?.Invoke(stream);
        _queued = _library.Open(_library.Pick(NextCategory(), _random));
    }

    /// <summary>
    /// 按住拖拽时循环拖拽动画；用户操作之后回待机；在放音乐时唱歌；
    /// 动作间隔内只待机；其余按权重随机，权重全为 0 时待机。
    /// 这是在当前片段开始时为下一段预取调用的，所以间隔判断要看当前片段播完的时刻。
    /// </summary>
    private string NextCategory()
    {
        if (Dragging) return PetLibrary.Drag;
        if (FromUser) return PetLibrary.Idle;
        if (_settings is { MusicDetect: true, MusicPlaying: true, PowerSavingActive: false })
            return PetLibrary.Sing;

        var clip = Current.Clip;
        if (_settings.ActionInterval > 0)
        {
            if (clip.Category != PetLibrary.Idle) return PetLibrary.Idle;
            var endsAt = DateTime.Now.AddSeconds(clip.EstimatedFrames / clip.Fps / _settings.EffectiveSpeed);
            if (endsAt < _restUntil) return PetLibrary.Idle;
        }

        var chain = _settings.Chain;
        var total = chain.Sum(c => c.Weight);
        if (total <= 0) return PetLibrary.Idle;
        var roll = _random.Next(total);
        foreach (var (category, weight) in chain)
        {
            if (roll < weight) return category;
            roll -= weight;
        }
        return PetLibrary.Idle;
    }

    public void Dispose()
    {
        _queued?.Dispose();
        _queued = null;
        Current?.Dispose();
    }
}
