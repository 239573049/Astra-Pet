using AstraPet.Core;

namespace AstraPet.Tests;

/// <summary>内存里的假解码器：每段固定帧数，不起 ffmpeg。记下打开过的帧源，方便检查有没有释放。</summary>
internal sealed class FakeDecoder(int frames = 3) : IClipDecoder
{
    public List<FakeSource> Opened { get; } = new();

    public PetClip Probe(string path, string category) =>
        new(Path.GetFileNameWithoutExtension(path), category, path, 24, frames);

    public IFrameSource Open(PetClip clip)
    {
        var source = new FakeSource(clip, frames);
        Opened.Add(source);
        return source;
    }
}

internal sealed class FakeSource(PetClip clip, int frames) : IFrameSource
{
    private int _left = frames;

    public PetClip Clip { get; } = clip;
    public bool Disposed { get; private set; }
    public bool Completed => _left <= 0;

    public byte[]? TryTake() => _left-- > 0 ? new byte[1] : null;

    public void Dispose() => Disposed = true;
}

/// <summary>在临时目录里摆一个角色素材目录：每个分类几个空的 webm（假解码器不读内容）。</summary>
internal sealed class TempLibrary : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "astra-lib-" + Guid.NewGuid().ToString("N"));

    public TempLibrary(bool withSing = false)
    {
        var layout = new Dictionary<string, string[]>
        {
            [PetLibrary.Idle] = ["breathe"],
            [PetLibrary.Click] = ["wave", "shy"],
            [PetLibrary.Drag] = ["lifted"],
            [PetLibrary.Move] = ["run"],
            [PetLibrary.Turn] = ["look"],
        };
        if (withSing) layout[PetLibrary.Sing] = ["song"];
        foreach (var (category, names) in layout)
        {
            Directory.CreateDirectory(Path.Combine(Dir, category));
            foreach (var name in names) File.WriteAllBytes(Path.Combine(Dir, category, name + ".webm"), []);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class PetDirectorTests : IDisposable
{
    private const int Frames = 3;
    private readonly TempLibrary _dir = new();
    private readonly FakeDecoder _decoder = new(Frames);
    private readonly PetSettings _settings = new();

    public void Dispose() => _dir.Dispose();

    private PetDirector Create(int seed = 1)
    {
        var director = new PetDirector(PetLibrary.Load(_dir.Dir, _decoder), _settings, new Random(seed));
        director.Start();
        return director;
    }

    /// <summary>把当前片段播完，返回播完后接上的那一段的分类。</summary>
    private static string PlayThrough(PetDirector director)
    {
        var current = director.Current;
        for (var i = 0; i < 100 && director.Current == current; i++) director.Tick();
        Assert.NotSame(current, director.Current);
        return director.Current.Clip.Category;
    }

    [Fact]
    public void Starts_with_idle_and_yields_every_frame()
    {
        var director = Create();
        Assert.Equal(PetLibrary.Idle, director.Current.Clip.Category);
        Assert.False(director.FromUser);
        for (var i = 0; i < Frames; i++)
        {
            Assert.NotNull(director.Tick());
            Assert.Equal(i, director.Frame);
        }
    }

    [Fact]
    public void Missing_required_category_fails_to_load()
    {
        Directory.Delete(Path.Combine(_dir.Dir, PetLibrary.Turn), true);
        var e = Assert.Throws<InvalidOperationException>(() => PetLibrary.Load(_dir.Dir, _decoder));
        Assert.Contains(PetLibrary.Turn, e.Message);
    }

    [Fact]
    public void User_triggered_clip_returns_to_idle()
    {
        var director = Create();
        director.Play(PetLibrary.Click, fromUser: true);
        Assert.True(director.FromUser);
        Assert.Equal(PetLibrary.Idle, PlayThrough(director));
        Assert.False(director.FromUser);
    }

    [Fact]
    public void Turn_flips_direction_only_after_it_finishes()
    {
        var director = Create();
        var before = director.Direction;
        director.Play(PetLibrary.Turn, fromUser: true);
        director.Tick();
        Assert.Equal(before, director.Direction);
        PlayThrough(director);
        Assert.Equal(-before, director.Direction);
    }

    [Fact]
    public void Dragging_loops_the_drag_animation()
    {
        var director = Create();
        director.Dragging = true;
        director.Play(PetLibrary.Drag, fromUser: true);
        // 按住期间不管是不是用户触发，下一段一律接拖拽。
        Assert.Equal(PetLibrary.Drag, PlayThrough(director));
        Assert.Equal(PetLibrary.Drag, PlayThrough(director));
        director.Dragging = false;
    }

    [Fact]
    public void Interrupting_disposes_current_and_prefetched_sources()
    {
        var director = Create();
        var before = _decoder.Opened.ToList();   // 当前一段 + 预取的下一段
        Assert.Equal(2, before.Count);
        director.Play(PetLibrary.Click, fromUser: true);
        Assert.All(before, s => Assert.True(s.Disposed));
        Assert.False(((FakeSource)director.Current).Disposed);
    }

    [Fact]
    public void Power_saving_only_plays_idle()
    {
        _settings.PowerSavingActive = true;
        var director = Create();
        for (var i = 0; i < 30; i++)
            Assert.Equal(PetLibrary.Idle, PlayThrough(director));
    }

    [Fact]
    public void Stay_still_never_moves()
    {
        _settings.StayStill = true;
        var director = Create(seed: 42);
        var seen = new HashSet<string>();
        for (var i = 0; i < 300; i++) seen.Add(PlayThrough(director));
        Assert.DoesNotContain(PetLibrary.Move, seen);
        Assert.Contains(PetLibrary.Click, seen);   // 确认随机链确实在跑，而不是一直待机
    }

    [Fact]
    public void Music_switches_to_sing_and_falls_back_to_click_without_sing_clips()
    {
        _settings.MusicDetect = true;
        _settings.MusicPlaying = true;
        var director = Create();
        // 没有唱歌素材时用动作片段代替。
        Assert.Equal(PetLibrary.Click, PlayThrough(director));
    }
}
