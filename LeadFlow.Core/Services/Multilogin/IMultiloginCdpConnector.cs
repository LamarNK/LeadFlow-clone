namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Запуск профиля Multilogin X, CDP-подключение и гарантированный stop.
/// </summary>
public interface IMultiloginCdpConnector
{
    Task<T> RunAsync<T>(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        Func<IMultiloginConnectedBrowser, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}
