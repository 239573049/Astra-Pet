using TKCode.Arc.Primitives;
using AstraPet.Adapters;
using AstraPet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using Button = Avalonia.Controls.Button;
using MenuEntry = AstraPet.Core.MenuEntry;
using MemoryItem = AstraPet.Core.MemoryItem;

namespace AstraPet.ControlPanel;

public static class ConsoleConverters
{
    /// <summary>测试结果的颜色：成功用 Success 令牌，其余（进行中、失败）用 Danger，跟着明暗主题走。</summary>
    public static readonly IValueConverter OkBrush = new FuncValueConverter<bool, IBrush?>(ok =>
        Application.Current is { } app && app.TryGetResource(ok ? "ArcDesign.Success" : "ArcDesign.Danger", app.ActualThemeVariant, out var r)
            ? r as IBrush
            : Brush.Parse(ok ? "#2E9E6E" : "#D05050"));
}

public partial class ConsoleWindow : Window
{
    private const double FrameRadius = 12;
    private const double PageMaxWidth = 780;

    private readonly ConsoleViewModel _vm;

    // 动画和预热
    private Border? _frame;
    private Panel? _resizeGrips;
    private StackPanel? _pageHeader;
    private Grid? _pageHost;
    private Image? _preview;
    private bool _warming;
    private bool _revealRequested;
    private bool _releasing;

    // 手动改大小（macOS 上 BeginResizeDrag 对无边框窗口不起作用）
    private WindowEdge _resizeEdge;
    private PixelPoint _resizeStart;
    private PixelPoint _resizeOrigin;
    private Size _resizeSize;
    private bool _resizing;

    public ConsoleWindow() : this(null!) { }   // 设计器和冒烟测试用

    public ConsoleWindow(ConsoleViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        AvaloniaXamlLoader.Load(this);

        _frame = this.FindControl<Border>("Frame");
        _resizeGrips = this.FindControl<Panel>("ResizeGrips");
        InitMotion();
        InitPreview();

        if (this.FindControl<ReorderableList>("MenuList") is { } menuList)
            menuList.ItemTitleSelector = o => (o as MenuEntry)?.Label ?? "";

        // 最大化时去掉圆角和描边，也不再需要改大小的拖拽条。
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
            _frame?.Classes.Set("maximized", maximized);
            if (_resizeGrips is not null) _resizeGrips.IsVisible = !maximized;
        };

        // 关窗只是收起，宠物还在跑；收起一段时间后由 App 调 Release 真的关掉，应用退出时也真的关。
        Closing += (_, e) =>
        {
            if (_releasing || e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            if (_vm is null) return;
            _vm.Pet.FrameShown -= OnPetFrame;
            _vm.Dispose();
        };

#if DEBUG
        // 开发用：ASTRA_PET_SNAPSHOT=目录 时，打开后把每一页各截一张 PNG（不依赖系统截屏权限），检查明暗两套配色。
        // ASTRA_PET_IMPORT=视频 时，打开后在「动作」页走一遍导入：处理、截图（有 ASTRA_PET_SNAPSHOT 时）、加到宠物上。
        if (Environment.GetEnvironmentVariable("ASTRA_PET_IMPORT") is { Length: > 0 } video && _vm is not null)
            Opened += (_, _) => DispatcherTimer.RunOnce(() => _ = ImportForTestAsync(video), TimeSpan.FromSeconds(2));
        else if (Environment.GetEnvironmentVariable("ASTRA_PET_SNAPSHOT") is { Length: > 0 } dir && _vm is not null)
            Opened += (_, _) => DispatcherTimer.RunOnce(() => _ = SnapshotAsync(dir), TimeSpan.FromSeconds(2));
#endif
    }

#if DEBUG
    private async Task ImportForTestAsync(string video)
    {
        _vm.Navigate("Actions");
        var import = _vm.Actions.Import;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await import.OpenAsync(video);
        await import.StartAsync();
        PetLog.Write($"导入测试：处理用了 {watch.Elapsed.TotalSeconds:0.0} 秒，{import.ReportText}；{string.Join("；", import.Warnings)}{import.Error}");
        if (Environment.GetEnvironmentVariable("ASTRA_PET_SNAPSHOT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            // 窗口拉高到装得下预览和质检结果，再滚到导入区。
            Height = 1500;
            await Task.Delay(500);
            if (this.FindControl<Control>("ImportSection") is { } section
                && section.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { Content: Visual content } scroller
                && section.TranslatePoint(new Point(0, 0), content) is { } at)
                scroller.Offset = new Vector(0, at.Y);
            await Task.Delay(900);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(Path.Combine(dir, "import.png"));
        }
        if (import.IsDone)
        {
            import.Add();
            PetLog.Write($"导入测试：已加到宠物上，现在播放 {_vm.Actions.Selected?.Id}，总共 {watch.Elapsed.TotalSeconds:0.0} 秒");
        }
    }

    private async Task SnapshotAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var appearance in new[] { "dark", "light" })
        {
            _vm.Settings.Appearance = appearance;
            foreach (var key in _vm.Pages.Select(p => p.Key))
            {
                _vm.Navigate(key);
                await Task.Delay(900);
                // 2 倍时 RenderTargetBitmap 画列表模板里的文字会错位（只是截图的问题，屏幕上正常），默认用 1 倍。
                var scale = Environment.GetEnvironmentVariable("ASTRA_PET_SNAPSHOT_SCALE") == "2" ? 2 : 1;
                var size = new PixelSize((int)Bounds.Width * scale, (int)Bounds.Height * scale);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                bitmap.Render(this);
                bitmap.Save(Path.Combine(dir, $"{appearance}-{key}.png"));
            }
        }
        PetLog.Write($"截图已保存到 {dir}");
    }
#endif

    // ───── 窗口外框 ─────

    /// <summary>侧栏顶部和页头：按住拖动窗口，双击最大化 / 还原。</summary>
    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!Enum.TryParse<WindowEdge>(grip.Tag as string, out var edge)) return;
        e.Handled = true;
        if (!OperatingSystem.IsMacOS())
        {
            BeginResizeDrag(edge, e);
            return;
        }
        _resizing = true;
        _resizeEdge = edge;
        _resizeStart = this.PointToScreen(e.GetPosition(this));
        _resizeOrigin = Position;
        _resizeSize = ClientSize;
        e.Pointer.Capture(grip);
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var now = this.PointToScreen(e.GetPosition(this));
        var scale = DesktopScaling;
        var dx = (now.X - _resizeStart.X) / scale;
        var dy = (now.Y - _resizeStart.Y) / scale;
        double w = _resizeSize.Width, h = _resizeSize.Height;
        int x = _resizeOrigin.X, y = _resizeOrigin.Y;

        if (_resizeEdge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast)
            w = Math.Max(MinWidth, _resizeSize.Width + dx);
        if (_resizeEdge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
        {
            w = Math.Max(MinWidth, _resizeSize.Width - dx);
            x = _resizeOrigin.X + (int)Math.Round((_resizeSize.Width - w) * scale);
        }
        if (_resizeEdge is WindowEdge.South or WindowEdge.SouthEast or WindowEdge.SouthWest)
            h = Math.Max(MinHeight, _resizeSize.Height + dy);
        if (_resizeEdge is WindowEdge.North or WindowEdge.NorthEast or WindowEdge.NorthWest)
        {
            h = Math.Max(MinHeight, _resizeSize.Height - dy);
            y = _resizeOrigin.Y + (int)Math.Round((_resizeSize.Height - h) * scale);
        }

        Width = w;
        Height = h;
        if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
    }

    // ───── 实时预览 ─────

    /// <summary>概览页左上角的宠物画面：直接用宠物窗口的那张位图，宠物每画一帧这里跟着刷新（只在看得见时）。</summary>
    private void InitPreview()
    {
        _preview = this.FindControl<Image>("PetPreview");
        if (_vm is null || _preview is null) return;
        _preview.Source = _vm.Pet.Frame;
        _vm.Pet.FrameShown += OnPetFrame;
    }

    private void OnPetFrame()
    {
        if (_preview is null || !IsVisible || _warming || !_vm.IsOverview) return;
        _preview.RenderTransform = _vm.Pet.Mirrored ? MirrorTransform : null;
        _preview.InvalidateVisual();
    }

    private static readonly ScaleTransform MirrorTransform = new(-1, 1);

    // ───── 明暗切换 ─────

    /// <summary>侧栏底部的明暗按钮：把选择存进设置（App 监听设置去套主题），带 Arc 的切换过渡。</summary>
    private async void OnThemeChangeRequested(object? sender, ThemeSwitcherChangeRequestedEventArgs e)
    {
        e.Handled = true;
        if (_vm is null) return;
        var next = e.Next == ThemeSwitcherAppearance.Dark ? "dark" : "light";
        await ThemeSwitcher.RunTransitionAsync(e.Trigger, e.Variant, () =>
        {
            _vm.Settings.Appearance = next;
            return Task.CompletedTask;
        });
    }

    private void OnPickAccent(object? sender, RoutedEventArgs e)
    {
        if (TagOf<AccentOption>(sender) is { } option) _vm.SelectAccent(option);
    }

    // ───── 动画与预热 ─────

    private void InitMotion()
    {
        _pageHeader = this.FindControl<StackPanel>("PageHeader");
        _pageHost = this.FindControl<Grid>("PageHost");
        if (_vm is null) return;
        InitNav();

        if (_pageHost is null) return;

        // 各页统一宽度：占满可用宽度，最宽 780，靠左和页头对齐（只设 MaxWidth 的话宽度会跟着内容走，每页不一样宽）。
        _pageHost.SizeChanged += (_, e) =>
        {
            var width = Math.Max(0, Math.Min(PageMaxWidth, e.NewSize.Width - 64 - 12));
            foreach (var page in _pageHost.Children.OfType<ScrollViewer>())
                if (page.Content is StackPanel panel && panel.Classes.Contains("page"))
                    panel.Width = width;
        };

        // 换页：页头和各块依次从下方淡入。
        foreach (var page in _pageHost.Children.OfType<ScrollViewer>())
            page.PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty || !page.IsVisible || !CanAnimate) return;
                // 等这一轮布局排完再放，Bounds 和位置才是对的。
                Dispatcher.UIThread.Post(() => EnterPage(page), DispatcherPriority.Render);
            };
    }

    /// <summary>
    /// 左侧导航：Arc 的 Sidebar 没有 ItemsSource，按视图模型里的分组把各页建成 SidebarSection / SidebarItem。
    /// 点一页就换页；页面从别处切换（概览里的「换个角色」、ASTRA_PET_PAGE）时把选中项跟过去。
    /// </summary>
    private void InitNav()
    {
        if (this.FindControl<Sidebar>("Nav") is not { } nav) return;
        foreach (var group in _vm.NavGroups)
        {
            var section = new SidebarSection { Header = group.HasTitle ? group.Title : null };
            foreach (var page in group.Items)
                section.Items.Add(new SidebarItem { Key = page.Key, Label = page.Label, Icon = page.Icon });
            nav.Sections.Add(section);
        }
        nav.SelectedKey = _vm.CurrentKey;
        nav.ItemInvoked += (_, e) => { if (e.Item.Key is { } key) _vm.Navigate(key); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConsoleViewModel.CurrentKey)) nav.SelectedKey = _vm.CurrentKey;
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

    /// <summary>预热完了，而且用户没在等着打开（App 借这个时机把窗口释放掉）。</summary>
    public event Action? WarmedUp;

    /// <summary>收起着、没在预热、也没有跑到一半的活，可以释放。</summary>
    public bool CanRelease => !IsVisible && !_warming && _vm is not null && !_vm.Busy;

    /// <summary>
    /// 真的关掉窗口，连同整棵界面树、渲染表面和 ViewModel 一起释放（约 150MB）。
    /// 控制台收起后常驻太占内存；JIT 和模板在第一次创建时已经热了，再建一个只要一百多毫秒。
    /// </summary>
    public void Release()
    {
        _releasing = true;
        Close();
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
        OnPetFrame();
    }

    /// <summary>
    /// 后台预热。控制台第一次打开要 1 秒多（XAML 加载加窗口创建），之后每一页第一次进入还要 50 到 160ms
    /// （控件模板、JIT），都在 UI 线程上，宠物也会跟着停。启动后趁空把这些一次做完：
    /// 窗口全透明、不接鼠标地开一遍，每页过一次布局。预热完如果用户没要打开，App 会把窗口释放掉（<see cref="WarmedUp"/>）：
    /// 留着要多占约 150MB，而 JIT 已经热了，之后再建窗口只要一百多毫秒（冷启动约 670ms）。
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
        // Opacity 只管 Avalonia 自己画的内容，原生窗口层要单独藏（macOS 上会透出一块窗口背景）。
        var native = this.AttachNative(_vm.Platform);
        native.SetAlpha(0);
        native.IgnoresMouse = true;

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
            native.IgnoresMouse = false;
            native.SetAlpha(1);
            _warming = false;
            PetLog.Write("控制台预热完成");
            if (_revealRequested) { _revealRequested = false; Present(); }
            else WarmedUp?.Invoke();
        }
        Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
    }

    private static T? TagOf<T>(object? sender) where T : class => (sender as Control)?.Tag as T;

    private void OnNavigate(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } key) _vm.Navigate(key); }
    private void OnOpenGitHub(object? sender, RoutedEventArgs e) => _vm.OpenRepository();
    private void OnExit(object? sender, RoutedEventArgs e) => _vm.Exit();

    // 常规
    private void OnOpenSettingsFolder(object? sender, RoutedEventArgs e) => _vm.OpenSettingsFolder();
    private void OnOpenLogFolder(object? sender, RoutedEventArgs e) => _vm.OpenLogFolder();
    private async void OnCheckUpdate(object? sender, RoutedEventArgs e) => await _vm.Update.CheckAsync();
    private async void OnDownloadUpdate(object? sender, RoutedEventArgs e) => await _vm.Update.ApplyAsync();
    private void OnOpenLicenses(object? sender, RoutedEventArgs e) => _vm.OpenLicensesFolder();

    // 角色
    private void OnResetPosition(object? sender, RoutedEventArgs e) => _vm.ResetPosition();
    private void OnOpenPetsFolder(object? sender, RoutedEventArgs e) => _vm.OpenPetsFolder();
    private void OnOpenPetLicense(object? sender, RoutedEventArgs e) => _vm.OpenPetLicense();
    private void OnRefreshPets(object? sender, RoutedEventArgs e) => _vm.RefreshPets();
    private void OnOpenStudio(object? sender, RoutedEventArgs e) => _vm.OpenStudio();

    // 互动
    private void OnSayLine(object? sender, RoutedEventArgs e) => _vm.SayRandomLine();

    // 状态 / 位置页（design/pet-life）
    private void OnFeed(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } id) _vm.Life.Feed(id); }
    private void OnResetTraits(object? sender, RoutedEventArgs e) => _vm.Life.ResetTraits();
    private void OnResetLife(object? sender, RoutedEventArgs e) => _vm.Life.ResetLife();
    private void OnGoClimb(object? sender, RoutedEventArgs e) => _vm.Life.GoClimb();
    private void OnDrop(object? sender, RoutedEventArgs e) => _vm.Life.Drop();
    private void OnToss(object? sender, RoutedEventArgs e) => _vm.Life.Toss();
    private void OnStartPlay(object? sender, RoutedEventArgs e) => _vm.Life.StartPlay();
    private void OnSleep(object? sender, RoutedEventArgs e) => _vm.Life.Sleep();
    private void OnSummon(object? sender, RoutedEventArgs e) => _vm.Life.Summon();
    private void OnPlayAction(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } c) _vm.PlayAction(c); }

    // 动作页
    private void OnPlayActionItem(object? sender, RoutedEventArgs e) { if (TagOf<ActionItem>(sender) is { } a) _vm.Actions.Play(a); }
    private void OnSelectActionItem(object? sender, RoutedEventArgs e) { if (TagOf<ActionItem>(sender) is { } a) _vm.Actions.Selected = a; }
    private void OnSayActionLine(object? sender, RoutedEventArgs e) { if (TagOf<ActionItem>(sender) is { } a) _vm.Actions.Say(a); }
    private void OnBindAction(object? sender, RoutedEventArgs e) => _vm.Actions.BindSelected();
    private void OnUnbindAction(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } id) _vm.Actions.Unbind(id); }
    private void OnResetAction(object? sender, RoutedEventArgs e) => _vm.Actions.ResetSelected();
    private void OnDeleteAction(object? sender, RoutedEventArgs e) => _vm.Actions.DeleteSelected();

    // 导入视频
    private async void OnImportPick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择动作视频",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("视频") { Patterns = ["*.mp4", "*.mov", "*.m4v", "*.webm", "*.mkv"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await _vm.Actions.Import.OpenAsync(path);
    }

    private async void OnImportStart(object? sender, RoutedEventArgs e) => await _vm.Actions.Import.StartAsync();
    private void OnImportCancel(object? sender, RoutedEventArgs e) => _vm.Actions.Import.Cancel();
    private void OnImportDiscard(object? sender, RoutedEventArgs e) => _vm.Actions.Import.Discard();
    private void OnImportAdd(object? sender, RoutedEventArgs e) => _vm.Actions.Import.Add();

    // 右键菜单
    private void OnResetMenu(object? sender, RoutedEventArgs e) => _vm.ResetMenu();

    // 提醒与专注
    private void OnTestBubble(object? sender, RoutedEventArgs e) => _vm.TestBubble();
    private void OnTogglePomodoro(object? sender, RoutedEventArgs e) => _vm.TogglePomodoro();

    // 模型与记忆
    private void OnOpenChat(object? sender, RoutedEventArgs e) => _vm.OpenChat();
    private void OnCopyPackPersona(object? sender, RoutedEventArgs e) => _vm.CopyPackPersona();
    private void OnDismissPromptNotice(object? sender, RoutedEventArgs e) => _vm.DismissPromptMigrationNotice();
    private void OnPreviewPrompt(object? sender, RoutedEventArgs e) => _vm.RefreshPromptPreview();
    private void OnCopyExternalToken(object? sender, RoutedEventArgs e) => _ = ArcClipboard.CopyTextAsync(this, _vm.Settings.ExternalToken);
    private void OnRegenerateExternalToken(object? sender, RoutedEventArgs e) => _vm.RegenerateExternalToken();
    private void OnCopyMcpCommand(object? sender, RoutedEventArgs e) => _ = ArcClipboard.CopyTextAsync(this, _vm.McpCommandText);
    private void OnCopyCurlExample(object? sender, RoutedEventArgs e) => _ = ArcClipboard.CopyTextAsync(this, _vm.CurlExample);
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
