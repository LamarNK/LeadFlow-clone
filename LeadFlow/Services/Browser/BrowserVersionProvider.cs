using System;
using Microsoft.Web.WebView2.Core;

namespace LeadFlow.Services.Browser;

public static class BrowserVersionProvider
{
    private const string FallbackVersion = "125.0.0.0";
    private const string FallbackChromiumMajor = "125";
    public const string CurrentBrowserName = "Edge";

    public static string GetCurrentBrowserVersionString()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return FallbackVersion;
            }

            var token = version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return string.IsNullOrWhiteSpace(token) ? FallbackVersion : token;
        }
        catch
        {
            return FallbackVersion;
        }
    }

    public static string GetCurrentChromiumMajorString()
    {
        var version = GetCurrentBrowserVersionString();
        var dotIndex = version.IndexOf('.');
        if (dotIndex > 0)
        {
            return version[..dotIndex];
        }

        return int.TryParse(version, out _) ? version : FallbackChromiumMajor;
    }
}
