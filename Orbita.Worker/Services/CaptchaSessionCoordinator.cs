using System.Threading.Channels;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.Worker;
using Microsoft.AspNetCore.SignalR.Client;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class CaptchaSessionCoordinator(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials,
    CaptchaSessionHost captchaHost,
    IWorkerMonitoringService monitoringService)
{
    private static readonly SemaphoreSlim SessionMutex = new(1, 1);
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;

    public async Task<bool> TryRunPendingSessionAsync(
        WorkerPendingCaptchaSessionDto pending,
        CancellationToken cancellationToken)
    {
        if (!await SessionMutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await CaptchaWorkerLog.WarningAsync(
                "Captcha: сессия уже выполняется, повторный запуск пропущен.",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);
            return false;
        }

        monitoringService.EnterCaptchaHold();
        try
        {
            _isRunning = true;
            await CaptchaWorkerLog.InfoAsync(
                $"Captcha: подхвачена сессия {pending.SessionId}, аккаунт {pending.AccountId}, профиль {pending.AdsPowerProfileId}.",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?>
                {
                    ["captcha.sessionId"] = pending.SessionId,
                    ["captcha.accountId"] = pending.AccountId,
                    ["captcha.pageUrl"] = pending.PageUrl
                }).ConfigureAwait(false);

            var inputChannel = Channel.CreateUnbounded<CaptchaInputPayload>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            using var sessionControlCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string? externalTerminalStatus = null;
            string? externalTerminalMessage = null;

            await using var hubConnection = BuildHubConnection(credentials.ApiKey);
            hubConnection.On<CaptchaInputMessage>("Input", message =>
            {
                if (message.SessionId != pending.SessionId)
                {
                    return Task.CompletedTask;
                }

                inputChannel.Writer.TryWrite(new CaptchaInputPayload(
                    message.SessionId,
                    message.EventType,
                    message.X,
                    message.Y,
                    message.PanelWidth,
                    message.PanelHeight,
                    message.TimestampMs,
                    message.Button,
                    message.Buttons,
                    message.Key,
                    message.Code,
                    message.AltKey,
                    message.CtrlKey,
                    message.ShiftKey,
                    message.MetaKey,
                    message.Repeat));
                return Task.CompletedTask;
            });

            hubConnection.On<CaptchaStateChangedMessage>("StateChanged", message =>
            {
                if (message.SessionId != pending.SessionId)
                {
                    return Task.CompletedTask;
                }

                if (!string.Equals(message.Status, CaptchaSessionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(message.Status, CaptchaSessionStatuses.Expired, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                externalTerminalStatus = message.Status;
                externalTerminalMessage = message.Message;
                sessionControlCts.Cancel();
                return Task.CompletedTask;
            });

            hubConnection.Reconnecting += error =>
            {
                _ = CaptchaWorkerLog.WarningAsync(
                    $"Captcha: hub переподключение — {error?.Message ?? "неизвестная ошибка"}",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId });
                return Task.CompletedTask;
            };

            hubConnection.Reconnected += connectionId =>
            {
                _ = RejoinWorkerHubAsync(hubConnection, pending.SessionId, connectionId);
                return Task.CompletedTask;
            };

            hubConnection.Closed += error =>
            {
                _ = CaptchaWorkerLog.WarningAsync(
                    $"Captcha: hub закрыт — {error?.Message ?? "без ошибки"}",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId });
                return Task.CompletedTask;
            };

            try
            {
                await hubConnection.StartAsync(sessionControlCts.Token).ConfigureAwait(false);
                await CaptchaWorkerLog.InfoAsync(
                    "Captcha: hub подключён.",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await CaptchaWorkerLog.ErrorAsync(
                    $"Captcha: не удалось подключиться к hub — {ex.Message}",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                throw;
            }

            var currentSession = await apiClient.GetCaptchaSessionAsync(pending.SessionId, cancellationToken).ConfigureAwait(false);
            if (!CanRunSession(currentSession))
            {
                await CaptchaWorkerLog.WarningAsync(
                    $"Captcha: запуск отменён до join, сессия {DescribeSessionState(currentSession)}.",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                return false;
            }

            await hubConnection.InvokeAsync("JoinAsWorker", pending.SessionId, sessionControlCts.Token).ConfigureAwait(false);

            currentSession = await apiClient.GetCaptchaSessionAsync(pending.SessionId, cancellationToken).ConfigureAwait(false);
            if (!CanRunSession(currentSession))
            {
                await CaptchaWorkerLog.WarningAsync(
                    $"Captcha: сессия {pending.SessionId} успела завершиться до старта host ({DescribeSessionState(currentSession)}).",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                return false;
            }

            var request = new CaptchaSessionHostRequest(
                pending.SessionId,
                pending.AdsPowerProfileId,
                pending.AdsPowerApiBaseUrl,
                pending.AdsPowerApiKey,
                pending.PageUrl,
                pending.SubProfileId,
                pending.ViewportWidth,
                pending.ViewportHeight);

            var snapshotCount = 0;
            var frameCount = 0;
            var result = await captchaHost.RunAsync(
                request,
                async (snapshot, ct) =>
                {
                    if (hubConnection.State != HubConnectionState.Connected)
                    {
                        return;
                    }

                    try
                    {
                        await hubConnection.InvokeAsync(
                            "SendSnapshot",
                            new CaptchaSnapshotMessage(
                                snapshot.SessionId,
                                snapshot.MhtmlGzipBase64,
                                snapshot.ViewportWidth,
                                snapshot.ViewportHeight,
                                snapshot.TimestampMs,
                                "image/jpeg"),
                            ct).ConfigureAwait(false);
                        snapshotCount++;
                        if (snapshotCount == 1 || snapshotCount % 10 == 0)
                        {
                            await CaptchaWorkerLog.InfoAsync(
                                $"Captcha: отправлен снимок #{snapshotCount} ({snapshot.MhtmlGzipBase64.Length} b64 chars).",
                                nameof(TryRunPendingSessionAsync),
                                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        await CaptchaWorkerLog.ErrorAsync(
                            $"Captcha: ошибка SendSnapshot — {ex.Message}",
                            nameof(TryRunPendingSessionAsync),
                            new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                            .ConfigureAwait(false);
                        throw;
                    }
                },
                async (frame, ct) =>
                {
                    if (hubConnection.State != HubConnectionState.Connected)
                    {
                        return;
                    }

                    try
                    {
                        await hubConnection.InvokeAsync(
                            "SendFrame",
                            new CaptchaFrameMessage(
                                frame.SessionId,
                                frame.ImageBase64,
                                frame.ViewportWidth,
                                frame.ViewportHeight,
                                frame.TimestampMs,
                                "image/jpeg"),
                            ct).ConfigureAwait(false);
                        frameCount++;
                        if (frameCount == 1 || frameCount % 30 == 0)
                        {
                            await CaptchaWorkerLog.InfoAsync(
                                $"Captcha: отправлен live frame #{frameCount} ({frame.ImageBase64.Length} b64 chars).",
                                nameof(TryRunPendingSessionAsync),
                                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        await CaptchaWorkerLog.ErrorAsync(
                            $"Captcha: ошибка SendFrame — {ex.Message}",
                            nameof(TryRunPendingSessionAsync),
                            new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                            .ConfigureAwait(false);
                        throw;
                    }
                },
                inputChannel.Reader,
                async (progress, ct) =>
                {
                    if (progress.Status == CaptchaSessionStatuses.Active)
                    {
                        var (updateOk, updateError) = await apiClient.UpdateCaptchaSessionStatusAsync(
                            new UpdateCaptchaSessionStatusRequest(pending.SessionId, progress.Status),
                            ct).ConfigureAwait(false);
                        if (!updateOk)
                        {
                            var latestSession = await apiClient.GetCaptchaSessionAsync(pending.SessionId, cancellationToken).ConfigureAwait(false);
                            if (latestSession is not null && !CaptchaSessionStatuses.IsActive(latestSession.Status))
                            {
                                externalTerminalStatus = latestSession.Status;
                                externalTerminalMessage = latestSession.FailureMessage ?? updateError;
                                await CaptchaWorkerLog.WarningAsync(
                                    $"Captcha: API отклонил переход в active — {updateError ?? "без сообщения"}. Останавливаем сессию.",
                                    nameof(TryRunPendingSessionAsync),
                                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                                    .ConfigureAwait(false);
                                sessionControlCts.Cancel();
                                return;
                            }

                            await CaptchaWorkerLog.WarningAsync(
                                $"Captcha: API не подтвердил переход в active — {updateError ?? "без сообщения"}. Продолжаем и ждём следующего статуса.",
                                nameof(TryRunPendingSessionAsync),
                                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                                .ConfigureAwait(false);
                        }
                    }

                    if (hubConnection.State == HubConnectionState.Connected)
                    {
                        await hubConnection.InvokeAsync(
                            "NotifyStateChanged",
                            new CaptchaStateChangedMessage(pending.SessionId, progress.Status, progress.Message),
                            ct).ConfigureAwait(false);
                    }
                },
                sessionControlCts.Token).ConfigureAwait(false);

            if ((string.Equals(externalTerminalStatus, CaptchaSessionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(externalTerminalStatus, CaptchaSessionStatuses.Expired, StringComparison.OrdinalIgnoreCase))
                && !cancellationToken.IsCancellationRequested)
            {
                await CaptchaWorkerLog.InfoAsync(
                    $"Captcha: сессия завершена внешним статусом {externalTerminalStatus} — {externalTerminalMessage ?? "—"}.",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                return false;
            }

            var finalStatus = result.Success ? CaptchaSessionStatuses.Completed : CaptchaSessionStatuses.Failed;
            var (finalUpdateOk, finalUpdateError) = await apiClient.UpdateCaptchaSessionStatusAsync(
                new UpdateCaptchaSessionStatusRequest(
                    pending.SessionId,
                    finalStatus,
                    result.Success,
                    result.Success ? null : result.Message),
                cancellationToken).ConfigureAwait(false);
            if (!finalUpdateOk)
            {
                await CaptchaWorkerLog.WarningAsync(
                    $"Captcha: финальный статус {finalStatus} не принят API — {finalUpdateError ?? "без сообщения"}.",
                    nameof(TryRunPendingSessionAsync),
                    new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
            }

            if (hubConnection.State == HubConnectionState.Connected)
            {
                await hubConnection.InvokeAsync(
                    "NotifyStateChanged",
                    new CaptchaStateChangedMessage(pending.SessionId, finalStatus, result.Message),
                    cancellationToken).ConfigureAwait(false);
            }

            await CaptchaWorkerLog.InfoAsync(
                $"Captcha: сессия завершена — {finalStatus}, live frames: {frameCount}, снимков: {snapshotCount}, сообщение: {result.Message ?? "—"}",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);

            return result.Success;
        }
        catch (Exception ex)
        {
            await CaptchaWorkerLog.ErrorAsync(
                $"Captcha: сессия упала — {ex.Message}",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["captcha.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            _isRunning = false;
            monitoringService.ExitCaptchaHold();
            SessionMutex.Release();
        }
    }

    private static async Task RejoinWorkerHubAsync(
        HubConnection hubConnection,
        Guid sessionId,
        string? connectionId)
    {
        try
        {
            await hubConnection.InvokeAsync("JoinAsWorker", sessionId).ConfigureAwait(false);
            await CaptchaWorkerLog.InfoAsync(
                $"Captcha: hub переподключён ({connectionId ?? "n/a"}), сессия {sessionId} восстановлена.",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["captcha.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CaptchaWorkerLog.WarningAsync(
                $"Captcha: не удалось восстановить hub-сессию — {ex.Message}",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["captcha.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
    }

    private static bool CanRunSession(CaptchaSessionDto? session) =>
        session is not null
        && CaptchaSessionStatuses.IsActive(session.Status)
        && session.ExpiresAtUtc > DateTime.UtcNow;

    private static string DescribeSessionState(CaptchaSessionDto? session)
    {
        if (session is null)
        {
            return "не подтверждена API";
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return $"истекла ({session.Status})";
        }

        return session.Status;
    }

    private HubConnection BuildHubConnection(string apiKey)
    {
        var baseUrl = credentials.ApiBaseUrl.TrimEnd('/');
        return new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/captcha", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
                options.Headers["Authorization"] = $"Bearer {apiKey}";
            })
            .WithAutomaticReconnect()
            .Build();
    }
}
