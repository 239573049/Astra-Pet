using System.Collections.Concurrent;
using System.Net;
using AstraPet.Core;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 视频任务调度（05 §2.3、04 §3 ⑤）：
/// <list type="bullet">
/// <item>并发上限（默认 2）：同时在服务端生成的任务数；</item>
/// <item><b>提交只发一次</b>：失败直接报给调用方，不自动重试（每次提交都计费）；</item>
/// <item>提交成功后 <b>先写任务记录再轮询</b>，程序重启后 <see cref="Resume"/> 接着查同一个任务；</item>
/// <item>查询指数退避（10 秒起，最长 30 秒），429、5xx 和网络错误继续退避重试；</item>
/// <item>成功后立刻下载（链接约 24 小时过期），先写 .part 再改名；链接过期标成「已生成但未下载」。</item>
/// </list>
/// <see cref="Changed"/> 在线程池上触发，UI 自己切线程。
/// </summary>
public sealed class VideoTaskQueue : IDisposable
{
    public static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxPoll = TimeSpan.FromSeconds(30);

    private readonly Func<MediaProvider, IVideoGenerator> _generators;
    private readonly Func<string, MediaProvider?> _providers;
    private readonly HttpClient _download;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _submit = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly ConcurrentDictionary<string, byte> _tracking = new();
    private readonly ConcurrentDictionary<string, int> _waiting = new();

    /// <param name="generators">按提供商挑客户端（测试里换成假的）。</param>
    /// <param name="providers">按 id 找提供商（恢复轮询时用；密钥不存进任务记录）。</param>
    public VideoTaskQueue(Func<MediaProvider, IVideoGenerator> generators, Func<string, MediaProvider?> providers,
        HttpClient? download = null, TimeProvider? time = null, int concurrency = 2)
    {
        _generators = generators;
        _providers = providers;
        _download = download ?? MediaHttp.Shared;
        _time = time ?? TimeProvider.System;
        _slots = new SemaphoreSlim(Math.Max(1, concurrency));
    }

    /// <summary>任务记录变了（提交、状态、下载）。</summary>
    public event Action<TaskRecord>? Changed;

    /// <summary>本地排队（还没提交）的数量变了。</summary>
    public event Action? WaitingChanged;

    private static string Key(StudioProject project, string action) => project.Directory + "|" + action;

    /// <summary>这个动作有几段在本地排队等提交。</summary>
    public int WaitingCount(StudioProject project, string action) => _waiting.GetValueOrDefault(Key(project, action));

    public bool IsTracking(TaskRecord record) => record.FilePath is { } f && _tracking.ContainsKey(f);

    /// <summary>
    /// 排队提交一段（费用已经确认过）。等到有空位才提交；返回的任务在视频下载完（或失败）时完成。
    /// 提交失败抛 <see cref="GenerationException"/>，不留任务记录，也不重试。
    /// </summary>
    public async Task<TaskRecord> EnqueueAsync(StudioProject project, PlannedAction action, MediaProvider provider, VideoJob job,
        string basis, double? estimate, CancellationToken ct)
    {
        var key = Key(project, action.Id);
        _waiting.AddOrUpdate(key, 1, (_, n) => n + 1);
        WaitingChanged?.Invoke();
        var holding = false;
        var waiting = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            await _slots.WaitAsync(linked.Token);
            holding = true;

            TaskRecord record;
            await _submit.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var take = project.NextTake(action.Id);
                var generator = _generators(provider);
                // 从这里起不再响应取消：请求一旦发出就可能已经计费，必须拿到任务 id 写进记录。
                var taskId = await generator.SubmitAsync(provider, job, CancellationToken.None);
                record = new TaskRecord
                {
                    Action = action.Id, Take = take, TaskId = taskId, State = VideoTaskState.Queued,
                    ProviderId = provider.Id, Provider = provider.Name, Model = provider.Model,
                    Prompt = job.Prompt, Resolution = job.Resolution, Duration = job.DurationSeconds, Basis = basis,
                    SubmittedAt = DateTime.Now,
                };
                record.Save(project.TaskFile(action.Id, take));
                project.AppendLedger(new LedgerEntry
                {
                    Step = "generate", Provider = provider.Name, Model = provider.Model, TaskId = taskId, Estimate = estimate,
                    Summary = $"{action.Name}（{action.Id}）take {take}：{job.Resolution} {job.DurationSeconds} 秒",
                });
                PetLog.Write($"工作台：已提交 {action.Id} take {take}，任务 {taskId}");
            }
            finally
            {
                _submit.Release();
            }
            Leave(key);
            waiting = false;
            Changed?.Invoke(record);
            return await TrackAsync(project, record, provider, pollFirst: false);
        }
        finally
        {
            if (holding) _slots.Release();
            if (waiting) Leave(key);
        }
    }

    private void Leave(string key)
    {
        _waiting.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
        WaitingChanged?.Invoke();
    }

    /// <summary>
    /// 程序启动或打开工程时：所有没结束（或成功但没下载）的任务接着查，不重新提交。返回恢复了几个。
    /// </summary>
    public int Resume(StudioProject project)
    {
        var count = 0;
        foreach (var record in project.AllTakes().Where(r => r.NeedsWork).ToList())
        {
            if (record.FilePath is null || _tracking.ContainsKey(record.FilePath)) continue;
            var provider = _providers(record.ProviderId);
            if (provider is null)
            {
                record.Error = $"找不到提交时用的视频接口「{record.Provider}」（可能被删了），没法继续查询";
                record.Save(record.FilePath);
                Changed?.Invoke(record);
                continue;
            }
            count++;
            PetLog.Write($"工作台：恢复查询 {record.Action} take {record.Take}，任务 {record.TaskId}");
            _ = TrackAsync(project, record, provider, pollFirst: true);
        }
        return count;
    }

    private async Task<TaskRecord> TrackAsync(StudioProject project, TaskRecord record, MediaProvider provider, bool pollFirst)
    {
        var path = record.FilePath!;
        if (!_tracking.TryAdd(path, 0)) return record;
        try
        {
            var ct = _life.Token;
            var generator = _generators(provider);
            var delay = FirstPoll;
            var immediate = pollFirst;
            while (!record.IsFinished)
            {
                if (!immediate) await Task.Delay(delay, _time, ct);
                immediate = false;
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, MaxPoll.TotalSeconds));
                VideoTaskStatus status;
                try
                {
                    status = await generator.GetAsync(provider, record.TaskId, ct);
                }
                catch (GenerationException e) when (e.Status is null or 429 or >= 500)
                {
                    record.Error = $"查询失败，稍后重试：{e.Message}";
                    Save(record);
                    continue;
                }
                catch (GenerationException e)
                {
                    // 401、404 这种重试也没用：停下，任务记录留着，下次打开工程再试。
                    record.Error = $"查询失败：{e.Message}";
                    Save(record);
                    return record;
                }
                record.State = status.State;
                record.Error = status.Error;
                record.VideoUrl = status.VideoUrl ?? record.VideoUrl;
                record.UsageTokens = status.UsageTokens ?? record.UsageTokens;
                Save(record);
            }

            if (record.State == VideoTaskState.Succeeded && !record.Downloaded && record.DownloadError is null)
            {
                await DownloadAsync(project, record, ct);
                if (record.Downloaded && record.UsageTokens is { } tokens)
                    project.AppendLedger(new LedgerEntry
                    {
                        Step = "usage", Provider = record.Provider, Model = record.Model, TaskId = record.TaskId,
                        Summary = $"{record.Action} take {record.Take} 完成", Usage = $"{tokens} tokens",
                    });
            }
            return record;
        }
        catch (OperationCanceledException)
        {
            return record;   // 程序退出：记录留着，下次恢复
        }
        finally
        {
            _tracking.TryRemove(path, out _);
        }
    }

    private void Save(TaskRecord record)
    {
        record.Save(record.FilePath!);
        Changed?.Invoke(record);
    }

    private async Task DownloadAsync(StudioProject project, TaskRecord record, CancellationToken ct)
    {
        var target = project.RawVideo(record.Action, record.Take);
        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var response = await _download.GetAsync(record.VideoUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        record.DownloadError = $"视频链接已过期或失效（HTTP {(int)response.StatusCode}），这一段需要重新生成";
                        Save(record);
                        return;
                    }
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(part);
                    await response.Content.CopyToAsync(file, ct);
                }
                File.Move(part, target, overwrite: true);
                record.Downloaded = true;
                record.Error = null;
                Save(record);
                PetLog.Write($"工作台：已下载 {record.Action} take {record.Take}（{new FileInfo(target).Length / 1024} KB）");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt < 3)
            {
                record.Error = $"下载失败，稍后重试：{e.Message}";
                Save(record);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), _time, ct);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                // 连试三次不行：留着 NeedsWork，下次打开工程再下（链接 24 小时内有效）。
                record.Error = $"下载失败：{e.Message}";
                Save(record);
                TryDelete(part);
                return;
            }
        }
    }

    /// <summary>取消服务端排队中的任务（协议支持时）。</summary>
    public async Task CancelAsync(TaskRecord record, CancellationToken ct)
    {
        var provider = _providers(record.ProviderId) ?? throw new GenerationException("找不到提交时用的视频接口");
        await _generators(provider).CancelAsync(provider, record.TaskId, ct);
        record.State = VideoTaskState.Cancelled;
        Save(record);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
    }
}
