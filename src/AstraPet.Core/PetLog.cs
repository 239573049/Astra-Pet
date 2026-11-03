using System.Text;

namespace AstraPet.Core;

/// <summary>一条日志。</summary>
public sealed record LogEntry(DateTime Time, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff}  {Message}";
}

/// <summary>
/// 全局日志：照旧打到标准输出，同时留最近 <see cref="Capacity"/> 条在内存里，
/// 调过 <see cref="UseDirectory"/> 之后还按天写进日志文件（Windows 的发布版没有控制台，不写文件日志就等于丢了）。
/// 不依赖 Avalonia，<see cref="Written"/> 在写日志的线程上触发，订阅方自己切 UI 线程。
/// </summary>
public static class PetLog
{
    public const int Capacity = 500;

    /// <summary>日志文件保留几天。</summary>
    public const int KeepDays = 7;

    private static readonly Queue<LogEntry> Recent = new();
    private static readonly Lock Gate = new();

    private static string? _directory;
    private static StreamWriter? _file;
    private static DateOnly _fileDate;

    public static event Action<LogEntry>? Written;

    /// <summary>当前在写的日志目录，没开文件日志时为 null。</summary>
    public static string? Directory => _directory;

    /// <summary>开始往 <paramref name="directory"/> 里写日志文件（astra-pet-yyyyMMdd.log），顺带删掉过期的。</summary>
    public static void UseDirectory(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            _file?.Dispose();
            _file = null;
        }
        try
        {
            // 第一次运行时目录还不存在，不用清理（也别让 EnumerateFiles 抛异常：正常路径不抛异常）。
            if (!System.IO.Directory.Exists(directory)) return;
            var cutoff = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (var old in System.IO.Directory.EnumerateFiles(directory, "astra-pet-*.log"))
                if (File.GetLastWriteTime(old) < cutoff)
                    File.Delete(old);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Write(string message)
    {
        var entry = new LogEntry(DateTime.Now, message);
        Console.WriteLine(message);
        lock (Gate)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > Capacity) Recent.Dequeue();
            WriteFile(entry);
        }
        Written?.Invoke(entry);
    }

    public static LogEntry[] Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }

    /// <summary>在 <see cref="Gate"/> 里调。写失败就关掉文件日志，不影响程序运行。</summary>
    private static void WriteFile(LogEntry entry)
    {
        if (_directory is null) return;
        try
        {
            var today = DateOnly.FromDateTime(entry.Time);
            if (_file is null || _fileDate != today)
            {
                _file?.Dispose();
                System.IO.Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"astra-pet-{today:yyyyMMdd}.log");
                // 允许别的进程同时读（用户开着日志看），自动刷新：崩溃前的最后几行也要留下。
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _fileDate = today;
            }
            _file.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff}  {entry.Message}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"写日志文件失败，之后只输出到控制台: {e.Message}");
            _file?.Dispose();
            _file = null;
            _directory = null;
        }
    }
}
