using System.Collections.ObjectModel;
using AstraPet.Core;
using AstraPet.Pet;
using Avalonia.Layout;

namespace AstraPet.ControlPanel;

/// <summary>对话框里的一条消息。</summary>
public sealed record ChatLine(string Text, bool FromUser, bool IsError = false)
{
    /// <summary>模型的回复按 Markdown 渲染（列表、代码块、表格）；用户自己说的话和错误提示保持纯文本。</summary>
    public bool IsMarkdown => !FromUser && !IsError;
    public bool IsPlain => !IsMarkdown;
    public HorizontalAlignment Alignment => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    // 气泡颜色由 ChatWindow.axaml 按 FromUser / IsError 套设计令牌，跟着明暗主题走。
}

public sealed class ChatViewModel : Observable
{
    private readonly PetCompanion _companion;
    private readonly PetSettings _settings;
    private string _input = "";
    private bool _busy;

    public ChatViewModel(PetCompanion companion, PetSettings settings)
    {
        _companion = companion;
        _settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PetSettings.ActiveProviderId) or nameof(PetSettings.ActiveProvider))
                Raise(nameof(ProviderText));
        };
    }

    public ObservableCollection<ChatLine> Lines { get; } = new();

    public string Input { get => _input; set => Set(ref _input, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public string ProviderText => _settings.ActiveProvider is { } p
        ? $"{p.Name} · {p.Model}"
        : "还没有配置模型，请先到控制台「智能」页添加";

    public async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || Busy) return;
        Input = "";
        Lines.Add(new ChatLine(text, FromUser: true));
        Busy = true;
        try
        {
            Lines.Add(new ChatLine(await _companion.ChatAsync(text), FromUser: false));
        }
        catch (Exception e)
        {
            Lines.Add(new ChatLine(e.Message, FromUser: false, IsError: true));
        }
        finally
        {
            Busy = false;
            Raise(nameof(ProviderText));
        }
    }

    public void Clear()
    {
        Lines.Clear();
        _companion.Agent.ClearHistory();
    }
}
