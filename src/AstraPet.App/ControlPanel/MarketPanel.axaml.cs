using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AstraPet.ControlPanel;

/// <summary>「宠物市场」页。逻辑都在 <see cref="MarketViewModel"/>，这里只转发点击。</summary>
public partial class MarketPanel : UserControl
{
    public MarketPanel()
    {
        // 和 ProvidersPanel 一样：先置空截断宿主 ConsoleViewModel 的继承，等 DataContext="{Binding Market}" 生效。
        DataContext = null;
        AvaloniaXamlLoader.Load(this);
    }

    private MarketViewModel? Vm => DataContext as MarketViewModel;

    private static MarketPetItem? ItemOf(object? sender) => (sender as Control)?.Tag as MarketPetItem;

    /// <summary>登录不在市场页做：打开控制台左上角的账号面板。</summary>
    private void OnGoLogin(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as ConsoleWindow)?.OpenAccount();
    private async void OnRefresh(object? sender, RoutedEventArgs e) { if (Vm is { } vm) await vm.RefreshAsync(); }
    private async void OnLoadMore(object? sender, RoutedEventArgs e) { if (Vm is { } vm) await vm.LoadMoreAsync(); }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        e.Handled = true;
        await vm.RefreshAsync();
    }

    private async void OnAction(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && Vm is { } vm) await vm.ActAsync(item);
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm?.Use(item);
    }
}
