using AstraPet.Core;
using AstraPet.Platform;
using AstraPet.Platform.Linux;

namespace AstraPet.Tests;

public class PlatformTests
{
    [Fact]
    public void Factory_picks_current_os_and_never_returns_null_services()
    {
        var p = PlatformFactory.Create();
        var expectedOs = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsLinux() ? "linux" : null;
        if (expectedOs is not null) Assert.Equal(expectedOs, p.Identity.Os);
        Assert.Matches("^[a-z0-9]+-[a-z0-9]+$", p.Identity.Key);

        // 每个服务都有实现（不支持的平台是空实现），调用方不用判空。
        Assert.NotNull(p.Paths);
        Assert.NotNull(p.Shell);
        Assert.NotNull(p.AutoStart);
        Assert.NotNull(p.Speech);
        Assert.NotNull(p.Monitor);
        Assert.NotNull(p.Installer);
        Assert.NotEmpty(p.Installer.PreferredKinds);
        Assert.Same(NullNativeWindow.Instance, p.AttachWindow(0));
    }

    [Fact]
    public void Paths_are_absolute()
    {
        var paths = PlatformFactory.Create().Paths;
        Assert.True(Path.IsPathRooted(paths.DataDirectory), paths.DataDirectory);
        Assert.True(Path.IsPathRooted(paths.LogDirectory), paths.LogDirectory);
        Assert.True(Path.IsPathRooted(paths.ResourceDirectory), paths.ResourceDirectory);
    }

    [Fact]
    public void Unsupported_capability_has_a_note_on_linux_and_windows()
    {
        // 不支持的开关在界面上置灰时要能说明原因。macOS 全部支持，不检查。
        var caps = PlatformFactory.Create().Capabilities;
        if (!caps.OverFullscreen) Assert.False(string.IsNullOrWhiteSpace(caps.NoteFor(nameof(caps.OverFullscreen))));
        if (!caps.ClickThrough) Assert.False(string.IsNullOrWhiteSpace(caps.NoteFor(nameof(caps.ClickThrough))));
        if (!caps.AudioDetection) Assert.False(string.IsNullOrWhiteSpace(caps.NoteFor(nameof(caps.AudioDetection))));
    }

    [Fact]
    public async Task ProcessRunner_reports_missing_program_as_127()
    {
        var (code, output) = await ProcessRunner.RunAsync("astra-pet-no-such-program-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(127, code);
        Assert.Contains("不存在", output);
    }

    [Theory]
    [InlineData("/usr/bin/astra-pet", "/usr/bin/astra-pet")]
    [InlineData("/home/a b/AstraPet", "\"/home/a b/AstraPet\"")]                     // 空格要加引号
    [InlineData("FFMPEG=/opt/x/ffmpeg", "FFMPEG=/opt/x/ffmpeg")]
    [InlineData("ASTRA_PET_VIDEOS=/a/$HOME/v", "\"ASTRA_PET_VIDEOS=/a/\\$HOME/v\"")] // $ 在引号里要转义
    [InlineData("50%", "50%%")]                                                     // % 是字段代码前缀，要写两个
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Desktop_entry_exec_quoting_follows_spec(string arg, string expected) =>
        Assert.Equal(expected, DesktopEntry.Quote(arg));
}

public class PetLogTests
{
    [Fact]
    public void Writes_daily_file_and_removes_expired_ones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var expired = Path.Combine(dir, "astra-pet-20000101.log");
        File.WriteAllText(expired, "old");
        File.SetLastWriteTime(expired, DateTime.Now.AddDays(-(PetLog.KeepDays + 1)));
        try
        {
            PetLog.UseDirectory(dir);
            var marker = "测试日志 " + Guid.NewGuid();
            PetLog.Write(marker);

            Assert.False(File.Exists(expired));
            var today = Path.Combine(dir, $"astra-pet-{DateTime.Now:yyyyMMdd}.log");
            using var reader = new StreamReader(new FileStream(today, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert.Contains(marker, reader.ReadToEnd());
        }
        finally
        {
            PetLog.UseDirectory(Path.Combine(dir, "closed"));   // 释放文件句柄
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
