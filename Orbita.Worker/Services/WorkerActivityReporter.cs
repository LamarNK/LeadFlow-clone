using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerActivityReporter(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials) : IWorkerActivityReporter
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _sync = new();
    private WorkerActivityRequest? _pending;
    private DateTime _lastSentUtc = DateTime.MinValue;
    private CancellationTokenSource? _flushCts;

    public void ReportCycle(int accountCount) =>
        Enqueue(WorkerActivityPhases.Cycle, $"Цикл: {accountCount} аккаунт(ов)");

    public void ReportWaiting(DateTime nextCycleAtUtc, string message) =>
        Enqueue(WorkerActivityPhases.Waiting, message, nextCycleAtUtc: nextCycleAtUtc);

    public void ReportAccount(Guid accountId, string accountName, string message) =>
        Enqueue(WorkerActivityPhases.Account, message, accountId, accountName);

    public void ReportSubProfile(
        Guid accountId,
        string accountName,
        string subProfileId,
        string subProfileName,
        string message) =>
        Enqueue(
            WorkerActivityPhases.SubProfile,
            message,
            accountId,
            accountName,
            subProfileId,
            subProfileName);

    public void ReportSkipped(Guid accountId, string accountName, string reason) =>
        Enqueue(WorkerActivityPhases.Skipped, reason, accountId, accountName);

    public void ReportError(string message) =>
        Enqueue(WorkerActivityPhases.Error, message);

    public void ReportStopped() =>
        Enqueue(WorkerActivityPhases.Stopped, "Мониторинг остановлен", flushImmediately: true);

    public void ReportIdle() =>
        Enqueue(WorkerActivityPhases.Idle, "Ожидание", flushImmediately: true);

    public void ReportNoEnabledAccounts() =>
        Enqueue(WorkerActivityPhases.Idle, "Нет активных аккаунтов", flushImmediately: true);

    private void Enqueue(
        string phase,
        string message,
        Guid? accountId = null,
        string? accountName = null,
        string? subProfileId = null,
        string? subProfileName = null,
        DateTime? nextCycleAtUtc = null,
        bool flushImmediately = false)
    {
        var workerId = credentials.WorkerId;
        if (workerId is null)
        {
            return;
        }

        var request = new WorkerActivityRequest(
            workerId.Value,
            phase,
            message,
            accountId,
            accountName,
            subProfileId,
            subProfileName,
            nextCycleAtUtc,
            DateTime.UtcNow);

        lock (_sync)
        {
            _pending = request;
            _flushCts?.Cancel();
            _flushCts?.Dispose();
            _flushCts = new CancellationTokenSource();

            if (flushImmediately || DateTime.UtcNow - _lastSentUtc >= DebounceInterval)
            {
                _ = FlushAsync(_flushCts.Token);
                return;
            }

            var cts = _flushCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceInterval, cts.Token).ConfigureAwait(false);
                    await FlushAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // superseded by a newer report
                }
            });
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        WorkerActivityRequest? request;
        lock (_sync)
        {
            request = _pending;
            _pending = null;
            if (request is null)
            {
                return;
            }

            _lastSentUtc = DateTime.UtcNow;
        }

        try
        {
            await apiClient.SendActivityAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // best effort telemetry
        }
    }
}