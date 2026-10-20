using System.Runtime.InteropServices;

namespace AstraPet.Core;

/// <summary>当前程序的版本、平台标识和默认的更新服务器。</summary>
public static class AppVersion
{
    /// <summary>x.y.z 或 x.y.z-preview.N。构建时由 -p:Version 决定，去掉 SDK 附加的 “+提交号”。</summary>
    public static string Current { get; } = Normalize(BuildInfo.Version);

    /// <summary>
    /// 和 LocalGate 上传时的 x-astra-platform 一致：darwin-arm64、darwin-x64、win32-x64、linux-x64。
    /// 更新检测拿它挑属于本机的文件。
    /// </summary>
    public static string Platform { get; } = (OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux")
        + "-" + RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };

    /// <summary>构建时写进去的更新服务器地址（-p:UpdateBaseUrl，见 AstraPet.csproj 的 GenerateBuildInfo）。没配就是空。</summary>
    public static string DefaultServer { get; } = BuildInfo.UpdateBaseUrl.Trim();

    private static string Normalize(string text)
    {
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }
}
