namespace AstraPet.Platform;

// 不支持的平台上各服务的空实现：调用方不用判空，能力描述里对应的开关是 false。

internal sealed class UnsupportedAutoStart : IAutoStart
{
    public bool IsEnabled() => false;
    public bool Apply(bool enabled, IReadOnlyDictionary<string, string> environment) => false;
}

internal sealed class UnsupportedSpeech : ISpeech
{
    public Task<IReadOnlyList<VoiceOption>> ListVoicesAsync() =>
        Task.FromResult<IReadOnlyList<VoiceOption>>([VoiceOption.SystemDefault]);
    public void Speak(string text, SpeechOptions options) { }
    public void Stop() { }
}

internal sealed class UnsupportedMonitor : ISystemMonitor
{
    public TimeSpan? IdleTime() => null;
    public Task<bool> IsAudioPlayingAsync() => Task.FromResult(false);
}

internal sealed class UnsupportedInstaller : IUpdateInstaller
{
    public IReadOnlyList<string> PreferredKinds { get; } = ["zip"];
    public bool CanInstall(string kind) => false;
    public Task<string?> InstallAsync(string file, string kind) => Task.FromResult<string?>("当前系统不支持应用内更新");
}
