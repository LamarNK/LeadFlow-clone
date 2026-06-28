using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using CandidateParser = Orbita.Api.Services.CandidateParser;

namespace Orbita.Tests;

public sealed class CandidateIngestionServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task IngestBatchAsync_LocalDuplicate_StoresDuplicateStatus()
    {
        await using var db = CreateDb();
        SeedWorker(db);

        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = "existing-source",
            FullName = "Existing User",
            PhoneRaw = "+7 (900) 111-11-11",
            PhoneNormalized = "79001111111",
            Status = ResponseStatuses.Sent,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "new-source-id",
                "New User",
                25,
                "+7 (900) 111-11-11",
                "Москва",
                "Курьер",
                "",
                "",
                "",
                "",
                DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.Duplicate, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "new-source-id");
        Assert.Equal(ResponseStatuses.Duplicate, stored.Status);
        Assert.True(stored.IsLocalDuplicate);
    }

    private static CandidateIngestionService CreateService(OrbitaDbContext db)
    {
        var duplicateService = new CandidateDuplicateService(db, new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()));
        var webhookResolver = new OfficeBitrixWebhookResolver(db, null!, null!);
        return new CandidateIngestionService(
            db,
            new PhoneNormalizer(),
            new CandidateParser(),
            duplicateService,
            webhookResolver,
            new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()),
            Options.Create(new OrbitaBitrixSettings { CheckDuplicatesInBitrix = false }));
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(OrbitaDbContext db)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker-1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}