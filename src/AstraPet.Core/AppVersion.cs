namespace AstraPet.Core;

/// <summary>当前程序的版本和默认的更新服务器。平台标识见 IPlatform.Identity。</summary>
public static class AppVersion
{
    /// <summary>x.y.z 或 x.y.z-preview.N。构建时由 -p:Version 决定，去掉 SDK 附加的 “+提交号”。</summary>
    public static string Current { get; } = Normalize(BuildInfo.Version);

    /// <summary>构建时写进去的更新服务器地址（-p:UpdateBaseUrl，见 AstraPet.csproj 的 GenerateBuildInfo）。没配就是空。</summary>
    public static string DefaultServer { get; } = BuildInfo.UpdateBaseUrl.Trim();

    private static string Normalize(string text)
    {
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }
}
