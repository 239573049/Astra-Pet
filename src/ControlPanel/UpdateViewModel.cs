using System.ComponentModel;
using System.Diagnostics;
using AstraPet.Core;
using AstraPet.Pet;

namespace AstraPet.ControlPanel;

/// <summary>控制台「常规 → 更新」一栏：当前版本、检查更新、下载新版本。</summary>
public sealed class UpdateViewModel : Observable
{
    private readonly PetCompanion _companion;
    private UpdateChecker Checker => _companion.Updates;

    public UpdateViewModel(PetCompanion companion, PetSettings settings)
    {
        _companion = companion;
        Settings = settings;
        Checker.PropertyChanged += OnCheckerChanged;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PetSettings.UpdateChannel)) Raise(nameof(UsePreview));
            if (e.PropertyName is nameof(PetSettings.UpdateServer)) Raise(nameof(HasServer));
        };
    }

    public PetSettings Settings { get; }

    public string CurrentVersion => $"{AppVersion.Current} · {AppVersion.Platform}";
    public string Status => Checker.State == UpdateState.Unknown
        ? (HasServer ? "还没有检查过更新" : "还没有配置更新服务器，在下面填一个")
        : Checker.Message;
    public string CheckedAt => Checker.CheckedAt is { } t ? $"上次检查 {t:MM-dd HH:mm}" : "";
    public bool HasChecked => Checker.CheckedAt is not null;
    public bool Checking => Checker.State == UpdateState.Checking;
    public bool CanCheck => !Checking;
    public bool HasServer => Checker.Server.Length > 0;

    public bool HasUpdate => Checker.HasUpdate;
    public string NewVersion => Checker.Available is { } a ? $"新版本 {a.Version}" : "";
    public string NewNotes => Checker.Available?.Notes ?? "";
    public bool HasNotes => NewNotes.Length > 0;

    /// <summary>接收预览版：对应设置里的更新渠道。</summary>
    public bool UsePreview
    {
        get => Settings.UpdateChannel == "preview";
        set => Settings.UpdateChannel = value ? "preview" : "stable";
    }

    public async Task CheckAsync() => await _companion.CheckForUpdateAsync(automatic: false);

    /// <summary>用浏览器打开下载链接（压缩包），没有本机平台的文件就打开下载页。</summary>
    public void OpenDownload()
    {
        if (Checker.Available is not { } info) return;
        var url = info.DownloadUrl.Length > 0 ? info.DownloadUrl : info.PageUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { PetLog.Write($"打开下载链接失败: {e.Message}"); }
    }

    private void OnCheckerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 检查在线程池里发请求，但结果都是在 UI 线程上写回来的（await 之后），可以直接通知。
        Raise(nameof(Status));
        Raise(nameof(CheckedAt));
        Raise(nameof(HasChecked));
        Raise(nameof(Checking));
        Raise(nameof(CanCheck));
        Raise(nameof(HasUpdate));
        Raise(nameof(NewVersion));
        Raise(nameof(NewNotes));
        Raise(nameof(HasNotes));
        Raise(nameof(HasServer));
    }
}
