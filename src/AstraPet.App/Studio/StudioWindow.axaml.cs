using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TKCode.Arc;

namespace AstraPet.Studio;

/// <summary>工作台窗口：按钮转给 ViewModel，文件选择框在这里开。关窗口只是隐藏，后台的视频任务照常跑。</summary>
public partial class StudioWindow : Window
{
    private readonly StudioViewModel _vm;
    private bool _quitting;
    private Action? _unhookSteps;
    private readonly WindowChrome? _chrome;

    public StudioWindow() : this(null!) { }   // 设计器用

    public StudioWindow(StudioViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _chrome = new WindowChrome(this, Frame, ResizeGrips);
        TrafficLights.IsVisible = WindowChrome.IsMac;
        WindowButtons.IsVisible = !WindowChrome.IsMac;
        if (vm is null) return;
        StepNav.ItemInvoked += (_, e) =>
        {
            if (e.Item.Tag is StepItem step && P is { } p && p.Step != step)
            {
                p.Select(step);
                StepScroll.Offset = default;
            }
        };
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StudioViewModel.Current)) BuildSteps(); };
        BuildSteps();
    }

    /// <summary>
    /// 左侧步骤栏：Arc 的 Sidebar 没有 ItemsSource，按当前工程的九步建条目。图标是步骤编号，
    /// 状态（完成 / 需更新 / 进行中）放在条目末尾，没解锁的禁用；状态和选中项变了跟着改。
    /// </summary>
    private void BuildSteps()
    {
        _unhookSteps?.Invoke();
        _unhookSteps = null;
        StepNav.Sections.Clear();
        if (P is not { } p) return;

        var section = new SidebarSection();
        var hooks = new List<(StepItem, PropertyChangedEventHandler)>();
        foreach (var step in p.Steps)
        {
            var item = new SidebarItem
            {
                Key = step.Id.ToString(), Label = step.Title, Tag = step,
                Icon = new TextBlock { Text = ((int)step.Id).ToString(), FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                       VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            };
            void Sync()
            {
                item.Badge = step.State is StepState.Done or StepState.Stale or StepState.Running ? step.StateText : null;
                item.IsEnabled = !step.IsLocked;
            }
            Sync();
            PropertyChangedEventHandler handler = (_, e) => { if (e.PropertyName == nameof(StepItem.State)) Sync(); };
            step.PropertyChanged += handler;
            hooks.Add((step, handler));
            section.Items.Add(item);
        }
        StepNav.Sections.Add(section);
        SyncCustomRoles(p);
        StepNav.SelectedKey = p.Step.Id.ToString();

        // 选项不在可视树里，绑定拿不到工程；Fast / Mini 出不了 1080p，这一档在这里禁用。
        Sync1080(p);
        PropertyChangedEventHandler onStep = (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectViewModel.Step)) StepNav.SelectedKey = p.Step.Id.ToString();
            if (e.PropertyName == nameof(ProjectViewModel.No1080)) Sync1080(p);
        };
        p.PropertyChanged += onStep;
        _unhookSteps = () =>
        {
            foreach (var (step, handler) in hooks) step.PropertyChanged -= handler;
            p.PropertyChanged -= onStep;
        };
    }

    // ───── 窗口外框 ─────

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => _chrome?.DragAreaPressed(e);
    private void OnMinimize(object? sender, RoutedEventArgs e) => _chrome?.Minimize();
    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => _chrome?.ToggleMaximize();
    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void Sync1080(ProjectViewModel p)
    {
        foreach (var o in ResolutionPicker.Items.OfType<SegmentedOption>())
            if (Equals(o.Value, "1080p")) o.IsDisabled = p.No1080;
    }

    /// <summary>程序退出时调，真正关掉窗口。</summary>
    public void Shutdown()
    {
        _quitting = true;
        Close();
    }

    public void Present()
    {
        if (_vm.IsHome) _vm.RefreshProjects();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _vm.Current?.SaveNow();
        if (!_quitting)
        {
            e.Cancel = true;
            _vm.AnswerCost(false);
            Hide();
        }
        base.OnClosing(e);
    }

    private static T? TagOf<T>(object? sender) where T : class => (sender as Control)?.Tag as T;
    private ProjectViewModel? P => _vm.Current;

    private async Task<IReadOnlyList<string>> PickImagesAsync(string title, bool multiple)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    // ───── 首页 ─────

    private void OnCreateProject(object? sender, RoutedEventArgs e) => _vm.CreateProject();

    private void OnNewNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.CreateProject();
    }

    private void OnOpenProject(object? sender, RoutedEventArgs e) { if (TagOf<ProjectCard>(sender) is { } c) _vm.Open(c.Project); }
    private void OnDeleteProject(object? sender, RoutedEventArgs e) { if (TagOf<ProjectCard>(sender) is { } c) _vm.AskDelete(c); }
    private void OnDeleteCancel(object? sender, RoutedEventArgs e) => _vm.CancelDelete();
    private void OnDeleteConfirm(object? sender, RoutedEventArgs e) => _vm.ConfirmDelete();
    private void OnProjectCardDoubleTapped(object? sender, TappedEventArgs e) { if (TagOf<ProjectCard>(sender) is { } c) _vm.Open(c.Project); }
    private void OnOpenRoot(object? sender, RoutedEventArgs e) => _vm.OpenRootFolder();
    private void OnBackHome(object? sender, RoutedEventArgs e) => _vm.BackHome();

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        P?.Next();
        StepScroll.Offset = default;
    }

    // ───── ⓪ ─────

    private async void OnTestKey(object? sender, RoutedEventArgs e) => await _vm.TestAsync();
    private void OnUseChatKey(object? sender, RoutedEventArgs e) => _vm.UseChatKey();
    private void OnOpenKeyPage(object? sender, RoutedEventArgs e) => _vm.OpenKeyPage();

    // ───── ① ─────

    /// <summary>拖进来、点选或粘贴的参考图：复制进工程后清空拖放区的列表（下面的缩略图才是工程里的参考图）。</summary>
    private async void OnReferencesDropped(object? sender, FileDropzoneFilesChangedEventArgs e)
    {
        if (P is not { } p || sender is not FileDropzone zone) return;
        var paths = e.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        zone.Items.Clear();
        await p.AddReferencesAsync(paths);
    }

    private async void OnImportPrompt(object? sender, RoutedEventArgs e)
    {
        if (P is not { } p) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择提示词文件（.md）",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await p.ImportPromptAsync(path);
    }

    private void OnClearPrompt(object? sender, RoutedEventArgs e) => P?.ClearPrompt();

    private void OnRemoveReference(object? sender, RoutedEventArgs e) { if (TagOf<ImageItem>(sender) is { } i) P?.RemoveReference(i); }

    // ───── ② ③ ─────

    private void OnFillSheetPrompt(object? sender, RoutedEventArgs e) => P?.FillSheetPrompt();
    private void OnFillFigurePrompt(object? sender, RoutedEventArgs e) => P?.FillFigurePrompt();
    private async void OnGenerateSheets(object? sender, RoutedEventArgs e) { if (P is { } p) await p.GenerateSheetsAsync(); }
    private async void OnGenerateFigures(object? sender, RoutedEventArgs e) { if (P is { } p) await p.GenerateFiguresAsync(); }
    private async void OnSelectSheet(object? sender, RoutedEventArgs e) { if (TagOf<ImageItem>(sender) is { } i && P is { } p) await p.SelectSheetAsync(i); }
    private async void OnSelectFigure(object? sender, RoutedEventArgs e) { if (TagOf<ImageItem>(sender) is { } i && P is { } p) await p.SelectFigureAsync(i); }

    private async void OnImportCandidates(object? sender, RoutedEventArgs e)
    {
        if (P is not { } p || TagOf<string>(sender) is not { } folder) return;
        var paths = await PickImagesAsync(folder == "sheet" ? "上传设定图" : "上传站姿", multiple: true);
        if (paths.Count > 0) await p.ImportCandidatesAsync(folder, paths);
    }

    // ───── ④ ─────

    private void OnAddTemplate(object? sender, RoutedEventArgs e) { if (TagOf<ActionTemplate>(sender) is { } t) P?.AddTemplate(t); }
    private void OnAddRequired(object? sender, RoutedEventArgs e) => P?.AddRequired();
    private void OnAddLifeGroup(object? sender, RoutedEventArgs e) { if (TagOf<string>(sender) is { } g) P?.AddLifeGroup(g); }
    private async void OnGenerateKeyframe(object? sender, RoutedEventArgs e) { if (TagOf<KeyframeRow>(sender) is { } r && P is { } p) await p.GenerateKeyframesAsync(r); }

    private async void OnSelectKeyframe(object? sender, RoutedEventArgs e)
    {
        if (TagOf<ImageItem>(sender) is not { } item || P is not { } p) return;
        if (p.KeyframeRows.FirstOrDefault(r => r.Candidates.Contains(item)) is { } row) await p.SelectKeyframeAsync(row, item);
    }

    private async void OnImportKeyframe(object? sender, RoutedEventArgs e)
    {
        if (TagOf<KeyframeRow>(sender) is not { } row || P is not { } p) return;
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"选{row.Name}关键帧图片", AllowMultiple = true,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(x => x is not null).Select(x => x!).ToList();
        if (paths.Count > 0) await p.ImportKeyframeAsync(row, paths);
    }
    private void OnAddBlank(object? sender, RoutedEventArgs e) => P?.AddBlank();

    private void OnAddCustom(object? sender, RoutedEventArgs e) => P?.AddCustom();

    /// <summary>自定义动作的触发时机标签：按 ViewModel 里的默认（被点击）选上。</summary>
    private void SyncCustomRoles(ProjectViewModel p)
    {
        if (CustomRoleChips.SelectedItems is not { } selected) return;
        selected.Clear();
        foreach (var c in ProjectViewModel.RoleChoices.Where(c => p.CustomRoles.Contains(c.Id))) selected.Add(c);
    }

    private void OnCustomRolesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (P is { } p && sender is ChipGroup chips)
            p.CustomRoles = (chips.SelectedItems?.OfType<Choice>() ?? []).Select(c => c.Id).ToList();
    }
    private void OnRemovePlanItem(object? sender, RoutedEventArgs e) { if (TagOf<PlanItem>(sender) is { } i) P?.RemovePlanItem(i); }
    private void OnResetPlanPrompt(object? sender, RoutedEventArgs e) => TagOf<PlanItem>(sender)?.ResetPrompt();
    private void OnResetPrefix(object? sender, RoutedEventArgs e) => P?.ResetPrefix();
    private void OnConfirmPlan(object? sender, RoutedEventArgs e) => P?.ConfirmPlan();

    // ───── ⑤ ⑥ ─────

    private async void OnGenerateIdle(object? sender, RoutedEventArgs e) { if (P is { } p) await p.GenerateIdleAsync(); }
    private async void OnGenerateMissing(object? sender, RoutedEventArgs e) { if (P is { } p) await p.GenerateMissingAsync(); }
    private async void OnGenerateRow(object? sender, RoutedEventArgs e) { if (TagOf<ActionRow>(sender) is { } r && P is { } p) await p.GenerateOneAsync(r); }
    private async void OnCancelTake(object? sender, RoutedEventArgs e) { if (TagOf<TakeItem>(sender) is { } t && P is { } p) await p.CancelTakeAsync(t); }
    private void OnAdopt(object? sender, RoutedEventArgs e) { if (TagOf<TakeItem>(sender) is { } t) P?.Adopt(t); }
    private async void OnReprocess(object? sender, RoutedEventArgs e) { if (TagOf<TakeItem>(sender) is { } t && P is { } p) await p.ReprocessAsync(t); }
    private void OnOpenRaw(object? sender, RoutedEventArgs e) { if (TagOf<ActionRow>(sender) is { } r) P?.OpenRawFolder(r); }

    // ───── ⑦ ⑧ ─────

    private async void OnTryout(object? sender, RoutedEventArgs e) { if (P is { } p) await p.TryoutAsync(); }
    private async void OnEndTryout(object? sender, RoutedEventArgs e) { if (P is { } p) await p.EndTryoutAsync(); }
    private void OnPlayRow(object? sender, RoutedEventArgs e) { if (TagOf<ActionRow>(sender) is { } r) P?.Play(r); }
    private async void OnInstall(object? sender, RoutedEventArgs e) { if (P is { } p) await p.InstallAsync(); }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (P is not { } p) return;
        p.SaveNow();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出角色包",
            SuggestedFileName = p.SuggestedFileName,
            DefaultExtension = "astrapet",
            FileTypeChoices = [new FilePickerFileType("Astra Pet 角色包") { Patterns = ["*.astrapet"] }],
        });
        if (file?.TryGetLocalPath() is { } path) await p.ExportAsync(path);
    }

#if DEBUG
    /// <summary>开发用：等 ⑥ 处理完，把首页、每一步和费用确认框截图（深浅两种外观）。</summary>
    public async Task SnapshotAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        if (_vm.Current is not { } p) return;
        // 演示用的假 Key（只在 ASTRA_PET_DATA 指向的临时设置里）：让各步解锁，不会真的发请求。
        if (!_vm.HasKey) _vm.ApiKey = "demo-key";
        for (var i = 0; i < 600 && p.IsProcessingAny; i++) await Task.Delay(500);
        Height = 1400;
        await Task.Delay(800);
        foreach (var step in p.Steps)
        {
            p.Select(step);
            StepScroll.Offset = default;
            await Task.Delay(700);
            Save(Path.Combine(dir, $"step-{(int)step.Id}-{step.Id}.png"));
            if (step.Id == StepId.Plan)
            {
                p.CustomName = "吃甜甜圈";
                p.CustomDescription = "拿出一个甜甜圈，开心地咬了几口";
                StepScroll.ScrollToEnd();
                await Task.Delay(700);
                Save(Path.Combine(dir, "step-4-custom.png"));
                p.CustomName = p.CustomDescription = "";
            }
        }
        p.Select(p.Steps[(int)StepId.Tryout]);
        await p.TryoutAsync();
        AstraPet.Core.PetLog.Write("工作台截图：试用 → " + p.Message + "；播放 trot：" + _vm.Host.PlayActionById("trot"));
        await Task.Delay(1500);
        await p.EndTryoutAsync();
        AstraPet.Core.PetLog.Write("工作台截图：结束试用 → " + p.Message);
        p.Select(p.Steps[(int)StepId.Export]);
        await p.InstallAsync();
        AstraPet.Core.PetLog.Write("工作台截图：安装 → " + p.Message);
        Save(Path.Combine(dir, "installed.png"));
        p.Select(p.Steps[(int)StepId.Generate]);
        var pending = _vm.ConfirmAsync(StudioEngine.VideoEstimate(p.Project, p.Data.Actions, _vm.VideoProvider!), CancellationToken.None);
        await Task.Delay(700);
        Save(Path.Combine(dir, "cost.png"));
        _vm.AnswerCost(false);
        await pending;
        // 换成只能出 720p 的 Mini：④ 的 1080p 应该变灰，分辨率退到 720p。
        _vm.VideoModelId = "doubao-seedance-2-0-mini-260615";
        p.Select(p.Steps[(int)StepId.Bind]);
        StepScroll.Offset = default;
        await Task.Delay(700);
        Save(Path.Combine(dir, "step-0-mini.png"));
        p.Select(p.Steps[(int)StepId.Plan]);
        StepScroll.ScrollToEnd();
        await Task.Delay(700);
        Save(Path.Combine(dir, "step-4-mini.png"));
        AstraPet.Core.PetLog.Write($"工作台截图：换 Mini 后分辨率 {p.Resolution}");
        _vm.BackHome();
        await Task.Delay(700);
        Save(Path.Combine(dir, "home.png"));
        AstraPet.Core.PetLog.Write($"工作台截图已保存到 {dir}");
    }

    private void Save(string path)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height), new Avalonia.Vector(96, 96));
        bitmap.Render(this);
        bitmap.Save(path);
    }
#endif

    // ───── 费用确认 ─────

    private void OnCostConfirm(object? sender, RoutedEventArgs e) => _vm.AnswerCost(true);
    private void OnCostCancel(object? sender, RoutedEventArgs e) => _vm.AnswerCost(false);
}
