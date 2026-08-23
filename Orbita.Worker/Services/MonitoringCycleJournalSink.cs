using System.Collections.Concurrent;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>
/// Буферизует журнал мониторинг-циклов и отправляет батчами в Orbita API.
/// </summary>
public sealed class MonitoringCycleJournalSink(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials) : IMonitoringCycleJournal, IAsyncDisposable
{
    private const int MaxCyclesBeforeFlush = 8;
    private static readonly TimeSpan FlushWindow = TimeSpan.FromSeconds(4);

    private readonly ConcurrentDictionary<Guid, MutableCycle> _cycles = new();
    private readonly object _flushGate = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private volatile bool _disposed;
    private int _flushing;
    private int _flushRequested;

    private sealed class MutableCycle
    {
        public required Guid Id { get; init; }
        public required Guid AccountId { get; init; }
        public required string AccountName { get; set; }
        public required DateTime StartedAtUtc { get; init; }
        public DateTime? FinishedAtUtc { get; set; }
        public string Status { get; set; } = MonitoringCycleRunStatuses.Running;
        public ConcurrentDictionary<Guid, MutableSubProfile> SubProfiles { get; } = new();
        public bool Dirty { get; set; } = true;
    }

    private sealed class MutableSubProfile
    {
        public required Guid Id { get; init; }
        public required string SubProfileId { get; set; }
        public required string SubProfileName { get; set; }
        public required int Position { get; set; }
        public required int Total { get; set; }
        public required DateTime StartedAtUtc { get; init; }
        public DateTime? CompletedAtUtc { get; set; }
        public string Outcome { get; set; } = MonitoringSubProfileRunOutcomes.Started;
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public int FoundCount { get; set; }
        public int PublishedCount { get; set; }
        public int DeferredCount { get; set; }
        public int SkippedDuplicateCount { get; set; }
        public int CollectedCount { get; set; }
        public int CaptchaCount { get; set; }
        public int CaptchaSolvedCount { get; set; }
    }

    public Guid BeginCycle(Guid accountId, string accountName)
    {
        var id = Guid.NewGuid();
        var cycle = new MutableCycle
        {
            Id = id,
            AccountId = accountId,
            AccountName = string.IsNullOrWhiteSpace(accountName) ? accountId.ToString("D") : accountName.Trim(),
            StartedAtUtc = DateTime.UtcNow,
            Status = MonitoringCycleRunStatuses.Running,
            Dirty = true
        };
        _cycles[id] = cycle;
        _ = MaybeFlushAsync();
        return id;
    }

    public Guid BeginSubProfile(
        Guid cycleId,
        string subProfileId,
        string subProfileName,
        int position,
        int total)
    {
        if (!_cycles.TryGetValue(cycleId, out var cycle))
        {
            return Guid.Empty;
        }

        var id = Guid.NewGuid();
        cycle.SubProfiles[id] = new MutableSubProfile
        {
            Id = id,
            SubProfileId = subProfileId?.Trim() ?? string.Empty,
            SubProfileName = string.IsNullOrWhiteSpace(subProfileName) ? "—" : subProfileName.Trim(),
            Position = Math.Max(1, position),
            Total = Math.Max(total, position),
            StartedAtUtc = DateTime.UtcNow,
            Outcome = MonitoringSubProfileRunOutcomes.Started
        };
        cycle.Dirty = true;
        _ = MaybeFlushAsync();
        return id;
    }

    public void SkipSubProfile(
        Guid cycleId,
        string subProfileId,
        string subProfileName,
        int position,
        int total,
        string? errorType,
        string? errorMessage)
    {
        if (!_cycles.TryGetValue(cycleId, out var cycle))
        {
            return;
        }

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        cycle.SubProfiles[id] = new MutableSubProfile
        {
            Id = id,
            SubProfileId = subProfileId?.Trim() ?? string.Empty,
            SubProfileName = string.IsNullOrWhiteSpace(subProfileName) ? "—" : subProfileName.Trim(),
            Position = Math.Max(1, position),
            Total = Math.Max(total, position),
            StartedAtUtc = now,
            CompletedAtUtc = now,
            Outcome = MonitoringSubProfileRunOutcomes.Skipped,
            ErrorType = string.IsNullOrWhiteSpace(errorType) ? "not-reached" : errorType.Trim(),
            ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "очередь не дошла" : errorMessage.Trim()
        };
        cycle.Dirty = true;
        _ = MaybeFlushAsync();
    }

    public void CompleteSubProfile(
        Guid cycleId,
        Guid subProfileRunId,
        int foundCount,
        int publishedCount,
        int deferredCount = 0,
        int skippedDuplicateCount = 0,
        int collectedCount = 0,
        int captchaCount = 0,
        int captchaSolvedCount = 0)
    {
        if (!_cycles.TryGetValue(cycleId, out var cycle)
            || !cycle.SubProfiles.TryGetValue(subProfileRunId, out var sub))
        {
            return;
        }

        sub.Outcome = MonitoringSubProfileRunOutcomes.Completed;
        sub.CompletedAtUtc = DateTime.UtcNow;
        ApplyCounts(
            sub,
            foundCount,
            publishedCount,
            deferredCount,
            skippedDuplicateCount,
            collectedCount,
            captchaCount,
            captchaSolvedCount);
        cycle.Dirty = true;
        _ = MaybeFlushAsync();
    }

    public void FailSubProfile(
        Guid cycleId,
        Guid subProfileRunId,
        string? errorType,
        string? errorMessage,
        int foundCount = 0,
        int publishedCount = 0,
        int collectedCount = 0,
        int captchaCount = 0,
        int captchaSolvedCount = 0)
    {
        if (!_cycles.TryGetValue(cycleId, out var cycle)
            || !cycle.SubProfiles.TryGetValue(subProfileRunId, out var sub))
        {
            return;
        }

        sub.Outcome = MonitoringSubProfileRunOutcomes.Failed;
        sub.CompletedAtUtc = DateTime.UtcNow;
        sub.ErrorType = string.IsNullOrWhiteSpace(errorType) ? null : errorType.Trim();
        sub.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        ApplyCounts(
            sub,
            foundCount,
            publishedCount,
            deferredCount: 0,
            skippedDuplicateCount: 0,
            collectedCount,
            captchaCount,
            captchaSolvedCount);
        cycle.Dirty = true;
        _ = MaybeFlushAsync();
    }

    public void CompleteCycle(Guid cycleId) => FinishCycle(cycleId, MonitoringCycleRunStatuses.Completed);

    public void AbortCycle(Guid cycleId, string? errorType = null, string? errorMessage = null) =>
        FinishCycle(cycleId, MonitoringCycleRunStatuses.Aborted, errorType, errorMessage);

    public void FailCycle(Guid cycleId, string? errorType = null, string? errorMessage = null) =>
        FinishCycle(cycleId, MonitoringCycleRunStatuses.Failed, errorType, errorMessage);

    public void AbortOpenCycles(string? errorType = null, string? errorMessage = null)
    {
        foreach (var cycle in _cycles.Values.Where(c => c.Status == MonitoringCycleRunStatuses.Running))
        {
            FinishCycle(cycle.Id, MonitoringCycleRunStatuses.Aborted, errorType, errorMessage);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        FlushInternalAsync(cancellationToken);

    private void FinishCycle(
        Guid cycleId,
        string status,
        string? errorType = null,
        string? errorMessage = null)
    {
        if (!_cycles.TryGetValue(cycleId, out var cycle))
        {
            return;
        }

        if (cycle.SubProfiles.Count == 0 && !string.IsNullOrWhiteSpace(errorMessage))
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            cycle.SubProfiles[id] = new MutableSubProfile
            {
                Id = id,
                SubProfileId = string.Empty,
                SubProfileName = "—",
                Position = 1,
                Total = 1,
                StartedAtUtc = cycle.StartedAtUtc,
                CompletedAtUtc = now,
                Outcome = MonitoringSubProfileRunOutcomes.Failed,
                ErrorType = string.IsNullOrWhiteSpace(errorType) ? "cycle-start" : errorType.Trim(),
                ErrorMessage = errorMessage.Trim()
            };
        }

        cycle.Status = status;
        cycle.FinishedAtUtc = DateTime.UtcNow;
        cycle.Dirty = true;
        _ = MaybeFlushAsync(force: true);
    }

    private async Task MaybeFlushAsync(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        var dirtyCount = _cycles.Values.Count(c => c.Dirty);
        if (!force
            && dirtyCount < MaxCyclesBeforeFlush
            && DateTime.UtcNow - _lastFlushUtc < FlushWindow)
        {
            return;
        }

        // Флаши fire-and-forget: один in-flight POST. Если во время flush пришли
        // новые события — ставим _flushRequested и после завершения делаем ещё
        // один проход (не крутимся по Dirty: failed HTTP сам re-mark'ает Dirty
        // и иначе зациклился бы).
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _flushRequested, 1);
            return;
        }

        try
        {
            await FlushInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort — next flush will retry dirty cycles
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }

        if (Interlocked.Exchange(ref _flushRequested, 0) == 1 && !_disposed)
        {
            _ = MaybeFlushAsync(force: true);
        }
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        if (_disposed || credentials.WorkerId is null)
        {
            return;
        }

        List<MonitoringCycleRunUploadDto> batch;
        List<Guid> finishedIds;
        lock (_flushGate)
        {
            if (credentials.WorkerId is null)
            {
                return;
            }

            batch = [];
            finishedIds = [];
            foreach (var cycle in _cycles.Values.Where(c => c.Dirty).Take(MonitoringRunIngestServiceMaxBatch()))
            {
                batch.Add(ToDto(cycle));
                cycle.Dirty = false;
                if (cycle.Status is MonitoringCycleRunStatuses.Completed
                    or MonitoringCycleRunStatuses.Aborted
                    or MonitoringCycleRunStatuses.Failed)
                {
                    finishedIds.Add(cycle.Id);
                }
            }

            _lastFlushUtc = DateTime.UtcNow;
        }

        if (batch.Count == 0)
        {
            return;
        }

        var ok = await apiClient.SendMonitoringRunsAsync(
            new MonitoringRunBatchRequest(credentials.WorkerId.Value, batch),
            ct).ConfigureAwait(false);

        if (!ok)
        {
            // Re-mark dirty so we retry.
            foreach (var dto in batch)
            {
                if (_cycles.TryGetValue(dto.Id, out var cycle))
                {
                    cycle.Dirty = true;
                }
            }

            return;
        }

        foreach (var id in finishedIds)
        {
            _cycles.TryRemove(id, out _);
        }
    }

    private static int MonitoringRunIngestServiceMaxBatch() => 50;

    private static MonitoringCycleRunUploadDto ToDto(MutableCycle cycle) =>
        new(
            cycle.Id,
            cycle.AccountId,
            cycle.AccountName,
            cycle.StartedAtUtc,
            cycle.FinishedAtUtc,
            cycle.Status,
            cycle.SubProfiles.Values
                .OrderBy(s => s.Position)
                .ThenBy(s => s.StartedAtUtc)
                .Select(s => new MonitoringSubProfileRunUploadDto(
                    s.Id,
                    s.SubProfileId,
                    s.SubProfileName,
                    s.Position,
                    s.Total,
                    s.StartedAtUtc,
                    s.CompletedAtUtc,
                    s.Outcome,
                    s.ErrorType,
                    s.ErrorMessage,
                    s.FoundCount,
                    s.PublishedCount,
                    s.DeferredCount,
                    s.SkippedDuplicateCount,
                    s.CollectedCount,
                    s.CaptchaCount,
                    s.CaptchaSolvedCount))
                .ToList());

    private static void ApplyCounts(
        MutableSubProfile sub,
        int foundCount,
        int publishedCount,
        int deferredCount,
        int skippedDuplicateCount,
        int collectedCount,
        int captchaCount,
        int captchaSolvedCount)
    {
        sub.FoundCount = Math.Max(0, foundCount);
        sub.PublishedCount = Math.Max(0, publishedCount);
        sub.DeferredCount = Math.Max(0, deferredCount);
        sub.SkippedDuplicateCount = Math.Max(0, skippedDuplicateCount);
        sub.CollectedCount = Math.Max(0, collectedCount);
        sub.CaptchaCount = Math.Max(0, captchaCount);
        sub.CaptchaSolvedCount = Math.Min(sub.CaptchaCount, Math.Max(0, captchaSolvedCount));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await FlushInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}
