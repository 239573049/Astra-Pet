using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AstraPet.Pet;

/// <summary>
/// 宠物头顶的对话气泡：单独一个透明置顶窗口，跟着宠物窗口走，鼠标一律穿透。
/// 显示一段时间后自动收起，时长按字数算。
/// </summary>
public sealed class BubbleWindow : Window
{
    // 素材里角色头顶大约在原寸 y=64 处，气泡底边对齐到这里稍往下一点，看起来像从头上冒出来。
    private const double HeadY = 70;
    private const double MaxBubbleWidth = 260;

    private readonly PetWindow _pet;
    private readonly PetSettings _settings;
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#2B2F2E")),
        LineHeight = double.NaN,
    };
    private readonly Border _surface;
    private readonly DispatcherTimer _hideTimer = new();
    private MacWindow? _mac;

    public BubbleWindow(PetWindow pet, PetSettings settings)
    {
        _pet = pet;
        _settings = settings;

        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _surface = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F7FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#22000000")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8),
            MaxWidth = MaxBubbleWidth,
            Child = _text,
        };
        var tail = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 14,0 L 7,8 Z"),
            Fill = _surface.Background,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        Content = new StackPanel { Margin = new Thickness(6), Children = { _surface, tail } };

        _hideTimer.Tick += (_, _) => Dismiss();
        Opened += (_, _) =>
        {
            _mac = MacWindow.TryAttach(this);
            _mac?.MakeTransparent();
            if (_mac is not null) _mac.IgnoresMouse = true;
            ApplyCapture();
            Follow();
        };
        SizeChanged += (_, _) => Follow();
        _pet.PositionChanged += (_, _) => Follow();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetSettings.BubbleFontSize)) _text.FontSize = _settings.BubbleFontSize;
            if (e.PropertyName == nameof(PetSettings.AllowCapture)) ApplyCapture();
            if (e.PropertyName is nameof(PetSettings.Visible) && !_settings.Visible) Dismiss();
        };
        _text.FontSize = _settings.BubbleFontSize;
    }

    /// <summary>显示一句话。<paramref name="seconds"/> 为空时按字数估算，为 0 时一直显示直到下一次调用。</summary>
    public void Say(string text, double? seconds = null)
    {
        if (!_settings.Visible) return;
        _text.Text = text.Trim();
        _hideTimer.Stop();
        var duration = seconds ?? Math.Clamp(2.5 + text.Length * 0.18, 3, 15);
        if (duration > 0)
        {
            _hideTimer.Interval = TimeSpan.FromSeconds(duration);
            _hideTimer.Start();
        }
        if (!IsVisible) Show();
        Follow();
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        if (IsVisible) Hide();
    }

    private void ApplyCapture()
    {
        _mac?.SetCapturable(_settings.AllowCapture);
        WinWindow.SetCapturable(this, _settings.AllowCapture);
    }

    /// <summary>水平居中在宠物上方，底边落在角色头顶；超出屏幕顶端时贴着屏幕上沿。</summary>
    private void Follow()
    {
        if (!IsVisible) return;
        var scaling = DesktopScaling;
        var petScaling = _pet.DesktopScaling;
        var petWidth = _pet.Width * petScaling;
        var head = HeadY * _settings.Scale * petScaling;
        var w = Bounds.Width * scaling;
        var h = Bounds.Height * scaling;
        var x = _pet.Position.X + (petWidth - w) / 2;
        var y = _pet.Position.Y + head - h;
        var area = (Screens.ScreenFromWindow(_pet) ?? Screens.Primary)?.WorkingArea;
        if (area is { } a)
        {
            x = Math.Clamp(x, a.X, Math.Max(a.X, a.Right - w));
            y = Math.Max(y, a.Y);
        }
        Position = new PixelPoint((int)x, (int)y);
    }
}
