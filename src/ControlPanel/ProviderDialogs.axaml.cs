using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AstraPet.ControlPanel;

/// <summary>
/// 提供商相关的三个对话框（选择、新增 / 编辑、导入），叠在控制台窗口上。
/// 逻辑都在 <see cref="ProvidersViewModel"/>，这里只转发点击和弹系统文件选择框。
/// </summary>
public partial class ProviderDialogs : UserControl
{
    public ProviderDialogs()
    {
        // 宿主窗口的 DataContext 是 ConsoleViewModel，而这里的编译绑定认的是 ProvidersViewModel。
        // 先置空截断继承，等宿主 XAML 里的 DataContext="{Binding Hub}" 生效，免得加载时先按错的类型求值。
        DataContext = null;
        AvaloniaXamlLoader.Load(this);
    }

    private ProvidersViewModel? Vm => DataContext as ProvidersViewModel;
    private ProvidersViewModel? _watched;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null) _watched.PropertyChanged -= OnVmChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmChanged;
    }

    /// <summary>每次打开选择列表都从头开始看，不要停在上次滚到的位置。</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProvidersViewModel.IsPicker) && _watched is { IsPicker: true })
            Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("PickerScroll")?.ScrollToHome(), DispatcherPriority.Loaded);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Vm?.Close();

    /// <summary>点遮罩关闭，但表单里可能填了一半，所以只有选择和导入两屏响应。</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { IsForm: false } vm) vm.Close();
    }

    // 选择
    private void OnPickPreset(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is PresetItem item) Vm?.BeginAdd(item.Preset);
    }

    // 表单
    private void OnBackToPicker(object? sender, RoutedEventArgs e) => Vm?.BackToPicker();
    private void OnOpenKeyUrl(object? sender, RoutedEventArgs e) => Vm?.OpenKeyUrl();
    private void OnPickModel(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string model) Vm?.PickModel(model);
    }
    private async void OnFetchModels(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.FetchModelsAsync();
    }
    private async void OnTestDraft(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.TestDraftAsync();
    }
    private async void OnUpdateCatalog(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.UpdateCatalogAsync();
    }
    private void OnSave(object? sender, RoutedEventArgs e) => Vm?.SaveForm();
    private void OnDeleteProvider(object? sender, RoutedEventArgs e) => Vm?.DeleteEditing();

    // 导入
    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ImportSourceItem item) Vm?.OpenSource(item);
    }
    private void OnBackToSources(object? sender, RoutedEventArgs e) => Vm?.BackToSources();
    private void OnSelectAll(object? sender, RoutedEventArgs e) => Vm?.SelectAllCandidates(true);
    private void OnSelectNone(object? sender, RoutedEventArgs e) => Vm?.SelectAllCandidates(false);
    private void OnImportSelected(object? sender, RoutedEventArgs e) => Vm?.ImportSelected();

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择配置文件",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.ImportFromFile(path);
    }
}
