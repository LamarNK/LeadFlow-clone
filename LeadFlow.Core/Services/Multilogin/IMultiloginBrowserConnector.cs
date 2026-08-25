namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Подключение к уже запущенному профилю Multilogin X через CDP.
/// </summary>
public interface IMultiloginBrowserConnector
{
    Task<IMultiloginConnectedBrowser> ConnectAsync(
        string browserUrl,
        string? webSocketDebuggerUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
