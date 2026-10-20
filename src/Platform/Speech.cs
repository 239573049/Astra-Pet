using System.Diagnostics;
using System.Text.RegularExpressions;
using AstraPet.Core;

namespace AstraPet.Platform;

/// <summary>一个系统语音。</summary>
public sealed record VoiceOption(string Name, string Language)
{
    public override string ToString() => Name.Length == 0 ? "系统默认" : $"{Name}（{Language}）";
}

/// <summary>
/// 文字转语音，用系统自带的引擎：macOS 调 say，Windows 调 PowerShell 的 System.Speech。
/// 同一时刻只念一句，新的一句会打断上一句。
/// </summary>
public static partial class Speech
{
    private static Process? _current;

    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>列出系统语音，中文排前面。第一项是「系统默认」。</summary>
    public static IReadOnlyList<VoiceOption> ListVoices()
    {
        var voices = new List<VoiceOption> { new("", "") };
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // 每行形如 "Tingting             zh_CN    # 你好！我叫婷婷。"，名字里可能有空格。
                foreach (var line in Run("/usr/bin/say", ["-v", "?"]).Split('\n'))
                    if (VoiceLine().Match(line) is { Success: true } m)
                        voices.Add(new VoiceOption(m.Groups[1].Value.Trim(), m.Groups[2].Value));
            }
            else if (OperatingSystem.IsWindows())
            {
                const string script = "Add-Type -AssemblyName System.Speech;" +
                    "(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices()|" +
                    "%{$_.VoiceInfo.Name+'|'+$_.VoiceInfo.Culture}";
                foreach (var line in Run("powershell", ["-NoProfile", "-Command", script]).Split('\n'))
                    if (line.Split('|') is [var name, var culture])
                        voices.Add(new VoiceOption(name.Trim(), culture.Trim()));
            }
        }
        catch (Exception e)
        {
            PetLog.Write($"读取系统语音失败: {e.Message}");
        }
        return voices
            .OrderBy(v => v.Name.Length == 0 ? 0 : v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(v => v.Name)
            .ToList();
    }

    public static void Speak(string text, string voice, int rate, double volume)
    {
        Stop();
        text = text.Trim();
        if (text.Length == 0 || !IsSupported) return;
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("/usr/bin/say") { UseShellExecute = false };
                if (voice.Length > 0) { info.ArgumentList.Add("-v"); info.ArgumentList.Add(voice); }
                info.ArgumentList.Add("-r");
                info.ArgumentList.Add(rate.ToString());
                // say 没有音量参数，用内嵌命令 [[volm x]]。
                info.ArgumentList.Add($"[[volm {volume:0.00}]] {text}");
            }
            else
            {
                // System.Speech 的 Rate 是 -10..10，按 200 字/分钟 为 0 粗略换算。
                var winRate = Math.Clamp((rate - 200) / 20, -10, 10);
                var escaped = text.Replace("'", "''");
                var select = voice.Length > 0 ? $"$s.SelectVoice('{voice.Replace("'", "''")}');" : "";
                info = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add("Add-Type -AssemblyName System.Speech;" +
                    "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                    $"{select}$s.Rate={winRate};$s.Volume={(int)(volume * 100)};$s.Speak('{escaped}')");
            }
            _current = Process.Start(info);
        }
        catch (Exception e)
        {
            PetLog.Write($"朗读失败: {e.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            if (_current is { HasExited: false }) _current.Kill();
        }
        catch { }
        _current?.Dispose();
        _current = null;
    }

    private static string Run(string file, string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    [GeneratedRegex(@"^(.+?)\s+([a-z]{2,3}_[A-Z0-9]{2,3})\s+#")]
    private static partial Regex VoiceLine();
}
