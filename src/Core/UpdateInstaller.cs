using System.Diagnostics;
using System.Net.Http.Headers;

namespace AstraPet.Core;

/// <summary>
/// 应用内更新：下载安装包，再按平台就地安装并重启。
///   macOS   挂载 dmg，把新的 .app 复制到旧 .app 旁边；程序退出后由一个小脚本换掉旧的并重新打开。
///   Windows 静默运行 Inno Setup 安装包（它会自己关掉宠物、覆盖文件），装完由安装包重新启动。
///   Linux   用 pkexec 以管理员身份 dpkg -i，装完重新启动。
/// 完整性靠 HTTPS 和文件大小校验（站点的 latest.json 没有哈希）。
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>这个更新能不能在应用内装（有本机平台的安装包，而且类型认识）。zip 和没有文件的退回浏览器下载。</summary>
    public static bool CanInstall(UpdateInfo info) =>
        info.DownloadUrl.Length > 0 && info.Kind is "dmg" or "exe" or "deb";

    /// <summary>下载安装包到临时目录，返回本地路径。progress 取 0~1，大小未知时不报告。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AstraPet-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 旧文件被占用就算了 */ }
        Directory.CreateDirectory(dir);

        var name = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
        if (string.IsNullOrEmpty(name)) name = "AstraPet-" + info.Version + "." + info.Kind;
        var path = Path.Combine(dir, name);
        var part = path + ".part";

        using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Size;

        long done = 0;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(1.0, (double)done / total));
            }
        }
        if (total > 0 && done != total) throw new IOException($"下载不完整（{done}/{total} 字节）");
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// 安装已下载的安装包。成功返回 null，调用方随后应立刻退出程序（安装和重启由已经起好的后台进程完成）；
    /// 失败返回给人看的原因，程序继续运行。
    /// </summary>
    public static async Task<string?> InstallAsync(UpdateInfo info, string file)
    {
        try
        {
            return info.Kind switch
            {
                "dmg" when OperatingSystem.IsMacOS() => await InstallMacAsync(file),
                "exe" when OperatingSystem.IsWindows() => InstallWindows(file),
                "deb" when OperatingSystem.IsLinux() => await InstallLinuxAsync(file),
                _ => "这个安装包不适合当前系统",
            };
        }
        catch (Exception e)
        {
            PetLog.Write($"安装更新失败: {e}");
            return e.Message;
        }
    }

    private static string? InstallWindows(string file)
    {
        // /relaunch=1 让安装包装完后重新启动宠物（见 AstraPet.iss）；安装包自己会先结束正在运行的 AstraPet.exe。
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true });
        return null;
    }

    private static async Task<string?> InstallMacAsync(string dmg)
    {
        // 当前的 .app：…/Astra Pet.app/Contents/MacOS/AstraPet 往上三层
        var exe = Environment.ProcessPath ?? "";
        var target = exe.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe))) : null;
        if (target is null || !target.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return "当前不是从 .app 运行的，没法原地更新";

        // 新版本先放在旧版本旁边（同一个磁盘，之后换名是瞬间完成的）。目录不可写（比如别的用户装的）就退回手动安装。
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".AstraPet-update-" + Guid.NewGuid().ToString("N")[..8]);
        try { Directory.CreateDirectory(stage); }
        catch { return $"没有权限修改 {Path.GetDirectoryName(target)}，请手动打开安装包安装"; }

        var mount = Path.Combine(Path.GetTempPath(), "AstraPet-update-mnt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        try
        {
            var (code, output) = await RunAsync("/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noverify", "-mountpoint", mount, dmg);
            if (code != 0) throw new IOException("挂载安装包失败：" + output.Trim());
            try
            {
                var app = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault()
                    ?? throw new IOException("安装包里没有找到应用");
                (code, output) = await RunAsync("/usr/bin/ditto", app, Path.Combine(stage, "new.app"));   // ditto 保留签名
                if (code != 0) throw new IOException("复制新版本失败：" + output.Trim());
            }
            finally
            {
                await RunAsync("/usr/bin/hdiutil", "detach", mount, "-force");
            }
        }
        catch
        {
            try { Directory.Delete(stage, true); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(mount); } catch { }
        }

        // 等本进程退出 → 换掉旧版本（换不成就还原）→ 打开。
        const string script = """
            pid=$1; target=$2; stage=$3
            while kill -0 "$pid" 2>/dev/null; do sleep 0.3; done
            if mv "$target" "$stage/old.app"; then
              if mv "$stage/new.app" "$target"; then rm -rf "$stage"; else mv "$stage/old.app" "$target"; fi
            fi
            open "$target"
            """;
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("astra-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(stage);
        Process.Start(psi);
        return null;
    }

    private static async Task<string?> InstallLinuxAsync(string deb)
    {
        // pkexec 弹系统的授权窗口；dpkg 换文件时，正在运行的旧程序不受影响。
        var (code, output) = await RunAsync("pkexec", "dpkg", "-i", deb);
        if (code == 126 || code == 127) return "授权被取消，或系统没有 pkexec；请手动安装：sudo dpkg -i " + deb;
        if (code != 0) return "dpkg 安装失败：" + output.Trim();

        var exe = Environment.ProcessPath ?? "/opt/astra-pet/AstraPet";
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("while kill -0 \"$1\" 2>/dev/null; do sleep 0.3; done; exec \"$2\"");
        psi.ArgumentList.Add("astra-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(exe);
        Process.Start(psi);
        return null;
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new IOException("无法启动 " + file);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, file + " 不存在");
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AstraPet", AppVersion.Current));
        return http;
    }
}
