using Orbita.Contracts;
using Orbita.Worker;
using Orbita.Worker.Services;

namespace Orbita.Tests
{
    public sealed class MonitoringCycleJournalSinkTests
    {
        [Fact]
        public async Task CompleteSubProfile_RecordsExplicitSuccessfulLoginAttempt()
        {
            var api = new OrbitaApiClient();
            var credentials = new WorkerCredentials { WorkerId = Guid.NewGuid() };
            await using var sink = new MonitoringCycleJournalSink(api, credentials);

            var cycleId = sink.BeginCycle(Guid.NewGuid(), "авито 88");
            var subRunId = sink.BeginSubProfile(cycleId, "sp-10", "Кадровый отдел Воронеж 10", 1, 1);
            sink.CompleteSubProfile(cycleId, subRunId, 0, 0, loginAttempted: true, loginSucceeded: true);
            sink.CompleteCycle(cycleId);
            await sink.FlushAsync();

            var sub = Assert.Single(api.Batches.Last().Cycles.Single().SubProfiles);
            Assert.True(sub.LoginAttempted);
            Assert.True(sub.LoginSucceeded);
            Assert.Null(sub.ErrorType);
        }

        [Fact]
        public async Task CompleteSubProfile_RecordsPhoneWatchMetricsSeparately()
        {
            var api = new OrbitaApiClient();
            var credentials = new WorkerCredentials { WorkerId = Guid.NewGuid() };
            await using var sink = new MonitoringCycleJournalSink(api, credentials);

            var cycleId = sink.BeginCycle(Guid.NewGuid(), "авито 56");
            var subRunId = sink.BeginSubProfile(cycleId, "sp-1", "Самара", 1, 1);
            sink.CompleteSubProfile(
                cycleId,
                subRunId,
                foundCount: 12,
                publishedCount: 2,
                collectedCount: 2,
                watchRefreshedCount: 7,
                phoneChangedCount: 1);
            sink.CompleteCycle(cycleId);
            await sink.FlushAsync();

            var sub = Assert.Single(api.Batches.Last().Cycles.Single().SubProfiles);
            Assert.Equal(2, sub.PublishedCount);
            Assert.Equal(2, sub.CollectedCount);
            Assert.Equal(7, sub.WatchRefreshedCount);
            Assert.Equal(1, sub.PhoneChangedCount);
        }

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
