using System.Collections.Concurrent;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerActivityReporter(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials,
    WorkerUpdateGate updateGate) : IWorkerActivityReporter
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<Guid, WorkerActiveAccountDto> _activeAccounts = new();
    private WorkerActivityGlobalState? _global;
    private WorkerActivityRequest? _pending;
    private DateTime _lastSentUtc = DateTime.MinValue;
    private CancellationTokenSource? _flushCts;

    private sealed record WorkerActivityGlobalState(
        string Phase,
        string Message,
        DateTime? NextCycleAtUtc);

    public void ReportCycle(int accountCount) =>
        SetGlobal(WorkerActivityPhases.Cycle, $"Цикл: {accountCount} аккаунт(ов)");

    public void ReportCycleProgress(string message) =>
        SetGlobal(WorkerActivityPhases.Cycle, message);

    public void ReportWaiting(DateTime nextCycleAtUtc, string message) =>
        SetGlobal(WorkerActivityPhases.Waiting, message, nextCycleAtUtc);

    public void ReportAccount(Guid accountId, string accountName, string message) =>
        UpsertAccount(accountId, accountName, WorkerActivityPhases.Account, message);

    public void ReportSubProfile(
        Guid accountId,
        string accountName,
        string subProfileId,
        string subProfileName,
        string message) =>
        UpsertAccount(
            accountId,
            accountName,
            WorkerActivityPhases.SubProfile,
            message,
            subProfileId,
            subProfileName);

    public void ReportSkipped(Guid accountId, string accountName, string reason) =>
        UpsertAccount(accountId, accountName, WorkerActivityPhases.Skipped, reason);

    public void ReportError(string message) =>
        SetGlobal(WorkerActivityPhases.Error, message, flushImmediately: true);

    public void ReportStopped() =>
        SetGlobal(WorkerActivityPhases.Stopped, "Мониторинг остановлен", flushImmediately: true);

    public void ReportIdle() =>
        SetGlobal(WorkerActivityPhases.Idle, "Ожидание", flushImmediately: true);

    public void ReportNoEnabledAccounts() =>
        SetGlobal(WorkerActivityPhases.Idle, "Нет активных аккаунтов", flushImmediately: true);

    public void ReportAccountFinished(Guid accountId)
    {
        if (!_activeAccounts.TryRemove(accountId, out _))
        {
            return;
        }

        EnqueueFlush();
    }

    private void SetGlobal(
        string phase,
        string message,
        DateTime? nextCycleAtUtc = null,
        bool flushImmediately = false)
    {
        lock (_sync)
        {
            _global = new WorkerActivityGlobalState(phase, message, nextCycleAtUtc);
            _activeAccounts.Clear();
        }

        updateGate.SetPhase(phase);
        EnqueueFlush(flushImmediately);
    }

    private void UpsertAccount(
        Guid accountId,
        string accountName,
        string phase,
        string message,
        string? subProfileId = null,
        string? subProfileName = null)
    {
        lock (_sync)
        {
            _global = null;
            updateGate.SetPhase(phase);
            _activeAccounts[accountId] = new WorkerActiveAccountDto(
                accountId,
                accountName,
                phase,
                message,
                subProfileId,
                subProfileName,
                DateTime.UtcNow);
        }

        EnqueueFlush();
    }

    private void EnqueueFlush(bool flushImmediately = false)
    {
        var workerId = credentials.WorkerId;
        if (workerId is null)
        {
            return;
        }

        var request = BuildRequest(workerId.Value);
        if (request is null)
        {
            return;
        }

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

    private WorkerActivityRequest? BuildRequest(Guid workerId)
    {
        WorkerActivityGlobalState? global;
        List<WorkerActiveAccountDto> active;
        lock (_sync)
        {
            global = _global;
            active = _activeAccounts.Values
                .OrderBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var now = DateTime.UtcNow;
        if (global is not null && active.Count == 0)
        {
            return new WorkerActivityRequest(
                workerId,
                global.Phase,
                global.Message,
                NextCycleAtUtc: global.NextCycleAtUtc,
                UpdatedAtUtc: now,
                ActiveAccounts: []);
        }

        if (active.Count == 0)
        {
            return null;
        }

        if (active.Count == 1)
        {
            var one = active[0];
            return new WorkerActivityRequest(
                workerId,
                one.Phase,
                one.Message,
                one.AccountId,
                one.AccountName,
                one.SubProfileId,
                one.SubProfileName,
                UpdatedAtUtc: now,
                ActiveAccounts: active);
        }

        return new WorkerActivityRequest(
            workerId,
            WorkerActivityPhases.Parallel,
            $"{active.Count} аккаунта в работе",
            ActiveAccounts: active,
            UpdatedAtUtc: now);
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