using AstraPet.Adapters;
using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace AstraPet.Pet;

/// <summary>输入卡片的状态：提示语、是否在等回复、发送按钮亮不亮。文字本身直接从输入框读，不走绑定。</summary>
public sealed class ChatInputModel : Observable
{
    private string _placeholder = "";
    private bool _busy;
    private bool _hasText;

    public string Placeholder { get => _placeholder; set => Set(ref _placeholder, value); }
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(SendOpacity)); } }
    public bool HasText { get => _hasText; set { if (Set(ref _hasText, value)) Raise(nameof(SendOpacity)); } }
    public double SendOpacity => Busy || HasText ? 1 : 0.35;
}

/// <summary>
/// 双击宠物（或菜单「聊天」）时在宠物旁边弹出的输入框，只有一个输入框：回车发给智能体，回复照常出现在头顶气泡里。
/// 发完不收起，可以接着聊；Esc、点别处收起。输入 /clear 清空上下文。上下文在智能体里，收起不丢。
/// </summary>
public partial class ChatInputWindow : Window
{
    /// <summary>输入框和身体之间留的空隙（DIP）。</summary>
    private const double Gap = 4;

    private readonly PetWindow _pet;
    private readonly PetCompanion _companion;
    private readonly PetSettings _settings;
    private readonly ChatInputModel _model = new();
    private INativeWindow _native = NullNativeWindow.Instance;
    private readonly TextBox _box;

    public ChatInputWindow() : this(null!, null!, null!, null!) { }   // 设计器和冒烟测试用

    public ChatInputWindow(PetWindow pet, PetCompanion companion, PetSettings settings, IPlatform platform)
    {
        _pet = pet;
        _companion = companion;
        _settings = settings;
        DataContext = _model;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        AvaloniaXamlLoader.Load(this);
        _box = this.FindControl<TextBox>("Box")!;
        // 隧道阶段先拿到回车，TextBox 自己还没处理。
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) _model.HasText = !string.IsNullOrWhiteSpace(_box.Text);
        };
        if (pet is null) return;

        Opened += (_, _) =>
        {
            _native = this.AttachNative(platform);
            _native.MakeTransparent();
            _native.SetCapturable(_settings.AllowCapture);
            Follow();
        };
        SizeChanged += (_, _) => Follow();
        _pet.PositionChanged += (_, _) => Follow();
        Activated += (_, _) => FocusInput();
        // 输入框开着就算在聊天：主动搭话、提醒、规则台词都不来打断。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) _companion.ChatOpen = IsVisible;
        };
        // 点到别处（包括宠物本身）就收起；正在等回复也收，回复照样冒在气泡里。
        Deactivated += (_, _) => Hide();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetSettings.AllowCapture)) _native.SetCapturable(_settings.AllowCapture);
            if (e.PropertyName == nameof(PetSettings.Visible) && !_settings.Visible) Hide();
        };
        Closing += (_, e) =>
        {
            // 关窗只是收起；应用退出时才真的关。
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>在宠物旁边弹出并拿到焦点。</summary>
    public void Present()
    {
        if (!_settings.Visible) return;
        UpdatePlaceholder();
        if (!IsVisible) Show();
        Follow();
        Activate();
        FocusInput();
    }

    private void FocusInput() => Dispatcher.UIThread.Post(() => _box.Focus(), DispatcherPriority.Input);

    private string PetName => _companion.Agent.Persona().Name is { Length: > 0 } n ? n : "它";

    private void UpdatePlaceholder() =>
        _model.Placeholder = _model.Busy ? $"{PetName}在想…" : $"和{PetName}说点什么…";

    /// <summary>输入法正在拼字（拼音还没选词）时的回车是给输入法的，不能当发送。</summary>
    private bool Composing() =>
        _box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.PreeditText is { Length: > 0 };

    private async void OnSendPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        await SendAsync();
    }

    private async void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }
        if (e.Key is not (Key.Enter or Key.Return) || Composing()) return;
        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        var text = (_box.Text ?? "").Trim();
        if (text.Length == 0 || _model.Busy) return;
        _box.Text = "";

        if (text is "/clear" or "/清空")
        {
            _companion.Agent.ClearHistory();
            _companion.Say("好，刚才聊的我先忘掉啦～", speak: false);
            return;
        }
        if (!_companion.Agent.IsConfigured)
        {
            _companion.Say("我还没有大脑呢…先去控制台「智能」页配一个模型吧", speak: false);
            return;
        }

        PetLog.Write($"对话：发送 {text.Length} 字");
        _model.Busy = true;
        UpdatePlaceholder();
        try
        {
            await _companion.ChatAsync(text);   // 回复流式出现在气泡里；出错提示也在气泡里
        }
        catch (Exception)
        {
            // ChatAsync 已经在气泡里说明并写了日志。
        }
        finally
        {
            _model.Busy = false;
            UpdatePlaceholder();
            if (IsVisible) FocusInput();
        }
    }

    /// <summary>
    /// 放在身体右边、和身体中部对齐；右边放不下就放左边，再夹在工作区里。
    /// 宠物位置、身体包围盒是桌面像素，窗口尺寸是 DIP。
    /// </summary>
    private void Follow()
    {
        if (!IsVisible || _pet is null) return;
        var scaling = DesktopScaling;
        var w = Bounds.Width * scaling;
        var h = Bounds.Height * scaling;
        var gap = Gap * scaling;
        var (left, top, right, bottom) = _pet.BodyBounds;
        var x = right + gap;
        var y = top + (bottom - top) * 0.45 - h / 2;
        var area = (Screens.ScreenFromWindow(_pet) ?? Screens.Primary)?.WorkingArea;
        if (area is { } a)
        {
            if (x + w > a.Right) x = left - gap - w;
            x = Math.Clamp(x, a.X, Math.Max(a.X, a.Right - w));
            y = Math.Clamp(y, a.Y, Math.Max(a.Y, a.Bottom - h));
        }
        Position = new PixelPoint((int)x, (int)y);
    }
}
