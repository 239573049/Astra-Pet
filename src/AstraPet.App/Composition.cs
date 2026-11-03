using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Infrastructure.Media;
using AstraPet.Infrastructure.Update;
using AstraPet.Platform;

namespace AstraPet;

/// <summary>
/// 组合根：程序里唯一知道各层具体实现的地方（平台用哪个、动画用 ffmpeg 解、大模型和更新走 HTTP）。
/// 其他代码只拿接口。分两段：<see cref="Create"/> 在 Avalonia 启动之前跑（定目录、找素材和 ffmpeg），
/// 窗口由 <see cref="App"/> 在 Avalonia 起来之后用这里的东西创建。
/// 不用 DI 容器：规模小，手写更直观，也不给 NativeAOT 添反射。
/// </summary>
public sealed class Composition
{
    private Composition(IPlatform platform, PetCatalog catalog, string ffmpeg)
    {
        Platform = platform;
        Catalog = catalog;
        Ffmpeg = ffmpeg;
        Decoder = new FfmpegClipDecoder(ffmpeg);
    }

    public IPlatform Platform { get; }

    /// <summary>可选的角色：内置角色包（默认小t）+ 用户角色目录里的。</summary>
    public PetCatalog Catalog { get; }

    public string Ffmpeg { get; }

    /// <summary>动画解码器：每段起一个 ffmpeg 子进程流式解码。</summary>
    public IClipDecoder Decoder { get; }

    /// <summary>大模型：三种协议的 HTTP 调用。</summary>
    public ILlmClient Llm { get; } = LlmClient.Instance;

    /// <summary>更新检测的数据源：LocalGate 站点的 latest.json。</summary>
    public IUpdateFeed UpdateFeed { get; } = new HttpUpdateFeed();

    /// <summary>选平台实现、定数据和日志目录、找内置角色包和 ffmpeg。找不到内置角色时抛异常（程序没法跑）。</summary>
    public static Composition Create()
    {
        var platform = PlatformFactory.Create();
        var paths = platform.Paths;
        PetLog.UseDirectory(paths.LogDirectory);
        SettingsStore.UseDirectory(paths.DataDirectory);

        var overrideVideos = Environment.GetEnvironmentVariable("ASTRA_PET_VIDEOS") is { Length: > 0 } v ? v : null;
        var catalog = new PetCatalog(ResolvePetsDir(paths, overrideVideos is not null),
            Path.Combine(paths.DataDirectory, "characters"), paths.CacheDirectory, overrideVideos);
        var composition = new Composition(platform, catalog, ResolveFfmpeg(paths));
        PetLog.Write($"Astra Pet {AppVersion.Current} · {platform.Identity}");
        PetLog.Write($"内置角色包: {ResolvePetsDir(paths, true)}" + (overrideVideos is null ? "" : $"（默认角色换成了 {overrideVideos}）"));
        PetLog.Write($"ffmpeg: {composition.Ffmpeg}");
        return composition;
    }

    /// <summary>
    /// 读设置、按设置选的角色加载素材库（每段只解一帧，确认解码器和 alpha 没问题），建好动画状态机。
    /// 选的角色坏了会退回内置角色；内置角色也加载不了时抛异常。
    /// </summary>
    public (PetSettings Settings, PetDirector Director) LoadPet()
    {
        var settings = SettingsStore.Load();
        PetLog.Write($"设置文件: {SettingsStore.FilePath}");
        var library = Catalog.LoadSelected(settings, Decoder);
        PetLog.Write($"加载完成: {library.ClipCount} 段, 动作 {library[PetLibrary.Click].Count} 个, " +
                     $"移动 {library[PetLibrary.Move].Count} 个, 唱歌 {library[PetLibrary.Sing].Count} 个");
        return (settings, new PetDirector(library, settings));
    }

    /// <summary>
    /// 内置角色包的目录：程序的资源目录下的 Assets/pets（macOS 的 .app 里在 Contents/Resources），
    /// 开发运行时找源码树里的 src/Assets/pets。<paramref name="optional"/> 为真时（默认角色被环境变量换掉了）找不到也不报错。
    /// </summary>
    private static string ResolvePetsDir(IAppPaths paths, bool optional)
    {
        var candidates = new[]
        {
            Path.Combine(paths.ResourceDirectory, "Assets", "pets"),
            // 开发运行：src/AstraPet.App/bin/<配置>/<框架>/ 往上四层是 src/，共享素材在 src/Assets。
            Path.GetFullPath(Path.Combine(paths.AppDirectory, "..", "..", "..", "..", "Assets", "pets")),
        };
        return candidates.FirstOrDefault(Directory.Exists)
               ?? (optional ? candidates[0] : throw new DirectoryNotFoundException(
                   "找不到内置角色包（Assets/pets/*.astrapet）。用 tools/pets/build.sh 生成，或设置 ASTRA_PET_VIDEOS 指向素材目录。"));
    }

    /// <summary>
    /// 环境变量 FFMPEG 优先；其次是官方发布包放在程序旁边的精简版（见 tools/ffmpeg/build.sh，版本固定，一定带 libvpx-vp9）；
    /// 再其次是系统里的。
    /// </summary>
    private static string ResolveFfmpeg(IAppPaths paths) =>
        Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } fromEnv ? fromEnv : paths.FindTool("ffmpeg");
}
