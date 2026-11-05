namespace AstraPet.Core.Studio;

// 生成模型的端口（design/pet-studio/05 §2.2、§3.3）。实现在 Infrastructure/Studio/Providers，付费调用只在那里发生。

public sealed record VideoJob(
    string Prompt, byte[] FirstFrame, byte[]? LastFrame,
    string Resolution, int DurationSeconds, string Ratio = "adaptive");

public enum VideoTaskState { Queued, Running, Succeeded, Failed, Cancelled, Expired }

public sealed record VideoTaskStatus(string TaskId, VideoTaskState State, string? VideoUrl, long? UsageTokens, string? Error)
{
    public bool IsFinished => State is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled or VideoTaskState.Expired;
}

public interface IVideoGenerator
{
    /// <summary>提交一个任务，返回任务 id。<b>会计费</b>，调用方不能自动重试。</summary>
    Task<string> SubmitAsync(MediaProvider provider, VideoJob job, CancellationToken ct);
    Task<VideoTaskStatus> GetAsync(MediaProvider provider, string taskId, CancellationToken ct);
    Task CancelAsync(MediaProvider provider, string taskId, CancellationToken ct);
    /// <summary>不花钱地检查地址和密钥：查一个不存在的任务，看返回 401 还是 404。</summary>
    Task<string> TestAsync(MediaProvider provider, CancellationToken ct);
}

/// <param name="References">参考图（PNG / JPEG 字节）；为空时走文生图。</param>
/// <param name="Size">像素尺寸，比如 1024x1536。</param>
public sealed record ImageJob(string Prompt, IReadOnlyList<byte[]> References, string Size, int Count, string? Quality = null);

/// <summary>生成结果。要了多张时可能部分失败：成功的放 <see cref="Images"/>，失败原因放 <see cref="Errors"/>。</summary>
public sealed record ImageResult(IReadOnlyList<byte[]> Images, IReadOnlyList<string> Errors, string? Usage = null);

public interface IImageGenerator
{
    /// <summary>同步生成，通常几十秒。<b>会计费</b>。</summary>
    Task<ImageResult> GenerateAsync(MediaProvider provider, ImageJob job, CancellationToken ct);
    /// <summary>不花钱地检查地址和密钥（发一个必然失败的请求看是不是鉴权错误）。</summary>
    Task<string> TestAsync(MediaProvider provider, CancellationToken ct);
}

/// <summary>生成接口返回的错误。<see cref="Status"/> 是 HTTP 状态码（网络错误时为 null），消息是服务端原文。</summary>
public sealed class GenerationException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>一次付费操作的费用估算，给确认框看。金额是估算，单价未知时 <see cref="Total"/> 为 null。</summary>
public sealed record CostEstimate(
    string Title, IReadOnlyList<CostItem> Items, double? Total, string Provider, string Model, string Note = "估算，以账单为准")
{
    public string TotalText => Total is { } t ? $"约 {t:0.##} 元" : "单价未知";
}

public sealed record CostItem(string Label, double? Amount);

/// <summary>所有付费操作的确认关口（04 §6）。App 弹确认框；返回 false 就不提交。</summary>
public interface ICostGate
{
    Task<bool> ConfirmAsync(CostEstimate estimate, CancellationToken ct);
}
