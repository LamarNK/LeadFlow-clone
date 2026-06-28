using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AppRepositorySqliteUniqueIndexTests
{
    [Fact]
    public async Task SaveCandidate_MultipleEmptyOrWhitespaceSourceIds_SameAccount_Succeeds()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repo = new AppRepository(new TestAppDbContextFactory(options));
        var accountId = Guid.NewGuid();
        await repo.SaveCandidateAsync(MinResponse(Guid.NewGuid(), accountId, string.Empty), CancellationToken.None);
        await repo.SaveCandidateAsync(MinResponse(Guid.NewGuid(), accountId, "  "), CancellationToken.None);
        await repo.SaveCandidateAsync(MinResponse(Guid.NewGuid(), accountId, "\t"), CancellationToken.None);
    }

    [Fact]
    public async Task Sqlite_PartialUniqueIndex_StillBlocksDuplicateNonEmptySourceId()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var accountId = Guid.NewGuid();
        var sharedSource = "src-dup";
        var baseEntity = new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AccountName = "A",
            Source = "Avito",
            SourceResponseId = sharedSource,
            FullName = "One",
            Status = nameof(ResponseStatus.New),
            CreatedAt = DateTime.UtcNow
        };

        await using (var db = new AppDbContext(options))
        {
            db.CandidateResponses.Add(baseEntity);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                AccountName = "A",
                Source = "Avito",
                SourceResponseId = sharedSource,
                FullName = "Two",
                Status = nameof(ResponseStatus.New),
                CreatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    private static CandidateResponse MinResponse(Guid id, Guid accountId, string sourceResponseId) => new()
    {
        Id = id,
        AccountId = accountId,
        AccountName = "A",
        Source = "Avito",
        SourceResponseId = sourceResponseId,
        FullName = "N",
        PhoneRaw = "+7000",
        PhoneNormalized = "7000",
        Status = ResponseStatus.New,
        CreatedAt = DateTime.UtcNow
    };
}
