using AstraPet.Adapters;
using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AstraPet.Pet;

/// <summary>
/// 状态浮层（design/pet-life/03 §3）：鼠标在角色身上停一秒，头顶出现一张小卡片：名字、亲密度、饱腹、精力、心情。
/// 和对话气泡一样是单独的透明置顶窗口，鼠标穿透；光标离开就收起。
/// </summary>
public sealed class StatusCard : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B2F2E"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#1F000000"));

    private readonly PetWindow _pet;
    private readonly PetLife _life;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Ink };
    private readonly TextBlock _bond = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#D9467A")) };
    private readonly StackPanel _rows = new() { Spacing = 5 };
    private INativeWindow _native = NullNativeWindow.Instance;

    public StatusCard(PetWindow pet, PetLife life, IPlatform platform)
    {
        _pet = pet;
        _life = life;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var header = new DockPanel { Children = { _bond, _title } };
        DockPanel.SetDock(_bond, Dock.Right);
        _bond.Margin = new Thickness(14, 0, 0, 0);
        Content = new Border
        {
            Margin = new Thickness(6),
            Background = new SolidColorBrush(Color.Parse("#F7FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#22000000")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9),
            MinWidth = 210,
            Child = new StackPanel { Spacing = 8, Children = { header, _rows } },
        };

        Opened += (_, _) =>
        {
            _native = this.AttachNative(platform);
            _native.MakeTransparent();
            _native.IgnoresMouse = true;
            Follow();
        };
        SizeChanged += (_, _) => Follow();
        _pet.PositionChanged += (_, _) => Follow();
        _life.Changed += () => { if (IsVisible) Refresh(); };
    }

    public void Present(string name)
    {
        if (!_life.Enabled) return;
        _title.Text = _life.Data.Nickname is { } nick ? $"{name}（{nick}）" : name;
        Refresh();
        if (!IsVisible) Show();
        Follow();
    }

    public void Dismiss()
    {
        if (IsVisible) Hide();
    }

    private void Refresh()
    {
        var d = _life.Data;
        var level = _life.Level;
        _bond.Text = new string('♥', Math.Min(5, (level + 1) / 2)) + new string('♡', 5 - Math.Min(5, (level + 1) / 2))
                     + $" Lv.{level} {LifeRules.LevelName(level)}";
        _rows.Children.Clear();
        _rows.Children.Add(Bar("🍙", "饱腹", d.Satiety, LifeRules.SatietyText(d.Satiety), "#E8954A"));
        _rows.Children.Add(Bar("⚡", "精力", d.Energy, LifeRules.EnergyText(d.Energy), "#2F80ED"));
        _rows.Children.Add(Row("☺", "心情", LifeRules.MoodText(d.Mood)));
        if (d.SnackCount > 0)
            _rows.Children.Add(Row("🎁", "零食", string.Join(" ", d.Snacks.Select(s => $"{Foods.Find(s.Key)?.Icon}×{s.Value}"))));
    }

    private static Control Bar(string icon, string label, double value, string text, string color)
    {
        var fill = new Border { Background = new SolidColorBrush(Color.Parse(color)), CornerRadius = new CornerRadius(3), Height = 6,
                                Width = 90 * Math.Clamp(value, 0, 100) / 100, HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Background = Track, CornerRadius = new CornerRadius(3), Height = 6, Width = 90, Child = fill,
                                 VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{icon} {label}", FontSize = 12, Foreground = Ink, Width = 64 },
                track,
                new TextBlock { Text = text, FontSize = 12, Foreground = Muted },
            },
        };
    }

    private static Control Row(string icon, string label, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new TextBlock { Text = $"{icon} {label}", FontSize = 12, Foreground = Ink, Width = 64 },
            new TextBlock { Text = text, FontSize = 12, Foreground = Muted },
        },
    };

    private void Follow()
    {
        if (!IsVisible) return;
        var scaling = DesktopScaling;
        var w = Bounds.Width * scaling;
        var h = Bounds.Height * scaling;
        var (center, top) = _pet.HeadAnchor;
        var x = center - w / 2;
        var y = top - h;
        var area = (Screens.ScreenFromWindow(_pet) ?? Screens.Primary)?.WorkingArea;
        if (area is { } a)
        {
            x = Math.Clamp(x, a.X, Math.Max(a.X, a.Right - w));
            y = Math.Max(y, a.Y);
        }
        Position = new PixelPoint((int)x, (int)y);
    }
}
