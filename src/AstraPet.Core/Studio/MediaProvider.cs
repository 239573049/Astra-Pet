using System.Text.Json.Serialization;

namespace AstraPet.Core.Studio;

/// <summary>生成模型的种类：图片（设定图、站姿）或视频（动作）。</summary>
public enum MediaKind { Image, Video }

/// <summary>生成模型的接口协议（design/pet-studio/05）。</summary>
public enum MediaProtocol
{
    /// <summary>火山方舟 contents/generations/tasks：提交任务 → 轮询 → 下载视频。</summary>
    ArkVideoTasks,
    /// <summary>OpenAI 兼容的 /images/generations（文生图）和 /images/edits（参考图，multipart）。</summary>
    OpenAIImages,
    /// <summary>火山方舟 Seedream：/images/generations，参考图用 data URL 放在 image 里。</summary>
    ArkImages,
}

/// <summary>
/// 一个图片或视频生成接口。和对话模型的 <see cref="LlmProvider"/> 分开存：字段和用途不同。
/// 密钥和对话模型一样明文存在设置文件里（D15 以后统一迁到系统钥匙串）；任务记录、日志、账本里一律不写。
/// </summary>
public sealed class MediaProvider : Observable
{
    private string _name = "新的生成模型";
    private MediaKind _kind;
    private MediaProtocol _protocol;
    private string _baseUrl = "";
    private string _apiKey = "";
    private string _model = "";
    private string _extraJson = "";
    private string _presetId = "";
    private Dictionary<string, double> _prices = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public MediaKind Kind { get => _kind; set => Set(ref _kind, value); }
    public MediaProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value ?? ""); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value ?? ""); }
    public string Model { get => _model; set => Set(ref _model, value ?? ""); }
    /// <summary>高级：原样合并进请求体的 JSON 对象（seed、camera_fixed 这类各家的私有参数）。</summary>
    public string ExtraJson { get => _extraJson; set => Set(ref _extraJson, value ?? ""); }
    public string PresetId { get => _presetId; set => Set(ref _presetId, value ?? ""); }

    /// <summary>
    /// 用户可编辑的单价，只用于估算：视频按分辨率（「720p」→ 元/秒），图片按质量（「high」→ 元/张，「*」是默认）。整体替换，不原地改。
    /// </summary>
    public Dictionary<string, double> Prices { get => _prices; set => Set(ref _prices, value ?? new()); }

    [JsonIgnore] public bool IsConfigured => BaseUrl.Trim().Length > 0 && ApiKey.Trim().Length > 0 && Model.Trim().Length > 0;

    /// <summary>单价：先按 key 找，再找「*」；都没有返回 null（界面显示「单价未知」）。</summary>
    public double? PriceOf(string key) =>
        Prices.TryGetValue(key, out var p) ? p : Prices.TryGetValue("*", out var d) ? d : null;

    public MediaProvider Clone()
    {
        var copy = new MediaProvider { Id = Id };
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(MediaProvider other)
    {
        Name = other.Name;
        Kind = other.Kind;
        Protocol = other.Protocol;
        BaseUrl = other.BaseUrl;
        ApiKey = other.ApiKey;
        Model = other.Model;
        ExtraJson = other.ExtraJson;
        PresetId = other.PresetId;
        Prices = new Dictionary<string, double>(other.Prices);
    }

    public override string ToString() => $"{Name} · {Model}";
}

/// <summary>生成模型的预设：预填地址、协议、模型建议和参考单价。模型名常换代，都可以改。</summary>
public sealed record MediaPreset(
    string Id, string Name, MediaKind Kind, MediaProtocol Protocol, string BaseUrl, string Model,
    IReadOnlyDictionary<string, double> Prices, string PriceNote)
{
    public MediaProvider Create() => new()
    {
        Name = Name, Kind = Kind, Protocol = Protocol, BaseUrl = BaseUrl, Model = Model, PresetId = Id,
        Prices = new Dictionary<string, double>(Prices),
    };
}

public static class MediaPresets
{
    public const string ArkBase = "https://ark.cn-beijing.volces.com/api/v3";

    /// <summary>NextCoWork 网关：图片走 OpenAI Images（/v1/images/*），视频走方舟任务协议（/api/v3/contents/generations/tasks）。</summary>
    public const string NextCoWorkHost = "nextco.work";

    public static IReadOnlyList<MediaPreset> All { get; } =
    [
        // 和对话模型的 NextCoWork 预设同一个 Key。单价按 2026-11 模型目录的折后价（按 token 计费）粗算成人民币：
        // Seedance 2.5 无输入视频 USD 9/百万 token，720p 约 2.2 万 token/秒；GPT Image 2.5 输出 USD 12/百万 token，一张 1536×1024 约六千 token。
        new("nextcowork-video", "NextCoWork Seedance 2.5", MediaKind.Video, MediaProtocol.ArkVideoTasks, "https://nextco.work/api/v3",
            "doubao-seedance-2-5-260628",
            new Dictionary<string, double> { ["480p"] = 0.63, ["720p"] = 1.40, ["1080p"] = 3.45 },
            "按 NextCoWork 模型目录 2026-11 的折后价、1 美元≈7.1 元粗算；按 token 计费，以账单为准"),
        new("nextcowork-image", "NextCoWork GPT Image 2.5", MediaKind.Image, MediaProtocol.OpenAIImages, "https://nextco.work/v1",
            "gpt-image-2.5-flare",
            new Dictionary<string, double> { ["low"] = 0.05, ["medium"] = 0.15, ["high"] = 0.6, ["*"] = 0.6 },
            "按 NextCoWork 模型目录 2026-11 的折后价、1 美元≈7.1 元粗算；按 token 计费，以账单为准"),
        new("ark-seedance", "火山方舟 Seedance", MediaKind.Video, MediaProtocol.ArkVideoTasks, ArkBase,
            "doubao-seedance-2-5-260628",
            new Dictionary<string, double> { ["480p"] = 0.67, ["720p"] = 1.51 },
            "参考单价来自第三方转述的官方示例（2026 年），以账单为准"),
        new("ark-video-custom", "兼容方舟协议的视频接口", MediaKind.Video, MediaProtocol.ArkVideoTasks, "", "",
            new Dictionary<string, double>(), "按服务商的价格填"),
        new("ark-seedream", "火山方舟 Seedream", MediaKind.Image, MediaProtocol.ArkImages, ArkBase,
            "doubao-seedream-4-0-250828",
            new Dictionary<string, double> { ["*"] = 0.2 }, "参考单价，以账单为准"),
        new("openai-images", "OpenAI 兼容图片接口", MediaKind.Image, MediaProtocol.OpenAIImages, "https://api.openai.com/v1",
            "gpt-image-2.5",
            new Dictionary<string, double> { ["low"] = 0.1, ["medium"] = 0.3, ["high"] = 1.2, ["*"] = 0.3 },
            "参考单价，以账单为准"),
    ];

    public static MediaPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// NextCoWork 上可选的 Seedance 视频模型。Fast、Mini 只能出 480p / 720p，2.0 和 2.5 能出 1080p。
    /// 单价按 2026-11 模型目录的折后价（每百万 token）和 2.5 的 token 用量（480p 约 1 万、720p 约 2.2 万、1080p 约 4.9 万 token/秒）粗算。
    /// </summary>
    public static IReadOnlyList<VideoModel> NextCoWorkVideoModels { get; } =
    [
        new("doubao-seedance-2-5-260628", "Seedance 2.5", "效果最好，最高 1080p",
            ["480p", "720p", "1080p"], new Dictionary<string, double> { ["480p"] = 0.63, ["720p"] = 1.40, ["1080p"] = 3.45 }),
        new("doubao-seedance-2-0-260128", "Seedance 2.0", "最高 1080p，比 2.5 便宜",
            ["480p", "720p", "1080p"], new Dictionary<string, double> { ["480p"] = 0.41, ["720p"] = 0.92, ["1080p"] = 2.29 }),
        new("doubao-seedance-2-0-fast-260128", "Seedance 2.0 Fast", "出片快，最高 720p",
            ["480p", "720p"], new Dictionary<string, double> { ["480p"] = 0.19, ["720p"] = 0.41 }),
        new("doubao-seedance-2-0-mini-260615", "Seedance 2.0 Mini", "最便宜，最高 720p",
            ["480p", "720p"], new Dictionary<string, double> { ["480p"] = 0.12, ["720p"] = 0.26 }),
    ];

    public static VideoModel? VideoModelOf(string? model) => NextCoWorkVideoModels.FirstOrDefault(m => m.Id == model?.Trim());

    /// <summary>这个接口的模型能出的分辨率：已知模型按表，不认识的模型原样返回（由服务商决定）。</summary>
    public static string FitResolution(MediaProvider provider, string resolution) =>
        VideoModelOf(provider.Model)?.Fit(resolution) ?? resolution;

    /// <summary>这个接口的模型能生成的时长：已知模型夹到它的范围内，不认识的模型原样返回（由服务商决定）。</summary>
    public static int FitDuration(MediaProvider provider, int seconds) =>
        VideoModelOf(provider.Model)?.FitSeconds(seconds) ?? seconds;

    /// <summary>工作台第一次打开（一个生成模型都没配）时默认放上的预设：图片、视频都用 NextCoWork。</summary>
    public static readonly IReadOnlyList<string> Defaults = ["nextcowork-image", "nextcowork-video"];

    /// <summary>地址的主机名（小写）；填的不是网址返回 null。用来把同一个网关的对话 Key 带到生成模型上。</summary>
    public static string? HostOf(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant() : null;
}

/// <summary>一个可选的视频模型：支持的分辨率（从低到高）和每秒参考单价（元）。</summary>
public sealed record VideoModel(string Id, string Name, string Note, IReadOnlyList<string> Resolutions, IReadOnlyDictionary<string, double> Prices)
{
    /// <summary>默认用的分辨率：能出的最高一档。</summary>
    public string Best => Resolutions[^1];

    public bool Supports(string resolution) => Resolutions.Contains(resolution);

    /// <summary>支持就原样用，不支持就退到能出的最高一档。</summary>
    public string Fit(string resolution) => Supports(resolution) ? resolution : Best;

    /// <summary>能生成的时长（秒）。Seedance 2.x 都是 4～15 秒，超出范围提交会被拒（HTTP 400）。</summary>
    public int MinSeconds { get; init; } = 4;
    public int MaxSeconds { get; init; } = 15;

    public int FitSeconds(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);
}
