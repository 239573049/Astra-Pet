using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AstraPet.Core;

/// <summary>
/// 设置存成一个 JSON 文件：macOS 在 ~/Library/Application Support/AstraPet/settings.json，
/// Windows 在 %APPDATA%\AstraPet\settings.json。API Key 也明文存在这里。
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

    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("ASTRA_PET_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AstraPet");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static PetSettings Load()
    {
        PetSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), TypeInfo);
        }
        catch (Exception e)
        {
            // 文件坏了就留一份备份，用默认值启动，不让宠物起不来。
            PetLog.Write($"读取设置失败，使用默认值: {e.Message}");
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        settings ??= new PetSettings();
        settings.Normalize();
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
