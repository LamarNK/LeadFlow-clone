namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerActivityReporter : IWorkerActivityReporter
{
    public static readonly NullWorkerActivityReporter Instance = new();

    public void ReportCycle(int accountCount) { }

    public void ReportCycleProgress(string message) { }

    public void ReportWaiting(DateTime nextCycleAtUtc, string message) { }

    public void ReportAccount(Guid accountId, string accountName, string message) { }

    public void ReportSubProfile(
        Guid accountId,
        string accountName,
        string subProfileId,
        string subProfileName,
        string message) { }

    public void ReportSkipped(Guid accountId, string accountName, string reason) { }

    public void ReportError(string message) { }

    public void ReportStopped() { }

    public void ReportIdle() { }

    public void ReportNoEnabledAccounts() { }

    public void ReportAccountFinished(Guid accountId) { }
}