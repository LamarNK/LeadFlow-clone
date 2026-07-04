using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AppRepositoryDuplicateTests
{
    private readonly EfInMemoryDatabase _db = new();
    private readonly AppRepository _repository;

    public AppRepositoryDuplicateTests() =>
        _repository = new AppRepository(_db.Factory);

    [Fact]
    public async Task FindDuplicateAsync_NoCandidates_ReturnsNull()
    {
        var found = await _repository.FindDuplicateAsync(
            "79000000000",
            DuplicateScope.GlobalAcrossAllAccounts,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindDuplicateAsync_GlobalScope_FindsAcrossAccounts_ReturnsNewest()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var phone = "79000000001";

        await _repository.SaveCandidateAsync(NewCandidate(accountA, phone, "older", DateTime.UtcNow.AddHours(-1)), CancellationToken.None);
        await _repository.SaveCandidateAsync(NewCandidate(accountB, phone, "newer", DateTime.UtcNow), CancellationToken.None);

        var found = await _repository.FindDuplicateAsync(
            phone,
            DuplicateScope.GlobalAcrossAllAccounts,
            accountA,
            CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("newer", found!.SourceResponseId);
        Assert.Equal(accountB, found.AccountId);
    }

    [Fact]
    public async Task FindDuplicateAsync_PerAccountScope_IgnoresOtherAccount()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var phone = "79000000002";

        await _repository.SaveCandidateAsync(NewCandidate(accountA, phone, "a-1", DateTime.UtcNow), CancellationToken.None);

        var foundForB = await _repository.FindDuplicateAsync(
            phone,
            DuplicateScope.PerAvitoAccount,
            accountB,
            CancellationToken.None);

        Assert.Null(foundForB);
    }

    [Fact]
    public async Task FindDuplicateAsync_PerAccountScope_FindsSameAccount()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var phone = "79000000003";

        await _repository.SaveCandidateAsync(NewCandidate(accountA, phone, "a-1", DateTime.UtcNow), CancellationToken.None);
        await _repository.SaveCandidateAsync(NewCandidate(accountB, phone, "b-1", DateTime.UtcNow), CancellationToken.None);

        var foundForA = await _repository.FindDuplicateAsync(
            phone,
            DuplicateScope.PerAvitoAccount,
            accountA,
            CancellationToken.None);

        Assert.NotNull(foundForA);
        Assert.Equal(accountA, foundForA!.AccountId);
        Assert.Equal("a-1", foundForA.SourceResponseId);
    }

    [Fact]
    public async Task GetExistingNormalizedPhonesAsync_WithSubProfile_FiltersBySubProfile()
    {
        var accountId = Guid.NewGuid();
        await _repository.SaveCandidateAsync(
            NewCandidate(accountId, "79000000010", "a-1", DateTime.UtcNow, "sub-a"),
            CancellationToken.None);
        await _repository.SaveCandidateAsync(
            NewCandidate(accountId, "79000000011", "a-2", DateTime.UtcNow, "sub-b"),
            CancellationToken.None);

        var found = await _repository.GetExistingNormalizedPhonesAsync(
            ["79000000010", "79000000011"],
            DuplicateScope.PerAvitoAccount,
            accountId,
            CancellationToken.None,
            "sub-a");

        Assert.Equal(["79000000010"], found.OrderBy(static x => x));
    }

    private static CandidateResponse NewCandidate(
        Guid accountId,
        string phoneNorm,
        string sourceId,
        DateTime createdAt,
        string avitoSubProfileId = "") => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        AccountName = "Acc",
        Source = "Avito",
        SourceResponseId = sourceId,
        FullName = "Test User",
        PhoneRaw = phoneNorm,
        PhoneNormalized = phoneNorm,
        AvitoSubProfileId = avitoSubProfileId,
        Status = ResponseStatus.Sent,
        CreatedAt = createdAt
    };
}
