using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AppRepositoryPruneTests
{
    private readonly EfInMemoryDatabase _db = new();
    private readonly AppRepository _repository;

    public AppRepositoryPruneTests() =>
        _repository = new AppRepository(_db.Factory);

    [Fact]
    public async Task PruneExpiredLocalData_RemovesOldFinalizedCandidatesAndLogs()
    {
        var now = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var oldSentId = Guid.NewGuid();
        var recentInProgressId = Guid.NewGuid();

        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            db.CandidateResponses.AddRange(
                new CandidateResponseEntity
                {
                    Id = oldSentId,
                    AccountId = accountId,
                    PhoneNormalized = "79001112233",
                    Status = nameof(ResponseStatus.Sent),
                    CreatedAt = now.AddDays(-120)
                },
                new CandidateResponseEntity
                {
                    Id = recentInProgressId,
                    AccountId = accountId,
                    PhoneNormalized = "79004445566",
                    Status = nameof(ResponseStatus.InProgress),
                    CreatedAt = now.AddDays(-120)
                });
            db.ProcessingLogs.AddRange(
                new ProcessingLogEntity
                {
                    Id = Guid.NewGuid(),
                    CandidateResponseId = oldSentId,
                    AccountId = accountId,
                    CreatedAt = now.AddDays(-120),
                    Level = "Info",
                    Message = "old"
                },
                new ProcessingLogEntity
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    CreatedAt = now.AddDays(-40),
                    Level = "Info",
                    Message = "orphan"
                });
            await db.SaveChangesAsync();
        }

        var result = await _repository.PruneExpiredLocalDataAsync(now, 90, 30, CancellationToken.None);

        Assert.Equal(1, result.CandidatesRemoved);
        Assert.Equal(2, result.LogsRemoved);

        await using var verify = await _db.Factory.CreateDbContextAsync();
        Assert.Single(verify.CandidateResponses);
        Assert.Equal(recentInProgressId, verify.CandidateResponses.Single().Id);
        Assert.Empty(verify.ProcessingLogs);
    }
}