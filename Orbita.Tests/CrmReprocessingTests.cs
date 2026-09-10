using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmReprocessingTests
{
    private static readonly Guid Source = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Destination = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid TestOffice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly DateTime From = DateTimeOffset.Parse("2026-09-07T00:00:00+05:00").UtcDateTime;

    [Fact]
    public async Task PersistenceFailureRollsBackCardHistoryTasksAndContacts()
    {
        var failure = new FailureInterceptor();
        await using var h = await Harness.Create(true, failure);
        var card = h.Card(From);
        h.Db.CrmTasks.Add(new CrmTaskEntity
        {
            Id = Guid.NewGuid(), CardId = card.Id, OfficeId = Source,
            AssigneeUserId = "former", CreatorUserId = "former", Status = CrmTaskStatuses.Open
        });
        await h.Db.SaveChangesAsync();
        failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        h.Db.ChangeTracker.Clear();
        Assert.Equal(Source, (await h.Db.CrmCandidateCards.SingleAsync()).OfficeId);
        Assert.True((await h.Db.CrmCandidateCards.SingleAsync()).IsClosed);
        Assert.Null((await h.Db.CrmCandidateHistory.SingleAsync()).OfficeId);
        Assert.Equal(CrmTaskStatuses.Open, (await h.Db.CrmTasks.SingleAsync()).Status);
        Assert.Empty(await h.Db.CandidateContactPhones.ToListAsync());
        failure.Enabled = false;
        Assert.Equal(1, await h.Run());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cutoff_CurrentState_OfficeScope_HistoryAndRetry(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var selected = new[] { h.Card(From), h.Card(From.AddDays(1)), h.Card(From.AddDays(2)) };
        var before = h.Card(From.AddTicks(-1));
        var test = h.Card(From, office: TestOffice);
        var inDestination = h.Card(From, office: Destination);
        var otherReason = h.Card(From, reason: CrmCloseReasons.NotRelevant);
        var reopened = h.Card(From); reopened.IsClosed = false;
        var noDate = h.Card(From); noDate.ClosedAtUtc = null;
        var task = new CrmTaskEntity { Id = Guid.NewGuid(), CardId = selected[0].Id, OfficeId = Source,
            AssigneeUserId = "former", CreatorUserId = "former", Status = CrmTaskStatuses.Open };
        h.Db.CrmTasks.Add(task);
        var note = new CrmCandidateNoteEntity { Id = Guid.NewGuid(), CardId = selected[0].Id, Text = "keep" };
        h.Db.CrmCandidateNotes.Add(note);
        await h.Db.SaveChangesAsync();
        var closureIds = await h.Db.CrmCandidateHistory.Where(x => x.Action == "Closed").Select(x => x.Id).ToArrayAsync();
        Assert.Equal(3, await h.Run());
        foreach (var card in selected)
        {
            Assert.Equal(Destination, card.OfficeId);
            Assert.Equal(Source, card.EntryOfficeId);
            Assert.Equal("former", card.InitialManagerUserId);
            Assert.Null(card.ManagerUserId); // No shift: destination queue.
            Assert.False(card.IsClosed);
            Assert.Null(card.CloseReason);
            Assert.Null(card.ClosedAtUtc);
            Assert.Null(card.NextActionAtUtc);
            Assert.Equal(CrmStages.Lead, card.Stage);
            Assert.Equal(From.AddDays(-10), card.EnteredCrmAtUtc);
            var close = await h.Db.CrmCandidateHistory.SingleAsync(x => x.CardId == card.Id && x.Action == "Closed");
            Assert.Equal(Source, close.OfficeId);
            Assert.Equal("former", close.ActorUserId);
            Assert.True(close.ContextInferred);
            Assert.Equal(Source, (await h.Db.CrmCandidateHistory.SingleAsync(x => x.CardId == card.Id && x.Action == "Reopened")).OfficeId);
            Assert.Equal(Destination, (await h.Db.CrmCandidateHistory.SingleAsync(x => x.CardId == card.Id && x.Action == "ReprocessingReceived")).OfficeId);
            Assert.Equal(Source, (await h.Db.CandidateResponses.SingleAsync(x => x.Id == card.ResponseId)).OfficeId);
        }
        Assert.Equal(CrmTaskStatuses.Cancelled, task.Status);
        Assert.True(await h.Db.CrmCandidateNotes.AnyAsync(x => x.Id == note.Id));
        Assert.Equal(closureIds.Length, await h.Db.CrmCandidateHistory.CountAsync(x => x.Action == "Closed"));
        Assert.Equal(Source, before.OfficeId);
        Assert.Equal(TestOffice, test.OfficeId);
        Assert.True(inDestination.IsClosed);
        Assert.True(otherReason.IsClosed);
        Assert.False(reopened.IsClosed);
        Assert.True(noDate.IsClosed);
        Assert.Equal(0, await h.Run());
        selected[0].IsClosed = true; selected[0].CloseReason = CrmCloseReasons.NoAnswer; selected[0].ClosedAtUtc = From.AddDays(3);
        await h.Db.SaveChangesAsync();
        Assert.Equal(0, await h.Run()); // NDZ inside destination never loops.
        h.Card(From.AddDays(5)); await h.Db.SaveChangesAsync();
        Assert.Equal(1, await h.Run()); // Subsequent days are picked up too.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrUnreadyRuleCannotMoveCards_AndCanRetry(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var card = h.Card(From); await h.Db.SaveChangesAsync();
        h.Settings.Enabled = false;
        Assert.Equal(0, await h.Run());
        h.Settings.Enabled = true; h.Settings.ClosedFromUtc = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        h.Settings.ClosedFromUtc = new DateTimeOffset(From);
        var office = await h.Db.Offices.SingleAsync(x => x.Id == Destination);
        office.CrmEnabled = false; await h.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.Equal(Source, card.OfficeId); Assert.True(card.IsClosed);
        office.CrmEnabled = true; office.CrmStagesJson = "[\"Переговоры\"]"; await h.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        office.CrmStagesJson = null; await h.Db.SaveChangesAsync();
        Assert.Equal(1, await h.Run());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchesAreBoundedAndNewServiceResumesWithoutDuplicates(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        for (var i = 0; i < 5; i++) h.Card(From.AddMinutes(i));
        await h.Db.SaveChangesAsync();
        h.Settings.BatchSize = 2;
        Assert.Equal(2, await h.Run());
        Assert.Equal(2, await h.Run());
        Assert.Equal(1, await h.Run());
        Assert.Equal(0, await h.Run());
        Assert.Equal(5, await h.Db.CrmCandidateCards.CountAsync());
        Assert.Equal(5, await h.Db.CrmCandidateHistory.CountAsync(x => x.Action == "ReprocessingSent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleManagerWriteCannotUndoOfficeTransfer(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var card = h.Card(From); await h.Db.SaveChangesAsync();
        await using var staleDb = new OrbitaDbContext(h.DbOptions);
        var stale = await staleDb.CrmCandidateCards.SingleAsync(x => x.Id == card.Id);
        Assert.Equal(1, await h.Run());
        stale.Stage = CrmStages.Negotiations;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        h.Db.ChangeTracker.Clear();
        Assert.Equal(CrmStages.Lead, (await h.Db.CrmCandidateCards.SingleAsync()).Stage);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection? connection;
        public DbContextOptions<OrbitaDbContext> DbOptions { get; }
        public OrbitaDbContext Db { get; }
        public CrmReprocessingOptions Settings { get; } = new()
        {
            Enabled = true, DestinationOfficeId = Destination, SourceOfficeNames = ["1 офис"],
            ClosedFromUtc = new DateTimeOffset(From)
        };
        private Harness(DbContextOptions<OrbitaDbContext> options, SqliteConnection? connection)
        { DbOptions = options; Db = new(options); this.connection = connection; }
        public static async Task<Harness> Create(bool sqlite, SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<OrbitaDbContext>();
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            SqliteConnection? connection = null;
            if (sqlite)
            {
                connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
                await connection.OpenAsync(); builder.UseSqlite(connection);
            }
            else builder.UseInMemoryDatabase(Guid.NewGuid().ToString());
            var h = new Harness(builder.Options, connection);
            await h.Db.Database.EnsureCreatedAsync();
            h.Db.Offices.AddRange(new OfficeEntity { Id = Source, Name = "1 офис", CrmEnabled = true },
                new OfficeEntity { Id = Destination, Name = "Повторная обработка", CrmEnabled = true },
                new OfficeEntity { Id = TestOffice, Name = "Тест Офис", CrmEnabled = true });
            await h.Db.SaveChangesAsync(); return h;
        }
        public CrmCandidateCardEntity Card(DateTime closed, Guid? office = null, string reason = CrmCloseReasons.NoAnswer)
        {
            var person = new CandidatePersonEntity { Id = Guid.NewGuid(), PhoneRaw = "70000000000", PhoneNormalized = "70000000000" };
            var response = new CandidateResponseEntity { Id = Guid.NewGuid(), PersonId = person.Id,
                OfficeId = office ?? Source, PhoneRaw = person.PhoneRaw, PhoneNormalized = person.PhoneNormalized };
            var card = new CrmCandidateCardEntity { Id = Guid.NewGuid(), ResponseId = response.Id, OfficeId = office ?? Source,
                IsClosed = true, CloseReason = reason, ClosedAtUtc = closed, Stage = CrmStages.Ndz73,
                ManagerUserId = "former", CreatedAtUtc = From.AddDays(-10), NextActionAtUtc = closed };
            Db.CandidatePersons.Add(person); Db.CandidateResponses.Add(response); Db.CrmCandidateCards.Add(card);
            Db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity { Id = Guid.NewGuid(), CardId = card.Id,
                Action = "Closed", Details = reason, ActorUserId = "former", CreatedAtUtc = closed });
            return card;
        }
        public Task<int> Run() => new CrmReprocessingService(Db, new CrmLeadDistributionService(Db, null!), Options.Create(Settings)).ProcessBatchAsync();
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); if (connection is not null) await connection.DisposeAsync(); }
    }

    private sealed class FailureInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Simulated persistence failure");
            return ValueTask.FromResult(result);
        }
    }
}
