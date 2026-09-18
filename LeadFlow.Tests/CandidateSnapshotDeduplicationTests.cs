using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidateSnapshotDeduplicationTests
{
    [Fact]
    public void PendingCandidate_SuppressesSamePhoneButAllowsPhoneChange()
    {
        var pendingPhones = new HashSet<string>(StringComparer.Ordinal);
        var candidate = new CandidateResponse
        {
            FullName = "Иванов Иван Иванович",
            AvitoSubProfileId = "sub-1",
            PhoneRaw = "+7 900 000-00-00"
        };

        WorkerMonitoringService.MarkCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000000");

        Assert.True(WorkerMonitoringService.IsCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000000"));
        Assert.False(WorkerMonitoringService.IsCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000001"));

        WorkerMonitoringService.MarkCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000001");

        Assert.True(WorkerMonitoringService.IsCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000000"));
        Assert.True(WorkerMonitoringService.IsCandidatePhonePending(
            pendingPhones,
            candidate,
            "79000000001"));
    }
}
