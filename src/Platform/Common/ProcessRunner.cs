using System.ComponentModel;
using System.Diagnostics;

namespace AstraPet.Platform;

/// <summary>起子进程的公共写法：不用 shell、不弹窗口、找不到程序不抛异常。</summary>
internal static class ProcessRunner
{
    /// <summary>运行并等它结束，返回退出码和 stdout + stderr。程序不存在时返回 127。</summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
    {
        var psi = Info(file, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        try
        {
            using var p = Process.Start(psi) ?? throw new IOException("无法启动 " + file);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (Win32Exception)
        {
            return (127, file + " 不存在");
        }
    }

    /// <summary>运行并读完 stdout（最多等 <paramref name="timeoutMs"/>）。在后台线程上调。</summary>
    public static string ReadOutput(string file, string[] args, int timeoutMs = 5000)
    {
        var psi = Info(file, args);
        psi.RedirectStandardOutput = true;
        using var p = Process.Start(psi) ?? throw new IOException("无法启动 " + file);
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return output;
    }

    /// <summary>启动后不管它（更新完重启用的小脚本）。</summary>
    public static void StartDetached(string file, params string[] args)
    {
        using var _ = Process.Start(Info(file, args));
    }

    public static ProcessStartInfo Info(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
