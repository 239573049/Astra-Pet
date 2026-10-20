using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AstraPet.ControlPanel;

public static class ConsoleConverters
{
    /// <summary>测试结果的颜色：成功绿，其余（进行中、失败）浅红/灰由文字本身说明。</summary>
    public static readonly IValueConverter OkBrush = new FuncValueConverter<bool, IBrush>(ok =>
        ok ? Brush.Parse("#8AC8AA") : Brush.Parse("#D9A7A7"));
}

public partial class ConsoleWindow : AtomUI.Desktop.Controls.Window
{
    private readonly ConsoleViewModel _vm;

    // 动画和预热
    private Border? _navPill;
    private StackPanel? _pageHeader;
    private Grid? _pageHost;
    private double _pillTop;
    private bool _warming;
    private bool _revealRequested;

    public ConsoleWindow() : this(null!) { }   // 设计器用

    public ConsoleWindow(ConsoleViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        AvaloniaXamlLoader.Load(this);

        // macOS：窗口背景透明，毛玻璃由系统视图垫在下面；标题栏变透明、不占版面，红绿灯浮在侧栏上。
        // 别的平台保持 XAML 里的不透明深色。
        if (OperatingSystem.IsMacOS())
        {
            Classes.Add("glass");
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            TransparencyBackgroundFallback = Brushes.Transparent;
            TitleBarFrameBackground = Brushes.Transparent;
        }
        Opened += (_, _) =>
        {
            if (MacWindow.TryAttach(this) is not { } mac) return;
            mac.MakeTransparent();
            mac.InstallGlassBackdrop();

            // 系统按钮由 AtomUI 按标题栏高度垂直居中；撑到 52，按钮中心落在 y=26，
            // 和侧栏圆角、顶部留白对得上（XAML 里 TitleBarPanel 的负边距要和这个数一致）。
            if (this.GetVisualDescendants().OfType<AtomUI.Desktop.Controls.WindowTitleBar>().FirstOrDefault() is { } bar)
                bar.Height = 52;
        };

        InitMotion();

        // 关窗只是收起，宠物还在跑；应用退出时才真的关。
        Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => _vm?.Dispose();

        // 提供商对话框开着时，Esc 先关对话框。
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _vm?.Hub is not { IsOpen: true } hub) return;
            hub.Close();
            e.Handled = true;
        };
    }

    // ───── 动画与预热 ─────

    private void InitMotion()
    {
        _navPill = this.FindControl<Border>("NavPill");
        _pageHeader = this.FindControl<StackPanel>("PageHeader");
        _pageHost = this.FindControl<Grid>("PageHost");
        if (_vm is null) return;
        _pillTop = _vm.NavHighlightMargin.Top;

        // 导航高亮胶囊：布局直接把它放到新位置，动画再让它从旧位置滑过来。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ConsoleViewModel.NavHighlightMargin)) return;
            var top = _vm.NavHighlightMargin.Top;
            var dy = _pillTop - top;
            _pillTop = top;
            if (dy != 0 && CanAnimate && _navPill is not null)
                Motion.Slide(_navPill, 0, dy, TimeSpan.FromMilliseconds(340));
        };

        // 换页：页头和各块依次从下方淡入。
        if (_pageHost is null) return;
        foreach (var page in _pageHost.Children.OfType<ScrollViewer>())
            page.PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty || !page.IsVisible || !CanAnimate) return;
                // 等这一轮布局排完再放，Bounds 和位置才是对的。
                Dispatcher.UIThread.Post(() => EnterPage(page), DispatcherPriority.Render);
            };
    }

    /// <summary>窗口可见且不在预热时才放动画。</summary>
    private bool CanAnimate => IsVisible && !_warming;

    private void EnterPage(ScrollViewer page)
    {
        if (_pageHeader is not null)
            Motion.Enter(_pageHeader, 0, 10, TimeSpan.FromMilliseconds(360));

        var blocks = page.Content is Panel panel ? panel.Children.Cast<Visual>().ToList()
            : page.Content is Visual single ? [single] : [];
        for (var i = 0; i < blocks.Count; i++)
            // 只错开前 8 块，后面的在屏幕外，不用排队。
            Motion.Enter(blocks[i], 0, 16, TimeSpan.FromMilliseconds(420), TimeSpan.FromMilliseconds(Math.Min(i, 8) * 45 + 40));
    }

    /// <summary>每次从隐藏变为可见：整个界面淡入并从 97% 放大到原样，再把当前页的各块放进来。</summary>
    private void PlayEntrance()
    {
        if (Content is Visual root)
            Motion.Enter(root, 0, 0, TimeSpan.FromMilliseconds(300), scaleFrom: 0.97);
        var page = _pageHost?.Children.OfType<ScrollViewer>().FirstOrDefault(p => p.IsVisible);
        if (page is not null) EnterPage(page);
    }

    /// <summary>
    /// 打开控制台。预热还没结束时先记下来，预热完再开。
    /// </summary>
    public void Present()
    {
        if (_warming) { _revealRequested = true; return; }
        var wasHidden = !IsVisible;
        Show();
        Activate();
        if (wasHidden) PlayEntrance();
    }

    /// <summary>
    /// 后台预热。控制台第一次打开要 1 秒多（XAML 加载加窗口创建），之后每一页第一次进入还要 50 到 160ms
    /// （控件模板、JIT），都在 UI 线程上，宠物也会跟着停。启动后趁空把这些一次做完：
    /// 窗口全透明、不接鼠标地开一遍，每页过一次布局，再藏起来。真正打开时就只剩一个淡入。
    /// 每页之间让出 UI 线程，期间宠物和输入照常响应。
    /// </summary>
    public void Warmup()
    {
        if (IsVisible || _warming || _vm is null) return;
        _warming = true;
        var home = _vm.CurrentKey;
        var keys = _vm.Pages.Select(p => p.Key).ToArray();

        ShowActivated = false;
        Opacity = 0;
        Show();
        var mac = MacWindow.TryAttach(this);
        mac?.SetAlpha(0);
        if (mac is not null) mac.IgnoresMouse = true;

        var i = 0;
        void Step()
        {
            if (i < keys.Length)
            {
                _vm.Navigate(keys[i++]);
                UpdateLayout();
                Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
                return;
            }
            _vm.Navigate(home);
            UpdateLayout();
            Hide();
            Opacity = 1;
            ShowActivated = true;
            if (mac is not null) { mac.IgnoresMouse = false; mac.SetAlpha(1); }
            _warming = false;
            PetLog.Write("控制台预热完成");
            if (_revealRequested) { _revealRequested = false; Present(); }
        }
        Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
    }

    private static T? TagOf<T>(object? sender) where T : class => (sender as Control)?.Tag as T;

    private void OnToggleSidebar(object? sender, RoutedEventArgs e) => _vm.SidebarExpanded = !_vm.SidebarExpanded;
    private void OnNavigate(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } key) _vm.Navigate(key); }
    private void OnExit(object? sender, RoutedEventArgs e) => _vm.Exit();

    // 常规
    private void OnOpenSettingsFolder(object? sender, RoutedEventArgs e) => _vm.OpenSettingsFolder();
    private async void OnCheckUpdate(object? sender, RoutedEventArgs e) => await _vm.Update.CheckAsync();
    private void OnDownloadUpdate(object? sender, RoutedEventArgs e) => _vm.Update.OpenDownload();
    private void OnOpenLicenses(object? sender, RoutedEventArgs e) => _vm.OpenLicensesFolder();

    // 桌宠
    private void OnResetPosition(object? sender, RoutedEventArgs e) => _vm.ResetPosition();
    private void OnOpenPetsFolder(object? sender, RoutedEventArgs e) => _vm.OpenPetsFolder();
    private void OnRefreshPets(object? sender, RoutedEventArgs e) => _vm.RefreshPets();

    // 互动
    private void OnSayLine(object? sender, RoutedEventArgs e) => _vm.SayRandomLine();
    private void OnPlayAction(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } c) _vm.PlayAction(c); }

    // 菜单
    private void OnMenuUp(object? sender, RoutedEventArgs e) { if (TagOf<MenuEntry>(sender) is { } m) _vm.MoveMenuItem(m, -1); }
    private void OnMenuDown(object? sender, RoutedEventArgs e) { if (TagOf<MenuEntry>(sender) is { } m) _vm.MoveMenuItem(m, 1); }
    private void OnResetMenu(object? sender, RoutedEventArgs e) => _vm.ResetMenu();

    // 桌面组件
    private void OnTestBubble(object? sender, RoutedEventArgs e) => _vm.TestBubble();
    private void OnTogglePomodoro(object? sender, RoutedEventArgs e) => _vm.TogglePomodoro();

    // 智能
    private void OnOpenChat(object? sender, RoutedEventArgs e) => _vm.OpenChat();
    private void OnResetPrompt(object? sender, RoutedEventArgs e) => _vm.ResetSystemPrompt();
    private void OnPreviewPrompt(object? sender, RoutedEventArgs e) => _vm.RefreshPromptPreview();
    private void OnAddMemory(object? sender, RoutedEventArgs e) => _vm.AddMemory();
    private void OnMemoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.AddMemory();
    }
    private void OnRemoveMemory(object? sender, RoutedEventArgs e) { if (TagOf<MemoryItem>(sender) is { } m) _vm.RemoveMemory(m); }
    private void OnClearMemories(object? sender, RoutedEventArgs e) => _vm.ClearMemories();

    // 语音
    private void OnTestSpeech(object? sender, RoutedEventArgs e) => _vm.TestSpeech();
    private void OnStopSpeech(object? sender, RoutedEventArgs e) => _vm.StopSpeech();
}
