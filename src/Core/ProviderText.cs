namespace AstraPet.Core;

/// <summary>提供商在界面上展示用的几个小转换：主机名、协议简称、遮盖后的 Key。</summary>
public static class ProviderText
{
    /// <summary>接口地址的主机部分（带非默认端口），解析不了就原样返回。</summary>
    public static string Host(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "未填写地址";
        var text = baseUrl.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return text;
    }

    /// <summary>本机地址（localhost、127.x、::1）：这类接口通常不需要 API Key。</summary>
    public static bool IsLocal(string baseUrl)
    {
        var host = Host(baseUrl);
        return host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
               || host.StartsWith("127.", StringComparison.Ordinal)
               || host.StartsWith("[::1]", StringComparison.Ordinal);
    }

    public static string ProtocolShort(LlmProtocol protocol) => protocol switch
    {
        LlmProtocol.Responses => "OpenAI Responses",
        LlmProtocol.Anthropic => "Anthropic Messages",
        _ => "OpenAI Chat",
    };

    /// <summary>sk-abc…744f 这种：留头三位和末四位，太短的全遮住。</summary>
    public static string MaskKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0) return "";
        return key.Length <= 10 ? new string('•', key.Length) : $"{key[..3]}…{key[^4..]}";
    }

    /// <summary>比较地址用：去掉首尾空白和结尾的斜杠。</summary>
    public static string TrimUrl(string url) => url.Trim().TrimEnd('/');
}
