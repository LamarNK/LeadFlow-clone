using PuppeteerSharp;

namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Живое CDP-соединение с браузером Multilogin X.
/// </summary>
public interface IMultiloginConnectedBrowser
{
    bool IsConnected { get; }

    IBrowser Browser { get; }

    Task EnsureResponsiveAsync(CancellationToken cancellationToken = default);

    void Disconnect();
}
