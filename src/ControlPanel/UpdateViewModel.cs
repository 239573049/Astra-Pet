using System.ComponentModel;
using System.Diagnostics;
using AstraPet.Core;
using AstraPet.Pet;

namespace AstraPet.ControlPanel;

/// <summary>控制台「常规 → 更新」一栏：当前版本、检查更新、下载并安装新版本。</summary>
public sealed class UpdateViewModel : Observable
{
    private readonly PetCompanion _companion;
    private readonly Action _exit;
    private bool _busy;
    private string _progressText = "";
    private UpdateChecker Checker => _companion.Updates;

    public UpdateViewModel(PetCompanion companion, PetSettings settings, Action exit)
    {
        _exit = exit;
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

    private bool InApp => Checker.Available is { } a && UpdateInstaller.CanInstall(a);
    public bool CanApply => !_busy;
    public string ApplyText => _busy ? (_progressText.Length > 0 ? _progressText : "更新中…") : InApp ? "更新并重启" : "下载更新";
    public string InstallHint => _progressText.Length > 0 && _busy ? "" : InApp
        ? "会在后台下载并安装，完成后自动重启，设置和记忆会保留。"
        : "下载后打开安装包安装即可，设置和记忆会保留。";
    private string _error = "";
    public string InstallError { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasInstallError)); } }
    public bool HasInstallError => _error.Length > 0;

    private void SetProgress(string text)
    {
        _progressText = text;
        Raise(nameof(ApplyText));
        Raise(nameof(InstallHint));
    }

    /// <summary>有本机安装包就应用内下载安装并重启；否则退回浏览器下载。</summary>
    public async Task ApplyAsync()
    {
        if (_busy || Checker.Available is not { } info) return;
        if (!UpdateInstaller.CanInstall(info)) { OpenDownload(); return; }

        _busy = true;
        InstallError = "";
        Raise(nameof(CanApply));
        SetProgress("下载中 0%");
        try
        {
            // 进度回调在下载线程上来，切回 UI 线程再通知界面
            var progress = new Progress<double>(p => SetProgress($"下载中 {(int)(p * 100)}%"));
            var file = await Task.Run(() => UpdateInstaller.DownloadAsync(info, progress, CancellationToken.None));
            SetProgress("正在安装…");
            var error = await UpdateInstaller.InstallAsync(info, file);
            if (error is null)
            {
                SetProgress("即将重启…");
                _exit();
                return;
            }
            InstallError = error;
            PetLog.Write("应用内更新失败: " + error);
        }
        catch (Exception e)
        {
            InstallError = "下载失败：" + e.Message;
            PetLog.Write("应用内更新下载失败: " + e.Message);
        }
        _busy = false;
        SetProgress("");
        Raise(nameof(CanApply));
    }

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
        Raise(nameof(ApplyText));
        Raise(nameof(InstallHint));
    }
}
