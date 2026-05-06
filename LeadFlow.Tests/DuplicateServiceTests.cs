using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Bitrix;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class DuplicateServiceTests
{
    [Fact]
    public async Task LocalDuplicate_BitrixCheckOff_DoesNotCallBitrix()
    {
        var repo = new FakeDuplicateRepository
        {
            Lookup = (_, _, _) => new CandidateResponse { Id = Guid.NewGuid() }
        };
        var bitrix = new FakeBitrixClient();
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);
        var settings = NewSettings(checkBitrixDuplicates: false);
        var response = NewResponse();

        var result = await sut.CheckAsync(response, settings, CancellationToken.None);

        Assert.True(result.IsLocalDuplicate);
        Assert.True(result.IsDuplicate);
        Assert.Equal(0, bitrix.HasDuplicateCallCount);
    }

    [Fact]
    public async Task NoLocal_BitrixDuplicate_ReturnsBitrixDuplicate()
    {
        var repo = new FakeDuplicateRepository();
        var bitrix = new FakeBitrixClient
        {
            HasDuplicateImpl = (_, _) => new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Duplicate)
        };
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);

        var result = await sut.CheckAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.False(result.IsLocalDuplicate);
        Assert.True(result.IsBitrixDuplicate);
        Assert.True(result.IsDuplicate);
    }

    [Fact]
    public async Task BitrixUnavailable_DefersBitrixSend_AndPropagatesReason()
    {
        var repo = new FakeDuplicateRepository();
        var bitrix = new FakeBitrixClient
        {
            HasDuplicateImpl = (_, _) =>
                new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Unavailable, "HTTP 500")
        };
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);

        var result = await sut.CheckAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.True(result.IsBitrixCheckUnavailable);
        Assert.True(result.ShouldDeferBitrixSend);
        Assert.Equal("HTTP 500", result.BitrixCheckUnavailableReason);
        Assert.False(result.IsBitrixDuplicate);
    }

    [Fact]
    public async Task BitrixSkipped_NoLocal_NotDuplicate()
    {
        var repo = new FakeDuplicateRepository();
        var bitrix = new FakeBitrixClient
        {
            HasDuplicateImpl = (_, _) => new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Skipped)
        };
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);

        var result = await sut.CheckAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.False(result.IsBitrixCheckUnavailable);
    }

    [Fact]
    public async Task CheckAsync_WritesNormalizedPhoneIntoResponse()
    {
        var repo = new FakeDuplicateRepository();
        var bitrix = new FakeBitrixClient();
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);

        var response = NewResponse();
        response.PhoneRaw = "+7 (900) 000-00-00";

        await sut.CheckAsync(response, NewSettings(), CancellationToken.None);

        Assert.Equal("79000000000", response.PhoneNormalized);
        Assert.Equal("79000000000", repo.LastPhoneArgument);
    }

    [Fact]
    public async Task CheckAsync_PassesDuplicateScopeFromSettings()
    {
        var repo = new FakeDuplicateRepository();
        var sut = new DuplicateService(repo, new PhoneNormalizer(), new FakeBitrixClient());

        await sut.CheckAsync(NewResponse(), NewSettings(scope: DuplicateScope.PerAvitoAccount), CancellationToken.None);

        Assert.Equal(DuplicateScope.PerAvitoAccount, repo.LastScopeArgument);
    }

    [Fact]
    public async Task EmptyPhone_SkipsLocalDuplicateLookup()
    {
        var repo = new FakeDuplicateRepository
        {
            Lookup = (_, _, _) => new CandidateResponse { Id = Guid.NewGuid() }
        };
        var bitrix = new FakeBitrixClient();
        var sut = new DuplicateService(repo, new PhoneNormalizer(), bitrix);
        var response = NewResponse();
        response.PhoneRaw = string.Empty;

        var result = await sut.CheckAsync(response, NewSettings(), CancellationToken.None);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsLocalDuplicate);
        Assert.False(result.IsDuplicate);
    }

    private static AppSettings NewSettings(
        bool checkBitrixDuplicates = true,
        DuplicateScope scope = DuplicateScope.GlobalAcrossAllAccounts) => new()
    {
        DuplicateScope = scope,
        Bitrix = new BitrixSettings
        {
            CheckDuplicatesInBitrix = checkBitrixDuplicates,
            WebhookUrl = "https://b24.example/rest/1/abc/"
        }
    };

    private static CandidateResponse NewResponse() => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        PhoneRaw = "+7 900 000-00-00",
        FullName = "Иванов Иван"
    };
}
