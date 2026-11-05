using Avalonia;
using Avalonia.Controls;

namespace AstraPet.ControlPanel;

/// <summary>
/// 控制台里的一行设置：左边标题和说明，右边（或加 <c>stack</c> 类时在下面整行）放控件。
/// <see cref="Value"/> 是标题右侧的当前值（滑块行用）。外观由 ConsoleWindow.axaml 里的 ControlTheme 决定。
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Value));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}
