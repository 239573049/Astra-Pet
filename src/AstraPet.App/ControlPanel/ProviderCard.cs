using System.Globalization;
using AstraPet.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AstraPet.ControlPanel;

/// <summary>提供商图标（Assets/providers/*.png，LobeHub Icons）和卡片头部的品牌色渐变，按名字缓存。</summary>
public static class ProviderIcons
{
    private static readonly Dictionary<string, Bitmap?> Bitmaps = new();
    private static readonly Dictionary<string, IBrush> Headers = new();

    public static Bitmap? Get(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (Bitmaps.TryGetValue(icon, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://AstraPet/Assets/providers/{icon}.png"));
            bitmap = new Bitmap(stream);
        }
        catch (Exception e)
        {
            PetLog.Write($"提供商图标 {icon} 加载失败: {e.Message}");
        }
        return Bitmaps[icon] = bitmap;
    }

    /// <summary>卡片头部：品牌色从左上角淡淡地晕开，和图 2 那种头部一样。</summary>
    public static IBrush Header(string accent)
    {
        if (Headers.TryGetValue(accent, out var cached)) return cached;
        var color = Color.TryParse(accent, out var c) ? c : Color.Parse("#32AA7D");
        return Headers[accent] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x3A, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x0C, color.R, color.G, color.B), 1),
            },
        };
    }

    /// <summary>没有图标时画名字的第一个字。</summary>
    public static string FirstGlyph(string name)
    {
        var text = name.Trim();
        if (text.Length == 0) return "?";
        return new StringInfo(text).SubstringByTextElements(0, 1).ToUpperInvariant();
    }
}

/// <summary>提供商卡片：包一个 <see cref="LlmProvider"/>，加上界面要显示的派生内容和这张卡自己的测试状态。</summary>
public sealed class ProviderCard : Observable
{
    private readonly ProvidersViewModel _hub;
    private bool _testing;
    private bool _testOk;
    private string _testText = "";

    public ProviderCard(LlmProvider provider, ProvidersViewModel hub)
    {
        Provider = provider;
        _hub = hub;
        provider.PropertyChanged += (_, _) => Refresh();
    }

    public LlmProvider Provider { get; }

    public string Name => Provider.Name;
    public string Host => ProviderText.Host(Provider.BaseUrl);
    public string ProtocolLabel => ProviderText.ProtocolShort(Provider.Protocol);

    private ProviderPreset? Preset => ProviderPresets.Resolve(Provider);
    public Bitmap? Icon => ProviderIcons.Get(Preset?.Icon);
    public bool HasIcon => Icon is not null;
    public string Glyph => Preset?.Glyph is { Length: > 0 } g ? g : ProviderIcons.FirstGlyph(Provider.Name);
    public IBrush HeaderBrush => ProviderIcons.Header(Preset?.Accent ?? "#32AA7D");

    /// <summary>对话正在用它。开关只能打开：关掉当前这个等于没有模型可用，所以点关会被弹回去。</summary>
    public bool IsActive
    {
        get => _hub.IsActive(Provider);
        set
        {
            if (value) _hub.Activate(Provider);
            else if (IsActive) Dispatcher.UIThread.Post(() => Raise(nameof(IsActive)));
        }
    }

    public string StatusText => IsActive ? "使用中" : "未启用";

    /// <summary>
    /// 当前模型排第一，其余是「获取模型」或导入得到的列表；都没有就用内置模型库（models.dev）里这家的模型，从新到旧。
    /// </summary>
    private List<string> AllModels =>
        new[] { Provider.Model }
            .Concat(Provider.Models.Count > 0 ? Provider.Models : ModelCatalog.Current.SuggestIds(Provider))
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();

    public string ModelCountText => $"{Math.Max(1, AllModels.Count)} 个模型";

    /// <summary>卡片上展示的模型标签：最多三个，多的折成 +N。</summary>
    public IReadOnlyList<string> ModelChips
    {
        get
        {
            var all = AllModels;
            var chips = all.Take(3).ToList();
            if (all.Count > 3) chips.Add($"+{all.Count - 3}");
            return chips;
        }
    }

    public bool KeyMissing => string.IsNullOrWhiteSpace(Provider.ApiKey) && !IsLocal;
    private bool IsLocal => ProviderText.IsLocal(Provider.BaseUrl);

    public string KeyText => string.IsNullOrWhiteSpace(Provider.ApiKey)
        ? IsLocal ? "无需 Key" : "未填 Key"
        : ProviderText.MaskKey(Provider.ApiKey);

    public bool Testing { get => _testing; set => Set(ref _testing, value); }
    public bool TestOk { get => _testOk; set => Set(ref _testOk, value); }
    public string TestText
    {
        get => _testText;
        set { if (Set(ref _testText, value)) Raise(nameof(HasTestText)); }
    }
    public bool HasTestText => _testText.Length > 0;

    /// <summary>提供商的字段或「正在用谁」变了，重新算一遍派生内容。</summary>
    public void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(Host), nameof(ProtocolLabel), nameof(Icon), nameof(HasIcon), nameof(Glyph),
                     nameof(HeaderBrush), nameof(IsActive), nameof(StatusText), nameof(ModelCountText), nameof(ModelChips),
                     nameof(KeyMissing), nameof(KeyText),
                 })
            Raise(name);
    }
}
