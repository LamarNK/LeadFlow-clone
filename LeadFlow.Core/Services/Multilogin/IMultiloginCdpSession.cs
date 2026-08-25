namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Открытая Multilogin-сессия: disconnect + StopProfile при Dispose.
/// </summary>
public interface IMultiloginCdpSession : IAsyncDisposable
{
    IMultiloginConnectedBrowser Connected { get; }
}
