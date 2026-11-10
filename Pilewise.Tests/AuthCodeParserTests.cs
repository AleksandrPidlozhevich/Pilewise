using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class EpicAuthCodeParserTests
{
    [Fact]
    public void Extract_FromRedirectJson_ReturnsCode()
    {
        var code = "0123456789abcdef0123456789abcdef";
        Assert.Equal(code, EpicAuthCodeParser.Extract($"{{\"redirectUrl\":\"x\",\"authorizationCode\":\"{code}\"}}"));
    }

    [Fact]
    public void Extract_FromAuthorizationCodeFragment_ReturnsCode()
    {
        var code = "ABCDEF0123456789abcdef0123456789";
        Assert.Equal(code, EpicAuthCodeParser.Extract($"junk \"authorizationCode\":\"{code}\" junk"));
    }

    [Fact]
    public void Extract_FromBare32HexCode_ReturnsCode()
    {
        var code = "0123456789abcdef0123456789abcdef";
        Assert.Equal(code, EpicAuthCodeParser.Extract(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a code")]
    [InlineData("too-short-1234")]
    public void Extract_FromGarbage_ReturnsNull(string? input)
    {
        Assert.Null(EpicAuthCodeParser.Extract(input));
    }
}

public class GogAuthCodeParserTests
{
    [Fact]
    public void Extract_FromRedirectUrl_ReturnsCode()
    {
        var url = "https://embed.gog.com/on_login_success?origin=client&code=abcDEF123456_-xyz";
        Assert.Equal("abcDEF123456_-xyz", GogAuthCodeParser.Extract(url));
    }

    [Fact]
    public void Extract_FromRedirectUrl_StopsAtNextQueryParam()
    {
        var url = "https://embed.gog.com/on_login_success?code=abc123XYZ&state=foo";
        Assert.Equal("abc123XYZ", GogAuthCodeParser.Extract(url));
    }

    [Fact]
    public void Extract_FromBareToken_ReturnsToken()
    {
        Assert.Equal("abcDEF1234567890", GogAuthCodeParser.Extract("abcDEF1234567890"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces in it")]
    [InlineData("short")]
    public void Extract_FromGarbage_ReturnsNull(string? input)
    {
        Assert.Null(GogAuthCodeParser.Extract(input));
    }
}

public class XboxAuthCodeParserTests
{
    [Fact]
    public void Extract_FromImplicitRedirectWithAccessToken_ReturnsWholeUrl()
    {
        var url = "https://login.live.com/oauth20_desktop.srf#access_token=EwAA&refresh_token=M.R3";
        Assert.Equal(url, XboxAuthCodeParser.Extract(url));
    }

    [Fact]
    public void Extract_TrimsSurroundingWhitespace()
    {
        var url = "https://login.live.com/oauth20_desktop.srf#access_token=EwAA";
        Assert.Equal(url, XboxAuthCodeParser.Extract($"  {url}  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://login.live.com/oauth20_desktop.srf#error=access_denied")]
    public void Extract_WithoutAccessToken_ReturnsNull(string? input)
    {
        Assert.Null(XboxAuthCodeParser.Extract(input));
    }
}