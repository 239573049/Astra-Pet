namespace AstraPet.Core;

/// <summary>
/// 版本比较，规则和 LocalGate 站点的 compareVersions 一致：先比数字部分；同一个数字部分下，
/// 正式版高于它的预览版；预览标识里的数字按数值比较（preview.10 &gt; preview.2）。
/// </summary>
public static class SemVer
{
    public static bool TryParse(string? text, out (int Major, int Minor, int Patch, string[] Pre) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] pre = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return false;
        version = (nums[0], nums[1], nums[2], pre);
        return true;
    }

    /// <summary>a 比 b 新返回正数，旧返回负数，相同返回 0。解析不了的版本排在最后。</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var x);
        var okB = TryParse(b, out var y);
        if (!okA || !okB) return okA ? 1 : okB ? -1 : 0;

        var core = x.Major != y.Major ? x.Major.CompareTo(y.Major)
            : x.Minor != y.Minor ? x.Minor.CompareTo(y.Minor)
            : x.Patch.CompareTo(y.Patch);
        if (core != 0) return core;

        if (x.Pre.Length == 0 || y.Pre.Length == 0) return y.Pre.Length.CompareTo(x.Pre.Length);
        for (var i = 0; i < Math.Max(x.Pre.Length, y.Pre.Length); i++)
        {
            if (i >= x.Pre.Length) return -1;
            if (i >= y.Pre.Length) return 1;
            if (x.Pre[i] == y.Pre[i]) continue;
            var nx = int.TryParse(x.Pre[i], out var vx);
            var ny = int.TryParse(y.Pre[i], out var vy);
            if (nx && ny) return vx.CompareTo(vy);
            if (nx != ny) return nx ? -1 : 1;
            return string.CompareOrdinal(x.Pre[i], y.Pre[i]);
        }
        return 0;
    }
}
