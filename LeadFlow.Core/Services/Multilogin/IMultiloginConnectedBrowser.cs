namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Живое CDP-соединение с браузером Multilogin X. Не раскрывает AdsPower-типы.
/// </summary>
public interface IMultiloginConnectedBrowser
{
    bool IsConnected { get; }

    Task EnsureResponsiveAsync(CancellationToken cancellationToken = default);

    void Disconnect();
}
