using client.Services;
using Xunit;

namespace client.Tests;

public class WebBlockHostMatchTests
{
    [Theory]
    [InlineData("youtube.com", "youtube.com", true)]
    [InlineData("www.youtube.com", "youtube.com", true)]
    [InlineData("m.youtube.com", "youtube.com", true)]
    [InlineData("notyoutube.com", "youtube.com", false)]
    [InlineData("google.com", "youtube.com", false)]
    public void HostMatches_suffix_rules(string host, string blocked, bool expect)
    {
        var ok = WebBlockUxService.HostMatches(host, new[] { blocked }, out var matched);
        Assert.Equal(expect, ok);
        if (expect) Assert.Equal(blocked, matched);
    }

    [Fact]
    public void FindDomainInText_picks_blocked_host_from_error_title()
    {
        var hit = WebBlockUxService.FindDomainInText(
            "www.deepseek.com - This site can’t be reached",
            new[] { "deepseek.com", "youtube.com" });
        Assert.Equal("deepseek.com", hit);
    }
}
