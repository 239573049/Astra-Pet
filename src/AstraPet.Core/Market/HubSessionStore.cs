using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraPet.Core.Market;

/// <summary>
/// 市场的登录状态单独存成数据目录下的 hub-session.json（访问令牌和昵称），不放进 settings.json：
/// 设置文件会被用户拷来拷去、发给开发者排查问题，令牌不该跟着走。
/// Unix 上文件权限是 0600（只有自己能读写）；Windows 上数据目录本来就在用户目录下。令牌不写日志。
/// </summary>
public sealed class HubSessionStore(string directory)
{
    public const string FileName = "hub-session.json";

    public string FilePath { get; } = Path.Combine(directory, FileName);

    /// <summary>读不到、格式不对都当没登录。</summary>
    public HubSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var session = JsonSerializer.Deserialize(File.ReadAllText(FilePath), HubSessionJsonContext.Default.HubSession);
            return session is { AccessToken.Length: > 0 } ? session : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            PetLog.Write($"市场登录状态读取失败，按未登录处理: {e.Message}");
            return null;
        }
    }

    /// <summary>先写临时文件（创建时就是 0600）再改名，写到一半不会留下半个文件。</summary>
    public void Save(HubSession session)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        using (var stream = new FileStream(temp, options))
            JsonSerializer.Serialize(stream, session, HubSessionJsonContext.Default.HubSession);
        // 临时文件可能是上次留下的（那样 UnixCreateMode 不生效），改名前再明确设一次。
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, OwnerOnly);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PetLog.Write($"删除市场登录状态失败: {e.Message}");
        }
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(HubSession))]
internal sealed partial class HubSessionJsonContext : JsonSerializerContext;
