using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StreamMark.Avalonia.Extensions;
using StreamMark.Highlighting.TextMate;

namespace AstraPet.ControlPanel;

public partial class ChatWindow : AtomUI.Desktop.Controls.Window
{
    private readonly ChatViewModel _vm;

    public ChatWindow() : this(null!) { }   // 设计器用

    public ChatWindow(ChatViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        AvaloniaXamlLoader.Load(this);

        // 回复里的代码块做语法高亮。服务挂在窗口上，窗口里所有 Markdown 视图共用。
        MarkdownServices.SetServices(this, new MarkdownServices { Plugins = [new TextMatePlugin()] });

        // 关窗只是收起，对话上下文保留。
        Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            Hide();
        };
        vm?.Lines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(), DispatcherPriority.Background);
        Activated += (_, _) => this.FindControl<Control>("Input")?.Focus();
    }

    private async void OnSend(object? sender, RoutedEventArgs e) => await _vm.SendAsync();

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _vm.SendAsync();
        this.FindControl<Control>("Input")?.Focus();
    }

    private void OnClear(object? sender, RoutedEventArgs e) => _vm.Clear();
}
