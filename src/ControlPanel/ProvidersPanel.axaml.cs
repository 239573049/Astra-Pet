using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AstraPet.ControlPanel;

/// <summary>「智能」页上的提供商卡片区。逻辑都在 <see cref="ProvidersViewModel"/>，这里只转发点击。</summary>
public partial class ProvidersPanel : UserControl
{
    public ProvidersPanel()
    {
        // 宿主窗口的 DataContext 是 ConsoleViewModel，而这里的编译绑定认的是 ProvidersViewModel。
        // 先置空截断继承，等宿主 XAML 里的 DataContext="{Binding Hub}" 生效，免得加载时先按错的类型求值。
        DataContext = null;
        AvaloniaXamlLoader.Load(this);
    }

    private ProvidersViewModel? Vm => DataContext as ProvidersViewModel;

    private void OnAdd(object? sender, RoutedEventArgs e) => Vm?.OpenPicker();
    private void OnImport(object? sender, RoutedEventArgs e) => Vm?.OpenImport();

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.Tag is ProviderCard card) Vm?.BeginEdit(card);
    }

    /// <summary>卡片底部是操作区（测试、编辑、开关），点到空白处不要当成点了整张卡片。</summary>
    private void OnFooterTapped(object? sender, TappedEventArgs e) => e.Handled = true;

    private async void OnTestCard(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ProviderCard card && Vm is { } vm) await vm.TestCardAsync(card);
    }

    private void OnEditCard(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ProviderCard card) Vm?.BeginEdit(card);
    }
}
