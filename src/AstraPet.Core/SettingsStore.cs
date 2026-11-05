using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AstraPet.Core;

/// <summary>
/// 设置存成数据目录下的一个 JSON 文件（目录见 IAppPaths.DataDirectory：macOS 在 ~/Library/Application Support/AstraPet，
/// Windows 在 %APPDATA%\AstraPet，Linux 在 ~/.config/AstraPet）。API Key 也明文存在这里。
/// 环境变量 ASTRA_PET_DATA 可以换成别的目录，测试时用它，免得动到正在用的那份设置。
/// </summary>
public static class SettingsStore
{
    // 不转义中文：记忆、台词里全是中文，写成 \uXXXX 没法看。
    private static readonly JsonSerializerOptions Options = new(SettingsJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonTypeInfo<PetSettings> TypeInfo => (JsonTypeInfo<PetSettings>)Options.GetTypeInfo(typeof(PetSettings));

    private static string? _directory;

    /// <summary>数据目录，由启动代码从 IAppPaths.DataDirectory 设进来（<see cref="UseDirectory"/>）。</summary>
    public static string Directory =>
        _directory ?? throw new InvalidOperationException("还没有设置数据目录，先调 SettingsStore.UseDirectory");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>启动时调一次，在读设置之前。</summary>
    public static void UseDirectory(string directory) => _directory = directory;

    public static PetSettings Load()
    {
        PetSettings? settings = null;
        var existed = File.Exists(FilePath);
        try
        {
            if (existed)
                settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), TypeInfo);
        }
        catch (Exception e)
        {
            // 文件坏了就留一份备份，用默认值启动，不让宠物起不来。
            PetLog.Write($"读取设置失败，使用默认值: {e.Message}");
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        var upgraded = settings is not null;
        settings ??= new PetSettings();
        settings.Normalize(upgraded);
        return settings;
    }

    public static void Save(PetSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, TypeInfo));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            PetLog.Write($"保存设置失败: {e.Message}");
        }
    }
}

/// <summary>
/// 设置的 JSON 元数据在编译期生成：NativeAOT 下反射式序列化是关掉的，运行时会直接抛异常。
/// 枚举存成字符串，和以前的文件格式一致。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PetSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
