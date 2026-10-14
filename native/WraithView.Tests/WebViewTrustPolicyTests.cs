namespace WraithView.Tests;

public class WebViewTrustPolicyTests
{
    [Fact]
    public void ReleaseBridgeAcceptsOnlyTheVirtualHostOrigin()
    {
        var policy = new WebViewTrustPolicy("app.wraithview.local", null);

        Assert.True(policy.IsBridgeSource("https://app.wraithview.local/index.html"));
        Assert.False(policy.IsBridgeSource("http://app.wraithview.local/index.html"));
        Assert.False(policy.IsBridgeSource("https://app.wraithview.local.evil.invalid/"));
        Assert.False(policy.IsBridgeSource("https://user@app.wraithview.local/"));
        Assert.False(policy.IsBridgeSource("https://localhost:5178/"));
    }

    [Fact]
    public void DevelopmentBridgeUsesOnlyTheConfiguredOrigin()
    {
        var policy = new WebViewTrustPolicy("app.wraithview.local", "http://localhost:5178/ui/index.html");

        Assert.True(policy.IsBridgeSource("http://localhost:5178/other"));
        Assert.False(policy.IsBridgeSource("http://127.0.0.1:5178/"));
        Assert.False(policy.IsBridgeSource("http://localhost:5179/"));
    }

    [Theory]
    [InlineData("https://app.wraithview.local/index.html", "Internal")]
    [InlineData("https://example.com/docs", "External")]
    [InlineData("http://example.com/docs", "External")]
    [InlineData("https://user@example.com/docs", "Rejected")]
    [InlineData("file:///C:/Windows/win.ini", "Rejected")]
    [InlineData("mailto:test@example.com", "Rejected")]
    [InlineData("javascript:alert(1)", "Rejected")]
    public void NavigationClassificationIsNarrow(string target, string expected)
        => Assert.Equal(expected, new WebViewTrustPolicy("app.wraithview.local", null).ClassifyNavigation(target).ToString());
}
