using System.Threading.Channels;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public interface IWorkerRealtimeChannel
{
    bool IsConnected { get; }

    ChannelReader<bool> WakeReader { get; }

    event Action<string>? CommandReceived;

    event Action? ConfigChanged;

    event Action<WorkerPendingCaptchaSessionDto>? CaptchaSessionReceived;

    event Action<WorkerPendingBrowserMonitorSessionDto>? BrowserMonitorSessionReceived;

    event Action<WorkerPendingLocalChromeLoginDto>? LocalChromeLoginSessionReceived;

    void RequestWake();

    Task<bool> TrySendHeartbeatAsync(WorkerHeartbeatRequest heartbeat, CancellationToken ct);

    Task TryAckCommandAsync(string command, CancellationToken ct);
}