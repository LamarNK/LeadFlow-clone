using System.Text.Json;
using LeadFlow.Core.Services.Multilogin;

namespace Orbita.Tests;

public sealed class MultiloginConfigurationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("https://launcher.mlx.yt:45001/", "https://launcher.mlx.yt:45001")]
    [InlineData(" https://api.multilogin.com/// ", "https://api.multilogin.com")]
    [InlineData("http://127.0.0.1:45001", "http://127.0.0.1:45001")]
    public void Normalize_StripsWhitespaceAndTrailingSlashes(string? input, string? expected)
    {
        Assert.Equal(expected, MultiloginUrl.Normalize(input));
    }

    [Fact]
    public void ConnectionOptions_Normalized_TrimsTokenAndUrls_WithoutChangingEmptyTokenToPlaceholder()
    {
        var options = new MultiloginConnectionOptions
        {
            LauncherUrl = "https://launcher.mlx.yt:45001/",
            CloudApiUrl = " https://api.multilogin.com/ ",
            AutomationToken = "  tok  "
        }.Normalized();

        Assert.Equal("https://launcher.mlx.yt:45001", options.LauncherUrl);
        Assert.Equal("https://api.multilogin.com", options.CloudApiUrl);
        Assert.Equal("tok", options.AutomationToken);
        Assert.True(options.HasAutomationToken);
    }

    [Fact]
    public void ConnectionOptions_ToString_DoesNotIncludeToken()
    {
        var options = new MultiloginConnectionOptions
        {
            LauncherUrl = "https://launcher.mlx.yt:45001",
            CloudApiUrl = "https://api.multilogin.com",
            AutomationToken = "super-secret-token"
        };

        var text = options.ToString();
        Assert.DoesNotContain("super-secret-token", text, StringComparison.Ordinal);
        Assert.Contains("HasAutomationToken = True", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"AutomationToken = {options.AutomationToken}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionOptions_Json_RoundTripsFields()
    {
        var options = new MultiloginConnectionOptions
        {
            LauncherUrl = "https://launcher.mlx.yt:45001",
            CloudApiUrl = "https://api.multilogin.com",
            AutomationToken = "tok"
        };
        var json = JsonSerializer.Serialize(options);
        var loaded = JsonSerializer.Deserialize<MultiloginConnectionOptions>(json);
        Assert.NotNull(loaded);
        Assert.Equal(options.LauncherUrl, loaded.LauncherUrl);
        Assert.Equal(options.CloudApiUrl, loaded.CloudApiUrl);
        Assert.Equal(options.AutomationToken, loaded.AutomationToken);
    }

    [Fact]
    public void StartResult_FromPort_BuildsLocalBrowserUrl()
    {
        var result = MultiloginBrowserStartResult.FromPort(35001);
        Assert.Equal(35001, result.Port);
        Assert.Equal("http://127.0.0.1:35001", result.BrowserUrl);
        Assert.Null(result.WebSocketDebuggerUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void StartResult_FromPort_RejectsInvalidPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultiloginBrowserStartResult.FromPort(port));
    }

    [Fact]
    public void StartResult_FromWebSocket_KeepsEndpointAndOptionalPort()
    {
        var result = MultiloginBrowserStartResult.FromWebSocket("ws://127.0.0.1:35001/devtools/browser/abc", 35001);
        Assert.Equal(35001, result.Port);
        Assert.Equal("http://127.0.0.1:35001", result.BrowserUrl);
        Assert.Equal("ws://127.0.0.1:35001/devtools/browser/abc", result.WebSocketDebuggerUrl);
    }

    [Fact]
    public void StartResult_FromWebSocket_WithoutPort_LeavesBrowserUrlNull()
    {
        var result = MultiloginBrowserStartResult.FromWebSocket(" ws://127.0.0.1:35001/devtools/browser/abc ");
        Assert.Null(result.Port);
        Assert.Null(result.BrowserUrl);
        Assert.Equal("ws://127.0.0.1:35001/devtools/browser/abc", result.WebSocketDebuggerUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void StartResult_FromWebSocket_RejectsInvalidPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MultiloginBrowserStartResult.FromWebSocket("ws://127.0.0.1:1/devtools/browser/abc", port));
    }

    [Fact]
    public void ApiClientContract_HasStartStopAndSearch()
    {
        var names = typeof(IMultiloginApiClient).GetMethods().Select(static m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(IMultiloginApiClient.StartProfileAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.StopProfileAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.SearchProfilesAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.ProbeLauncherAsync), names);
        Assert.DoesNotContain("ListFoldersAsync", names);
        Assert.DoesNotContain("ListProfilesAsync", names);
    }
}
