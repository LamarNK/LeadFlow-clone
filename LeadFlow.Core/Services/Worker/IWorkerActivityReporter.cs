namespace LeadFlow.Core.Services.Worker;

public interface IWorkerActivityReporter
{
    void ReportCycle(int accountCount);

    void ReportWaiting(DateTime nextCycleAtUtc, string message);

    void ReportAccount(Guid accountId, string accountName, string message);

    void ReportSubProfile(
        Guid accountId,
        string accountName,
        string subProfileId,
        string subProfileName,
        string message);

    void ReportSkipped(Guid accountId, string accountName, string reason);

    void ReportError(string message);

    void ReportStopped();

    void ReportIdle();

    void ReportNoEnabledAccounts();

    void ReportAccountFinished(Guid accountId);
}