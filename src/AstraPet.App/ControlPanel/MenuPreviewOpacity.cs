using System.Globalization;
using Avalonia.Data.Converters;

namespace AstraPet.ControlPanel;

/// <summary>菜单预览：现在不会出现在菜单里的项置灰。</summary>
public sealed class MenuPreviewOpacity : IValueConverter
{
    public static readonly MenuPreviewOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is false ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
