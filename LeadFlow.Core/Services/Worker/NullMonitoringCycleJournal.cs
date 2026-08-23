namespace LeadFlow.Core.Services.Worker;

public sealed class NullMonitoringCycleJournal : IMonitoringCycleJournal
{
    public static readonly NullMonitoringCycleJournal Instance = new();

    public Guid BeginCycle(Guid accountId, string accountName) => Guid.NewGuid();

    public Guid BeginSubProfile(
        Guid cycleId,
        string subProfileId,
        string subProfileName,
        int position,
        int total) => Guid.NewGuid();

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
    }

    public void CompleteCycle(Guid cycleId)
    {
    }

    public void AbortCycle(Guid cycleId, string? errorType = null, string? errorMessage = null)
    {
    }

    public void FailCycle(Guid cycleId, string? errorType = null, string? errorMessage = null)
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
