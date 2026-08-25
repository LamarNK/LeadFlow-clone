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
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return new(port, $"http://127.0.0.1:{port}", null);
    }

    public static MultiloginBrowserStartResult FromWebSocket(string webSocketDebuggerUrl, int? port = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketDebuggerUrl);
        var browserUrl = port is > 0 and <= 65535 ? $"http://127.0.0.1:{port}" : null;
        return new(port, browserUrl, webSocketDebuggerUrl.Trim());
    }
}
