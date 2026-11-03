using System.Net.Http.Headers;

namespace AstraPet.Core;

/// <summary>
/// 应用内更新的下载部分：把安装包下到临时目录。安装由各平台的 IUpdateInstaller 负责。
/// 完整性靠 HTTPS 和文件大小校验（站点的 latest.json 没有哈希）。
/// </summary>
public static class UpdateDownloader
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>下载安装包到临时目录，返回本地路径。progress 取 0~1，大小未知时不报告。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AstraPet-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 旧文件被占用就算了 */ }
        Directory.CreateDirectory(dir);

        var name = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
        if (string.IsNullOrEmpty(name)) name = "AstraPet-" + info.Version + "." + info.Kind;
        var path = Path.Combine(dir, name);
        var part = path + ".part";

        using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Size;

        long done = 0;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(1.0, (double)done / total));
            }
        }
        if (total > 0 && done != total) throw new IOException($"下载不完整（{done}/{total} 字节）");
        File.Move(part, path, overwrite: true);
        return path;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AstraPet", AppVersion.Current));
        return http;
    }
}
