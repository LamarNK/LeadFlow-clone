using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerOutboundChatServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherWorkerId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task GetPending_ReturnsPlannedForOwnedAccount()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        var (card, message) = SeedPlanned(db, "src-1", "Напишите номер");
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);
        var pending = await sut.GetPendingAsync(WorkerId, AccountId);

        var item = Assert.Single(pending);
        Assert.Equal(message.Id, item.Id);
        Assert.Equal("src-1", item.SourceResponseId);
        Assert.Equal("Напишите номер", item.Text);
        Assert.NotEqual(default, item.QueuedAtUtc);
        Assert.Equal(card.ResponseId, message.ResponseId);
    }

    [Fact]
    public async Task GetPending_OtherWorkerAccount_ReturnsEmpty()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        SeedPlanned(db, "src-1", "Секрет");
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);
        var pending = await sut.GetPendingAsync(OtherWorkerId, AccountId);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task AckSent_MarksPlannedAsSent()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        var (_, message) = SeedPlanned(db, "src-2", "Когда удобно?");
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);
        Assert.True(await sut.ClaimForDeliveryAsync(WorkerId, message.Id));
        var ok = await sut.AckSentAsync(WorkerId, [message.Id]);

        Assert.True(ok);
        var stored = await db.CrmOutboundChatMessages.SingleAsync(x => x.Id == message.Id);
        Assert.Equal(CrmOutboundChatStatuses.Sent, stored.Status);
        Assert.NotNull(stored.SentAtUtc);
        Assert.Contains(db.CrmCandidateHistory, h => h.Action == "ChatSent" && h.CardId == message.CardId);
    }

    [Fact]
    public async Task AckSent_AlreadySent_IsIdempotent()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        var (_, message) = SeedPlanned(db, "src-3", "Повтор");
        message.Status = CrmOutboundChatStatuses.Sent;
        message.SentAtUtc = DateTime.UtcNow.AddMinutes(-3);
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);
        var ok = await sut.AckSentAsync(WorkerId, [message.Id]);

        Assert.True(ok);
        Assert.Equal(CrmOutboundChatStatuses.Sent, message.Status);
        Assert.Empty(db.CrmCandidateHistory.Where(x => x.Action == "ChatSent"));
    }

    [Fact]
    public async Task ClaimForDelivery_CancelledOrClosedMessage_IsNotClaimed()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        var (_, cancelled) = SeedPlanned(db, "src-4", "Не отправлять");
        cancelled.CancelledAtUtc = DateTime.UtcNow;
        var (closedCard, closed) = SeedPlanned(db, "src-5", "Карточка закрыта");
        closedCard.IsClosed = true;
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);

        Assert.False(await sut.ClaimForDeliveryAsync(WorkerId, cancelled.Id));
        Assert.False(await sut.ClaimForDeliveryAsync(WorkerId, closed.Id));
        Assert.Empty(await sut.GetPendingAsync(WorkerId, AccountId));
    }

    [Fact]
    public async Task AckSent_WithoutClaim_DoesNotMarkMessageSent()
    {
        await using var db = CreateDb();
        SeedWorkers(db);
        var (_, message) = SeedPlanned(db, "src-6", "Нужно зарезервировать");
        await db.SaveChangesAsync();

        var sut = new WorkerOutboundChatService(db);

        Assert.True(await sut.AckSentAsync(WorkerId, [message.Id]));
        Assert.Equal(CrmOutboundChatStatuses.Planned, message.Status);
        Assert.Null(message.SentAtUtc);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorkers(OrbitaDbContext db)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Office",
            RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            CrmEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "w1",
            MachineName = "pc",
            ApiKeyHash = "h",
            AppVersion = "1",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = OtherWorkerId,
            OfficeId = OfficeId,
            DisplayName = "w2",
            MachineName = "pc2",
            ApiKeyHash = "h2",
            AppVersion = "1",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            AdsPowerProfileId = "p1",
            DisplayName = "acc",
            IsEnabled = true,
            IsEnabledInPanel = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static (CrmCandidateCardEntity Card, CrmOutboundChatMessageEntity Message) SeedPlanned(
        OrbitaDbContext db,
        string sourceResponseId,
        string text)
    {
        var person = new CandidatePersonEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            FullName = "Иван",
            FirstName = "Иван",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var response = new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = AccountId,
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = sourceResponseId,
            FullName = "Иван",
            PhoneRaw = "79990001122",
            PhoneNormalized = "79990001122",
            Status = ResponseStatuses.New,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        };
        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = OfficeId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            StageChangedAtUtc = DateTime.UtcNow
        };
        var message = new CrmOutboundChatMessageEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            ResponseId = response.Id,
            AuthorUserId = "mgr",
            AuthorName = "Менеджер",
            Text = text,
            Status = CrmOutboundChatStatuses.Planned,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        db.CrmCandidateCards.Add(card);
        db.CrmOutboundChatMessages.Add(message);
        return (card, message);
    }
}
