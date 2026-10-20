namespace AstraPet.Core;

/// <summary>一条日志。</summary>
public sealed record LogEntry(DateTime Time, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff}  {Message}";
}

/// <summary>
/// 全局日志：照旧打到标准输出，同时留最近 <see cref="Capacity"/> 条给控制台面板看。
/// 不依赖 Avalonia，<see cref="Written"/> 在写日志的线程上触发，订阅方自己切 UI 线程。
/// </summary>
public static class PetLog
{
    public const int Capacity = 500;

    private static readonly Queue<LogEntry> Recent = new();
    private static readonly Lock Gate = new();

    public static event Action<LogEntry>? Written;

    public static void Write(string message)
    {
        var entry = new LogEntry(DateTime.Now, message);
        Console.WriteLine(message);
        lock (Gate)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > Capacity) Recent.Dequeue();
        }
        Written?.Invoke(entry);
    }

    public static LogEntry[] Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }
}
