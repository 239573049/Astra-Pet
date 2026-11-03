using System.Diagnostics;
using AstraPet.Core;

namespace AstraPet.Platform;

/// <summary>
/// 用系统自带命令行工具朗读的公共部分：同一时刻只有一个朗读进程，新的一句先结束上一句。
/// 子类只管怎么列语音、怎么拼命令。
/// </summary>
internal abstract class ProcessSpeech : ISpeech
{
    private Process? _current;

    /// <summary>读系统语音（不含「系统默认」），在后台线程上调。</summary>
    protected abstract IEnumerable<VoiceOption> ReadVoices();

    protected abstract ProcessStartInfo SpeakCommand(string text, SpeechOptions options);

    public Task<IReadOnlyList<VoiceOption>> ListVoicesAsync() => Task.Run<IReadOnlyList<VoiceOption>>(() =>
    {
        var voices = new List<VoiceOption> { VoiceOption.SystemDefault };
        try { voices.AddRange(ReadVoices()); }
        catch (Exception e) { PetLog.Write($"读取系统语音失败: {e.Message}"); }
        return voices
            .OrderBy(v => v.Name.Length == 0 ? 0 : v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(v => v.Name)
            .ToList();
    });

    public void Speak(string text, SpeechOptions options)
    {
        Stop();
        text = text.Trim();
        if (text.Length == 0) return;
        try { _current = Process.Start(SpeakCommand(text, options)); }
        catch (Exception e) { PetLog.Write($"朗读失败: {e.Message}"); }
    }

    public void Stop()
    {
        var wasSpeaking = false;
        try
        {
            if (_current is { HasExited: false })
            {
                wasSpeaking = true;
                _current.Kill();
            }
        }
        catch { /* 进程刚好自己结束了 */ }
        _current?.Dispose();
        _current = null;
        if (wasSpeaking) OnStop();
    }

    /// <summary>
    /// 打断了一句正在念的话之后调用，在下一句开始之前同步完成。
    /// 朗读由守护进程完成的引擎（speech-dispatcher）要在这里另外发取消，光杀客户端进程停不下来。
    /// </summary>
    protected virtual void OnStop() { }
}

/// <summary>应用内安装的公共部分：类型不对直接拒绝，异常一律转成给人看的原因。</summary>
internal abstract class UpdateInstallerBase : IUpdateInstaller
{
    public abstract IReadOnlyList<string> PreferredKinds { get; }

    /// <summary>能在应用内装的那一种安装包。</summary>
    protected abstract string InstallableKind { get; }

    public bool CanInstall(string kind) => kind == InstallableKind;

    public async Task<string?> InstallAsync(string file, string kind)
    {
        if (!CanInstall(kind)) return "这个安装包不适合当前系统";
        try
        {
            return await InstallCoreAsync(file);
        }
        catch (Exception e)
        {
            PetLog.Write($"安装更新失败: {e}");
            return e.Message;
        }
    }

    protected abstract Task<string?> InstallCoreAsync(string file);
}
