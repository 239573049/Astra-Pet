namespace AstraPet.Platform;

/// <summary>程序和数据放在哪。</summary>
public interface IAppPaths
{
    /// <summary>设置、角色目录、模型库缓存。环境变量 ASTRA_PET_DATA 可以换成别的目录（测试时用）。</summary>
    string DataDirectory { get; }

    /// <summary>
    /// 日志文件目录：macOS 在 ~/Library/Logs/AstraPet，Windows 在 %LOCALAPPDATA%\AstraPet\logs，
    /// Linux 在 $XDG_STATE_HOME/AstraPet。设了 ASTRA_PET_DATA 时放在数据目录下的 logs 里。
    /// </summary>
    string LogDirectory { get; }

    /// <summary>程序所在目录。macOS 的 .app 里是 Contents/MacOS。</summary>
    string AppDirectory { get; }

    /// <summary>素材、许可文件等非代码文件所在目录。macOS 的 .app 里是 Contents/Resources，其他情况同 <see cref="AppDirectory"/>。</summary>
    string ResourceDirectory { get; }

    /// <summary>
    /// 找一个外部工具（如 ffmpeg）：先找程序旁边随包带的，再找系统里的常见位置，都没有就返回裸名字交给 PATH。
    /// <paramref name="name"/> 不带扩展名，Windows 上会自动补 .exe。
    /// </summary>
    string FindTool(string name);
}

/// <summary>交给系统打开的东西：网页、文件夹。</summary>
public interface IShell
{
    /// <summary>用默认浏览器打开。失败只记日志，返回是否成功。</summary>
    bool OpenUrl(string url);

    /// <summary>用系统的文件管理器打开目录，不存在就先建出来。</summary>
    bool OpenFolder(string path);
}

/// <summary>登录系统时自动启动。</summary>
public interface IAutoStart
{
    /// <summary>系统里实际有没有自启项（以系统为准，用户可能在系统设置里手动删了）。</summary>
    bool IsEnabled();

    /// <summary>
    /// 写入或删除自启项。<paramref name="environment"/> 是自启时要带上的环境变量
    /// （素材目录、ffmpeg 路径），让自启的那次和这次用同样的素材；平台不支持带环境变量时忽略。
    /// 失败只记日志，返回是否成功。
    /// </summary>
    bool Apply(bool enabled, IReadOnlyDictionary<string, string> environment);
}

/// <summary>一个系统语音。</summary>
public sealed record VoiceOption(string Name, string Language)
{
    public static readonly VoiceOption SystemDefault = new("", "");

    public override string ToString() => Name.Length == 0 ? "系统默认" : $"{Name}（{Language}）";
}

/// <summary>朗读参数，单位统一，各平台自己换算。</summary>
/// <param name="Voice">系统语音名，空串表示系统默认。</param>
/// <param name="WordsPerMinute">语速，每分钟字数，200 左右是正常语速。</param>
/// <param name="Volume">音量 0~1。</param>
public sealed record SpeechOptions(string Voice, int WordsPerMinute, double Volume);

/// <summary>文字转语音。同一时刻只念一句，新的一句会打断上一句。</summary>
public interface ISpeech
{
    /// <summary>列出系统语音，第一项是「系统默认」，中文排前面。会起子进程，在后台线程上跑。</summary>
    Task<IReadOnlyList<VoiceOption>> ListVoicesAsync();

    void Speak(string text, SpeechOptions options);

    void Stop();
}

/// <summary>读系统状态。</summary>
public interface ISystemMonitor
{
    /// <summary>距离上一次键盘或鼠标输入多久。拿不到时返回 null，调用方不能把它当成 0。</summary>
    TimeSpan? IdleTime();

    /// <summary>是不是有声音在播放。可能要起子进程，不要在 UI 线程上同步等。拿不到时返回 false。</summary>
    Task<bool> IsAudioPlayingAsync();
}

/// <summary>应用内更新的安装部分（下载在 Core 的 UpdateDownloader 里）。</summary>
public interface IUpdateInstaller
{
    /// <summary>本平台能用的安装包类型，按优先级。更新检测按这个顺序挑文件。</summary>
    IReadOnlyList<string> PreferredKinds { get; }

    /// <summary>这种安装包能不能在应用内装。不能装的（zip 等）退回浏览器下载。</summary>
    bool CanInstall(string kind);

    /// <summary>
    /// 安装已下载的安装包。成功返回 null，调用方随后应立刻退出程序（替换和重启由已经起好的后台进程完成）；
    /// 失败返回给人看的原因，程序继续运行。不抛异常。
    /// </summary>
    Task<string?> InstallAsync(string file, string kind);
}

/// <summary>
/// 对一个已经打开的窗口做 Avalonia 管不到的事。所有方法在不支持的平台上都是空操作。
/// </summary>
public interface INativeWindow
{
    /// <summary>为真时点击、滚轮、悬停全部落到下面的窗口。</summary>
    bool IgnoresMouse { get; set; }

    /// <summary>当前光标相对窗口内容区左上角的位置（DIP）。窗口忽略鼠标时也要读得到。拿不到返回 null。</summary>
    (double X, double Y)? CursorInWindow();

    /// <summary>补上窗口透明（绕过个别平台后端的问题）。不需要的平台是空操作。</summary>
    void MakeTransparent();

    /// <summary>允许录屏软件捕获这个窗口。</summary>
    void SetCapturable(bool capturable);

    /// <summary>为真时宠物也出现在其他应用的全屏空间里，为假时全屏应用在前台时看不到宠物。</summary>
    void SetOverFullscreen(bool over);

    /// <summary>整个窗口（含原生视图）的不透明度，预热时用来把窗口整个藏起来。</summary>
    void SetAlpha(double alpha);
}

/// <summary>拿不到原生窗口时用的空实现（窗口还没打开、平台不支持）。</summary>
public sealed class NullNativeWindow : INativeWindow
{
    public static readonly NullNativeWindow Instance = new();

    private NullNativeWindow() { }

    public bool IgnoresMouse { get => false; set { } }
    public (double X, double Y)? CursorInWindow() => null;
    public void MakeTransparent() { }
    public void SetCapturable(bool capturable) { }
    public void SetOverFullscreen(bool over) { }
    public void SetAlpha(double alpha) { }
}
