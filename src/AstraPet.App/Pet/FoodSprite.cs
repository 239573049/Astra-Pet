using AstraPet.Adapters;
using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AstraPet.Pet;

/// <summary>
/// 喂食时飞向嘴边的食物图标（design/pet-life/03 §2.4）：一个小透明窗口，从光标处沿抛物线飞 0.4 秒，到了就消失并回调。
/// 吃不下时飞到一半掉下去。鼠标穿透。
/// </summary>
public sealed class FoodSprite : Window
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private (double X, double Y) _from, _to;
    private bool _drop;
    private Action? _arrived;

    // 拿在手上（design/pet-life/03 §2.1 方案 B）：图标跟着光标，移到宠物身上停一下就喂。
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private Func<(double X, double Y)?>? _cursor;
    private Func<bool>? _overPet;
    private DateTime _holdUntil, _overSince;
    private bool _over;
    private Action? _timeout;

    public FoodSprite(IPlatform platform)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Width = Height = 44;
        Content = new TextBlock { FontSize = 30, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                  VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Opened += (_, _) =>
        {
            var native = this.AttachNative(platform);
            native.MakeTransparent();
            native.IgnoresMouse = true;
        };
        _timer.Tick += (_, _) => Step();
        _holdTimer.Tick += (_, _) => HoldStep();
    }

    public bool Holding => _holdTimer.IsEnabled;

    /// <summary>
    /// 拿着食物：图标跟着 <paramref name="cursor"/>（桌面像素），<paramref name="overPet"/> 为真持续 0.3 秒就调 <paramref name="fed"/>；
    /// <paramref name="patience"/> 之内没喂到就调 <paramref name="timeout"/>（通常改成直接飞过去）。
    /// </summary>
    public void Hold(Food food, Func<(double X, double Y)?> cursor, Func<bool> overPet, TimeSpan patience, Action fed, Action timeout)
    {
        _timer.Stop();
        ((TextBlock)Content!).Text = food.Icon;
        (_cursor, _overPet, _arrived, _timeout) = (cursor, overPet, fed, timeout);
        _holdUntil = DateTime.Now + patience;
        _over = false;
        if (cursor() is { } at) Place((at.X + 18 * DesktopScaling, at.Y + 18 * DesktopScaling));
        if (!IsVisible) Show();
        _holdTimer.Start();
    }

    private void HoldStep()
    {
        if (_cursor?.Invoke() is not { } at || DateTime.Now > _holdUntil)
        {
            _holdTimer.Stop();
            var timeout = _timeout;
            _timeout = null;
            timeout?.Invoke();
            return;
        }
        // 图标在光标右下一点，不挡住光标下面的宠物（命中判断看的是光标）。
        Place((at.X + 18 * DesktopScaling, at.Y + 18 * DesktopScaling));
        var over = _overPet?.Invoke() == true;
        if (over && !_over) _overSince = DateTime.Now;
        _over = over;
        if (!over || DateTime.Now - _overSince < TimeSpan.FromMilliseconds(300)) return;
        _holdTimer.Stop();
        Hide();
        var fed = _arrived;
        _arrived = null;
        fed?.Invoke();
    }

    /// <summary>从 <paramref name="from"/> 飞到 <paramref name="to"/>（桌面像素，图标中心），到了调 <paramref name="arrived"/>。</summary>
    public void Fly(Food food, (double X, double Y) from, (double X, double Y) to, bool drop, Action arrived)
    {
        _holdTimer.Stop();
        ((TextBlock)Content!).Text = food.Icon;
        (_from, _to, _drop, _arrived) = (from, to, drop, arrived);
        _clock.Restart();
        Place(from);
        if (!IsVisible) Show();
        _timer.Start();
    }

    private void Step()
    {
        var t = Math.Clamp(_clock.Elapsed / Duration, 0, 1);
        var x = _from.X + (_to.X - _from.X) * t;
        var arc = Math.Abs(_to.X - _from.X) * 0.25 + 40 * DesktopScaling;
        var y = _from.Y + (_to.Y - _from.Y) * t - arc * 4 * t * (1 - t);
        if (_drop && t > 0.5) y += (t - 0.5) * (t - 0.5) * 800 * DesktopScaling;   // 吃不下：半路掉下去
        Place((x, y));
        if (t < 1) return;
        _timer.Stop();
        Hide();
        var done = _arrived;
        _arrived = null;
        done?.Invoke();
    }

    private void Place((double X, double Y) center)
    {
        var half = Width * DesktopScaling / 2;
        Position = new PixelPoint((int)(center.X - half), (int)(center.Y - half));
    }
}
