using System.Runtime.Versioning;

namespace AstraPet.Platform.MacOS;

/// <summary>
/// 挂载 dmg，把新的 .app 复制到旧 .app 旁边；程序退出后由一个小脚本换掉旧的并重新打开。
/// 完整性靠 HTTPS 和文件大小校验（站点的 latest.json 没有哈希）。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDmgInstaller : UpdateInstallerBase
{
    public override IReadOnlyList<string> PreferredKinds { get; } = ["dmg", "zip"];
    protected override string InstallableKind => "dmg";

    /// <summary>当前运行的 .app（…/Astra Pet.app/Contents/MacOS/AstraPet 往上三层），不是从 .app 运行时为 null。</summary>
    public static string? CurrentAppBundle()
    {
        var exe = Environment.ProcessPath ?? "";
        var app = exe.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe))) : null;
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    protected override async Task<string?> InstallCoreAsync(string dmg)
    {
        if (CurrentAppBundle() is not { } target)
            return "当前不是从 .app 运行的，没法原地更新";

        // 新版本先放在旧版本旁边（同一个磁盘，之后换名是瞬间完成的）。目录不可写（比如别的用户装的）就退回手动安装。
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".AstraPet-update-" + Guid.NewGuid().ToString("N")[..8]);
        try { Directory.CreateDirectory(stage); }
        catch { return $"没有权限修改 {Path.GetDirectoryName(target)}，请手动打开安装包安装"; }

        var mount = Path.Combine(Path.GetTempPath(), "AstraPet-update-mnt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        try
        {
            var (code, output) = await ProcessRunner.RunAsync("/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noverify", "-mountpoint", mount, dmg);
            if (code != 0) throw new IOException("挂载安装包失败：" + output.Trim());
            try
            {
                var app = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault()
                    ?? throw new IOException("安装包里没有找到应用");
                (code, output) = await ProcessRunner.RunAsync("/usr/bin/ditto", app, Path.Combine(stage, "new.app"));   // ditto 保留签名
                if (code != 0) throw new IOException("复制新版本失败：" + output.Trim());
            }
            finally
            {
                await ProcessRunner.RunAsync("/usr/bin/hdiutil", "detach", mount, "-force");
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
        ProcessRunner.StartDetached("/bin/sh", "-c", script, "astra-update", Environment.ProcessId.ToString(), target, stage);
        return null;
    }
}
