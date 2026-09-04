using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerHubConnection(
    WorkerCredentials credentials,
    WorkerRuntimeState runtimeState) : BackgroundService, IWorkerRealtimeChannel
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _wakeChannel = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _connectionSync = new();
    private HubConnection? _connection;

    public bool IsConnected
    {
        get
        {
            lock (_connectionSync)
            {
                return _connection?.State == HubConnectionState.Connected;
            }
        }
    }

    public ChannelReader<bool> WakeReader => _wakeChannel.Reader;

    public event Action<string>? CommandReceived;

    public event Action? ConfigChanged;

    public event Action<WorkerPendingCaptchaSessionDto>? CaptchaSessionReceived;

    public event Action<WorkerPendingBrowserMonitorSessionDto>? BrowserMonitorSessionReceived;

    public event Action<WorkerPendingLocalChromeLoginDto>? LocalChromeLoginSessionReceived;

    public event Action<WorkerPendingTopUpSessionDto>? TopUpSessionReceived;

    public void RequestWake() => _wakeChannel.Writer.TryWrite(true);

    public async Task TryAckCommandAsync(string command, CancellationToken ct)
    {
        HubConnection? connection;
        lock (_connectionSync)
        {
            connection = _connection;
        }

        if (connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await connection
                .InvokeAsync(WorkerHubMethods.AckCommand, command, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Config poll will consume the pending command as fallback.
        }
    }

    public async Task<bool> TrySendHeartbeatAsync(WorkerHeartbeatRequest heartbeat, CancellationToken ct)
    {
        HubConnection? connection;
        lock (_connectionSync)
        {
            connection = _connection;
        }

        if (connection?.State != HubConnectionState.Connected)
        {
            return false;
        }

        try
        {
            await connection
                .InvokeAsync(WorkerHubMethods.Heartbeat, heartbeat, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(stoppingToken).ConfigureAwait(false);
                await WaitForDisconnectAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WorkerConnectionErrors.ApplyHubFailure(runtimeState, ex);
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
        }

        await DisposeConnectionAsync().ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        HubConnection? connection;
        lock (_connectionSync)
        {
            connection = _connection;
        }

        if (connection?.State == HubConnectionState.Connected)
        {
            return;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        connection = BuildHubConnection(credentials.ApiKey);
        WireHandlers(connection);

        lock (_connectionSync)
        {
            _connection = connection;
        }

        await connection.StartAsync(ct).ConfigureAwait(false);
        await connection.InvokeAsync(
            WorkerHubMethods.Register,
            new WorkerHubRegisterRequest(
                ApplicationVersionProvider.GetVersion(),
                runtimeState.Status,
                runtimeState.Detail),
            ct).ConfigureAwait(false);

        RequestWake();
    }

    private async Task WaitForDisconnectAsync(CancellationToken ct)
    {
        HubConnection? connection;
        lock (_connectionSync)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void WireHandlers(HubConnection connection)
    {
        connection.On<WorkerPushCommandMessage>(WorkerHubEvents.ExecuteCommand, message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Command))
            {
                CommandReceived?.Invoke(message.Command);
            }

            return Task.CompletedTask;
        });

        connection.On<WorkerConfigChangedMessage>(WorkerHubEvents.ConfigChanged, _ =>
        {
            ConfigChanged?.Invoke();
            RequestWake();
            return Task.CompletedTask;
        });

        connection.On<WorkerPendingCaptchaSessionDto>(WorkerHubEvents.CaptchaSession, session =>
        {
            CaptchaSessionReceived?.Invoke(session);
            RequestWake();
            return Task.CompletedTask;
        });

        connection.On<WorkerPendingBrowserMonitorSessionDto>(WorkerHubEvents.BrowserMonitorSession, session =>
        {
            BrowserMonitorSessionReceived?.Invoke(session);
            RequestWake();
            return Task.CompletedTask;
        });

        connection.On<WorkerPendingLocalChromeLoginDto>(WorkerHubEvents.LocalChromeLoginSession, session =>
        {
            LocalChromeLoginSessionReceived?.Invoke(session);
            RequestWake();
            return Task.CompletedTask;
        });

        connection.On<WorkerPendingTopUpSessionDto>(WorkerHubEvents.TopUpSession, session =>
        {
            TopUpSessionReceived?.Invoke(session);
            RequestWake();
            return Task.CompletedTask;
        });

        connection.On("ConnectionReplaced", () =>
        {
            RequestWake();
            return Task.CompletedTask;
        });

        connection.Reconnecting += _ =>
        {
            RequestWake();
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            _ = ReRegisterAfterReconnectAsync(connection);
            return Task.CompletedTask;
        };
    }

    private async Task ReRegisterAfterReconnectAsync(HubConnection connection)
    {
        try
        {
            await connection.InvokeAsync(
                WorkerHubMethods.Register,
                new WorkerHubRegisterRequest(
                    ApplicationVersionProvider.GetVersion(),
                    runtimeState.Status,
                    runtimeState.Detail)).ConfigureAwait(false);
            RequestWake();
        }
        catch
        {
            // Full reconnect loop in ExecuteAsync will recover.
        }
    }

    private HubConnection BuildHubConnection(string apiKey)
    {
        var baseUrl = credentials.ApiBaseUrl.TrimEnd('/');
        return new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/worker", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(apiKey);
                options.Headers["Authorization"] = $"Bearer {apiKey}";
            })
            .WithAutomaticReconnect()
            .Build();
    }

    private async Task DisposeConnectionAsync()
    {
        HubConnection? connection;
        lock (_connectionSync)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}