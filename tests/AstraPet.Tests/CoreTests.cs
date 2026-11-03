using AstraPet.Core;

namespace AstraPet.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.2.0", "0.2.0-preview.1")]           // 正式版高于它的预览版
    [InlineData("0.2.0-preview.10", "0.2.0-preview.2")] // 预览号按数值比
    [InlineData("v0.3.0", "0.2.9")]                     // 允许 v 前缀
    [InlineData("0.2.0", "not-a-version")]              // 解析不了的排在最后
    public void Newer_version_compares_greater(string newer, string older)
    {
        Assert.True(SemVer.Compare(newer, older) > 0);
        Assert.True(SemVer.Compare(older, newer) < 0);
    }

    [Theory]
    [InlineData("0.1.3", "0.1.3")]
    [InlineData("0.1.3+abc123", "0.1.3")]   // SDK 附加的提交号不参与比较
    [InlineData("1.2", "1.2.0")]
    public void Equal_versions_compare_zero(string a, string b) => Assert.Equal(0, SemVer.Compare(a, b));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    public void Rejects_malformed(string text) => Assert.False(SemVer.TryParse(text, out _));
}

public class ProviderTextTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com/v1")]
    [InlineData("https://api.openai.com/v1/responses/", "https://api.openai.com/v1")]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com")]
    [InlineData("  http://localhost:11434/v1/  ", "http://localhost:11434/v1")]
    public void NormalizeEndpoint_strips_known_endpoints(string input, string expected) =>
        Assert.Equal(expected, ProviderText.NormalizeEndpoint(input));

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://api.deepseek.com", false)]
    public void IsLocal_detects_loopback(string url, bool local) => Assert.Equal(local, ProviderText.IsLocal(url));
}
