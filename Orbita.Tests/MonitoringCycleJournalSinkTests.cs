using Orbita.Contracts;
using Orbita.Worker;
using Orbita.Worker.Services;

namespace Orbita.Tests
{
    public sealed class MonitoringCycleJournalSinkTests
    {
        [Fact]
        public async Task AbortCycle_OpenSubProfile_ClosesItAsFailed()
        {
            var api = new OrbitaApiClient();
            var credentials = new WorkerCredentials { WorkerId = Guid.NewGuid() };
            await using var sink = new MonitoringCycleJournalSink(api, credentials);

            var cycleId = sink.BeginCycle(Guid.NewGuid(), "авито 88");
            await sink.FlushAsync();
            var subRunId = sink.BeginSubProfile(cycleId, "sp-10", "Кадровый отдел Воронеж 10", 2, 2);
            await sink.FlushAsync();

            sink.AbortCycle(cycleId, "cdp-timeout", "pointer-click не ответила за 8 с");
            await sink.FlushAsync();

            var cycle = Assert.Single(api.Batches.Last().Cycles);
            Assert.Equal(MonitoringCycleRunStatuses.Aborted, cycle.Status);
            var sub = Assert.Single(cycle.SubProfiles);
            Assert.Equal(subRunId, sub.Id);
            Assert.Equal(MonitoringSubProfileRunOutcomes.Failed, sub.Outcome);
            Assert.NotNull(sub.CompletedAtUtc);
            Assert.Equal("cdp-timeout", sub.ErrorType);
            Assert.Equal("pointer-click не ответила за 8 с", sub.ErrorMessage);
        }
    }
}

namespace Orbita.Worker
{
    public sealed class WorkerCredentials
    {
        public Guid? WorkerId { get; set; }
    }
}

namespace Orbita.Worker.Services
{
    public sealed class OrbitaApiClient
    {
        public List<MonitoringRunBatchRequest> Batches { get; } = [];

        public Task<bool> SendMonitoringRunsAsync(MonitoringRunBatchRequest batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.FromResult(true);
        }
    }
}
