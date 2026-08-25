namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Результат запуска профиля Multilogin X. Документированный launcher возвращает <c>data.port</c>;
/// websocket может появиться в реальном контракте позже.
/// </summary>
public sealed record MultiloginBrowserStartResult(
    int? Port,
    string? BrowserUrl,
    string? WebSocketDebuggerUrl)
{
    public static MultiloginBrowserStartResult FromPort(int port)
    {
        EnsureValidPort(port);
        return new(port, BuildLocalBrowserUrl(port), null);
    }

    public static MultiloginBrowserStartResult FromWebSocket(string webSocketDebuggerUrl, int? port = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketDebuggerUrl);
        if (port is not null)
        {
            EnsureValidPort(port.Value);
        }

        var browserUrl = port is null ? null : BuildLocalBrowserUrl(port.Value);
        return new(port, browserUrl, webSocketDebuggerUrl.Trim());
    }

    private static void EnsureValidPort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
    }

    private static string BuildLocalBrowserUrl(int port) => $"http://127.0.0.1:{port}";
}
