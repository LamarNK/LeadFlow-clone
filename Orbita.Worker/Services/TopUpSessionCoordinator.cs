using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>
/// Воркерная автоматизация ручного пополнения аванса Avito.
/// Открывает существующий браузерный профиль выбранного аккаунта, ведёт его через
/// /account/advance → сумма → СБП → оплата → QR, и сообщает статусы Started/QrReady/Failed.
/// Оплату не выполняет и не сообщает об оплате. Один запуск на сессию/аккаунт.
/// </summary>
public sealed class TopUpSessionCoordinator(
    WorkerAccountSessionFactory accountSessionFactory,
    WorkerAccountRuntimeStore runtimeStore,
    OrbitaConfigProvider configProvider,
    OrbitaApiClient apiClient,
    IWorkerRealtimeChannel realtime)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task TryRunPendingSessionAsync(
        WorkerPendingTopUpSessionDto pending,
        WorkerConfigDto config,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            await TopUpWorkerLog.WarningAsync(
                $"Top-up: сессия уже выполняется, повторный запуск пропущен (session {pending.SessionId:D}).",
                nameof(TryRunPendingSessionAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await RunAsync(pending, config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            realtime.RequestWake();
        }
    }

    private async Task RunAsync(
        WorkerPendingTopUpSessionDto pending,
        WorkerConfigDto config,
        CancellationToken cancellationToken)
    {
        await TopUpWorkerLog.InfoAsync(
            $"Top-up: подхвачена сессия {pending.SessionId:D}, аккаунт {pending.AccountName} ({pending.AccountId:D}), сумма {pending.RequestedAmount:0.##} ₽.",
            nameof(RunAsync),
            new Dictionary<string, object?>
            {
                ["topup.sessionId"] = pending.SessionId,
                ["topup.accountId"] = pending.AccountId,
                ["topup.amount"] = pending.RequestedAmount
            })
            .ConfigureAwait(false);

        var account = ResolveAccount(pending.AccountId, config);
        if (account is null)
        {
            await ReportFailedAsync(
                    pending.SessionId,
                    "Аккаунт не найден в конфигурации воркера.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Наблюдатель отмены: опрашивает состояние сессии и отменяет токен, если сессия
        // стала терминальной (отменена/истекла/ошибка). Токен передаётся в браузерный сценарий,
        // чтобы прервать его до клика по оплате и формирования QR.
        var guard = new TopUpSessionCancellationGuard(
            ct => apiClient.GetTopUpSessionAsync(pending.SessionId, ct));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.Token);
        var guardTask = guard.RunAsync(pending.SessionId, cancellationToken);

        try
        {
            // Проверка до старта: если сессия уже отменена, не начинаем.
            if (!await guard.IsSessionActiveAsync(pending.SessionId, cancellationToken).ConfigureAwait(false))
            {
                await TopUpWorkerLog.InfoAsync(
                    $"Top-up: сессия {pending.SessionId:D} уже завершена до старта, пропускаем.",
                    nameof(RunAsync),
                    new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                return;
            }

            // Сообщаем Started до навигации.
            if (!await ReportStartedAsync(pending.SessionId, cancellationToken).ConfigureAwait(false))
            {
                await TopUpWorkerLog.WarningAsync(
                    $"Top-up: API не принял статус started для сессии {pending.SessionId:D}, останавливаемся.",
                    nameof(RunAsync),
                    new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                    .ConfigureAwait(false);
                return;
            }

            var adsOptions = new AdsPowerConnectionOptions(
                string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl) ? string.Empty : account.AdsPowerApiBaseUrl,
                string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

            var loginCredentials = AvitoLoginCredentials.TryCreate(account.AvitoLogin, account.AvitoPassword);
            using var loginScope = AvitoAutoLoginContext.Use(loginCredentials);

            WorkerOpenedAccountSession? opened = null;
            try
            {
                // Проверка перед открытием браузера.
                if (guard.IsCancelled)
                {
                    await TopUpWorkerLog.InfoAsync(
                        $"Top-up: сессия {pending.SessionId:D} отменена до открытия браузера.",
                        nameof(RunAsync),
                        new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                        .ConfigureAwait(false);
                    return;
                }

                opened = await accountSessionFactory
                    .OpenAsync(account, adsOptions, reportStartupStage: null, linkedCts.Token)
                    .ConfigureAwait(false);

                // Проверка перед запуском сценария пополнения.
                if (guard.IsCancelled)
                {
                    await TopUpWorkerLog.InfoAsync(
                        $"Top-up: сессия {pending.SessionId:D} отменена до запуска сценария.",
                        nameof(RunAsync),
                        new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                        .ConfigureAwait(false);
                    return;
                }

                var result = await opened.Session
                    .RunAdvanceTopUpAsync(
                        pending.RequestedAmount,
                        linkedCts.Token,
                        beforePayClickAsync: async ct =>
                        {
                            // Линеаризационный барьер: атомарно заявляем право на оплату.
                            var claim = await apiClient.ClaimTopUpPaymentAsync(pending.SessionId, ct)
                                .ConfigureAwait(false);
                            if (!claim.Claimed)
                            {
                                await TopUpWorkerLog.WarningAsync(
                                    $"Top-up: claim оплаты отклонён для сессии {pending.SessionId:D} — {claim.Error ?? "без сообщения"}.",
                                    nameof(RunAsync),
                                    new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                                    .ConfigureAwait(false);
                            }

                            return claim.Claimed;
                        })
                    .ConfigureAwait(false);

                // Финальная проверка: не даём позднему QrReady оживить отменённую сессию.
                if (guard.IsCancelled)
                {
                    await TopUpWorkerLog.InfoAsync(
                        $"Top-up: сессия {pending.SessionId:D} отменена, QR не публикуем.",
                        nameof(RunAsync),
                        new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                        .ConfigureAwait(false);
                    return;
                }

                if (result.Success)
                {
                    await ReportQrReadyAsync(
                            pending.SessionId,
                            result.QrImageBase64,
                            result.QrImageUrl,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ReportFailedAsync(
                            pending.SessionId,
                            result.FailureMessage ?? "Не удалось сформировать QR-код.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (opened is not null)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (guard.IsCancelled)
        {
            // Сессия отменена оператором: не сообщаем failed, сессия уже терминальная.
            await TopUpWorkerLog.InfoAsync(
                $"Top-up: сессия {pending.SessionId:D} прервана отменой оператора.",
                nameof(RunAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена/остановка воркера: сессия остаётся активной, её подметёт sweeper на API.
            await TopUpWorkerLog.InfoAsync(
                $"Top-up: сессия {pending.SessionId:D} прервана отменой.",
                nameof(RunAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = pending.SessionId })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReportFailedAsync(
                    pending.SessionId,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            guard.Stop();
            await guardTask.ConfigureAwait(false);
        }
    }

    private AvitoAccount? ResolveAccount(Guid accountId, WorkerConfigDto config)
    {
        var dto = config.Accounts.FirstOrDefault(x => x.AccountId == accountId);
        if (dto is null)
        {
            return null;
        }

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, config.AdsPowerApiBaseUrl ?? string.Empty);
        runtimeStore.OverlayRuntime(account);
        return account;
    }

    private async Task<bool> ReportStartedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var (ok, error) = await apiClient
            .UpdateTopUpSessionStatusAsync(
                new UpdateTopUpSessionStatusRequest(sessionId, TopUpSessionStatuses.Started),
                cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            await TopUpWorkerLog.WarningAsync(
                $"Top-up: статус started не принят API — {error ?? "без сообщения"}.",
                nameof(ReportStartedAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }

        return ok;
    }

    private async Task ReportQrReadyAsync(
        Guid sessionId,
        string? qrBase64,
        string? qrUrl,
        CancellationToken cancellationToken)
    {
        var (ok, error) = await apiClient
            .UpdateTopUpSessionStatusAsync(
                new UpdateTopUpSessionStatusRequest(
                    sessionId,
                    TopUpSessionStatuses.QrReady,
                    qrBase64,
                    qrUrl),
                cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            await TopUpWorkerLog.WarningAsync(
                $"Top-up: статус qr_ready не принят API — {error ?? "без сообщения"}.",
                nameof(ReportQrReadyAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
        else
        {
            await TopUpWorkerLog.InfoAsync(
                $"Top-up: QR готов для сессии {sessionId:D}.",
                nameof(ReportQrReadyAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
    }

    private async Task ReportFailedAsync(
        Guid sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        var sanitized = AvitoAdvanceTopUpScripts.SanitizeDiagnostic(message);
        var (ok, error) = await apiClient
            .UpdateTopUpSessionStatusAsync(
                new UpdateTopUpSessionStatusRequest(
                    sessionId,
                    TopUpSessionStatuses.Failed,
                    FailureMessage: sanitized),
                cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            await TopUpWorkerLog.WarningAsync(
                $"Top-up: статус failed не принят API — {error ?? "без сообщения"}.",
                nameof(ReportFailedAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
        else
        {
            await TopUpWorkerLog.WarningAsync(
                $"Top-up: сессия {sessionId:D} завершена ошибкой — {sanitized}.",
                nameof(ReportFailedAsync),
                new Dictionary<string, object?> { ["topup.sessionId"] = sessionId })
                .ConfigureAwait(false);
        }
    }
}
