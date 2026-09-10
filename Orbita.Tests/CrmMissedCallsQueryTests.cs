using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmMissedCallsQueryTests
{
    private static readonly DateTime From = DateTimeOffset.Parse("2026-09-10T00:00:00+05:00").UtcDateTime;
    private static readonly DateTime To = From.AddDays(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiltersRealIncomingEvents_UsesCurrentOwner_AndKeepsUnmatchedCalls(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var owned = h.Card("a");
        var other = h.Card("b");
        var mine = h.Call(owned, "b"); // known card routes to its responsible, not an old extension
        var unknown = h.Call(null, "a");
        var failed = h.Call(owned, "a", CrmCallStatuses.Failed);
        var rejected = h.Call(owned, "a", CrmCallStatuses.Rejected);
        h.Call(other, "a");
        h.Call(null, null); // visible to the office lead, not silently attributed to every manager
        h.Call(owned, "a", CrmCallStatuses.Answered);
        h.Call(owned, "a", CrmCallStatuses.Unknown);
        h.Call(owned, "a", direction: CrmCallDirections.Outgoing);
        h.Call(owned, "a", at: From.AddTicks(-1));
        h.Call(owned, "a", at: To);
        h.Call(null, "a", office: h.OtherOffice);
        await h.Db.SaveChangesAsync();
        var result = Assert.IsType<CrmMissedCallsDto>(await h.Query.GetAsync(h.Office, "a", false, false,
            From, To, managerUserId: "b")); // arbitrary manager filter cannot expand access
        Assert.Equal(4, result.Total);
        Assert.Equal(2, result.Missed); Assert.Equal(1, result.Rejected); Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { mine, unknown, failed, rejected }.Order(), result.Rows.Select(x => x.Id).Order());
        Assert.Null(result.Rows.Single(x => x.Id == unknown).CardId);
        Assert.Equal(owned, result.Rows.Single(x => x.Id == mine).CardId);
        Assert.Equal("Кандидат a", result.Rows.Single(x => x.Id == mine).CandidateName);
        Assert.Equal("Менеджер a", result.Rows.Single(x => x.Id == mine).ResponsibleName);
        var lead = await h.Query.GetAsync(h.Office, "lead", true, false, From, To);
        Assert.Equal(6, lead!.Total);
        Assert.Contains(lead.Rows, x => x.ResponsibleName == null);
        Assert.Equal(7, (await h.Query.GetAsync(null, "admin", true, true, From, To))!.Total);
        Assert.Equal(1, (await h.Query.GetAsync(h.Office, "lead", true, false, From, To,
            managerUserId: "b"))!.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagingStatusAndReadAlerts_DoNotLoseCallHistory(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        for (var i = 0; i < 35; i++) h.Call(null, "a", at: From.AddMinutes(i));
        var rejected = h.Call(null, "a", CrmCallStatuses.Rejected);
        h.Db.CrmDeskAlerts.Add(new CrmDeskAlertEntity { Id = rejected, OfficeId = h.Office,
            RecipientUserId = "a", Kind = CrmTaskNotificationKinds.MissedCall, ReadAtUtc = To });
        await h.Db.SaveChangesAsync();
        var first = (await h.Query.GetAsync(h.Office, "a", false, false, From, To))!;
        var second = (await h.Query.GetAsync(h.Office, "a", false, false, From, To, page: 2))!;
        Assert.Equal(36, first.Total); Assert.Equal(30, first.Rows.Count); Assert.Equal(6, second.Rows.Count);
        Assert.Empty(first.Rows.Select(x => x.Id).Intersect(second.Rows.Select(x => x.Id)));
        var filtered = (await h.Query.GetAsync(h.Office, "a", false, false, From, To,
            status: CrmCallStatuses.Rejected, page: int.MaxValue))!;
        Assert.Equal(1, filtered.Page); Assert.Equal(rejected, Assert.Single(filtered.Rows).Id);
        Assert.Equal(35, filtered.Missed); // cards summarize all statuses with the same manager/date
        Assert.Equal(1, filtered.Rejected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationDeepLinkAndOfficeGuards(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var oldCall = h.Call(null, "a", at: From.AddDays(-90));
        var foreignCall = h.Call(null, "b", office: h.OtherOffice);
        await h.Db.SaveChangesAsync();
        Assert.Null(await h.Query.GetAsync(h.OtherOffice, "a", true, false, From, To));
        Assert.Null(await h.Query.GetAsync(null, "a", false, false, From, To));
        Assert.Null(await h.Query.GetAsync(h.Office, "a", false, false, To, From));
        Assert.Null(await h.Query.GetAsync(h.Office, "a", false, false, From, From.AddDays(367)));
        Assert.Equal(oldCall, Assert.Single((await h.Query.GetAsync(h.Office, "a", false, false,
            From, To, callId: oldCall))!.Rows).Id);
        Assert.Empty((await h.Query.GetAsync(h.Office, "a", false, false, From, To, callId: foreignCall))!.Rows);
        (await h.Db.Offices.SingleAsync(x => x.Id == h.Office)).CrmEnabled = false;
        await h.Db.SaveChangesAsync();
        Assert.Empty((await h.Query.GetAsync(h.Office, "admin", true, true, From, To, callId: oldCall))!.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LineBindingDoesNotGrantAccessToUnassignedOrTransferredCard(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var card = h.Card(null);
        var id = h.Call(card, "a");
        await h.Db.SaveChangesAsync();
        var row = Assert.Single((await h.Query.GetAsync(h.Office, "a", false, false, From, To))!.Rows);
        Assert.Equal(id, row.Id);
        Assert.Null(row.CardId); Assert.Null(row.CandidateName);
        Assert.Equal(card, Assert.Single((await h.Query.GetAsync(h.Office, "lead", true, false, From, To))!.Rows).CardId);
        (await h.Db.CrmCandidateCards.SingleAsync(x => x.Id == card)).OfficeId = h.OtherOffice;
        await h.Db.SaveChangesAsync();
        Assert.Null(Assert.Single((await h.Query.GetAsync(h.Office, "a", false, false, From, To))!.Rows).CardId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Guid Office { get; } = Guid.NewGuid();
        public Guid OtherOffice { get; } = Guid.NewGuid();
        public OrbitaDbContext Db { get; }
        public CrmMissedCallsQueryService Query { get; }
        private readonly SqliteConnection? connection;
        private Harness(OrbitaDbContext db, SqliteConnection? connection)
        { Db = db; Query = new(db); this.connection = connection; }
        public static async Task<Harness> Create(bool sqlite)
        {
            SqliteConnection? connection = null;
            var builder = new DbContextOptionsBuilder<OrbitaDbContext>();
            if (sqlite)
            {
                connection = new("Data Source=:memory:;Foreign Keys=False");
                await connection.OpenAsync(); builder.UseSqlite(connection);
            }
            else builder.UseInMemoryDatabase(Guid.NewGuid().ToString());
            var h = new Harness(new OrbitaDbContext(builder.Options), connection);
            await h.Db.Database.EnsureCreatedAsync();
            h.Db.Offices.AddRange(new OfficeEntity { Id = h.Office, Name = "Тестовый офис", IsEnabled = true, CrmEnabled = true },
                new OfficeEntity { Id = h.OtherOffice, Name = "Другой офис", IsEnabled = true, CrmEnabled = true });
            foreach (var id in new[] { "a", "b", "lead" })
                h.Db.PanelUserProfiles.Add(new PanelUserProfileEntity { UserId = id, OfficeId = h.Office, FullName = "Менеджер " + id });
            await h.Db.SaveChangesAsync(); return h;
        }
        public Guid Card(string? manager)
        {
            var person = TestCandidatePersonFactory.CreatePerson(Office);
            Db.CandidatePersons.Add(person);
            var response = TestCandidatePersonFactory.CreateResponse(Office, person.Id);
            response.FullName = "Кандидат " + manager;
            Db.CandidateResponses.Add(response);
            var card = new CrmCandidateCardEntity { Id = Guid.NewGuid(), OfficeId = Office, ResponseId = response.Id,
                ManagerUserId = manager, Stage = CrmStages.Lead, CreatedAtUtc = From };
            Db.CrmCandidateCards.Add(card); return card.Id;
        }
        public Guid Call(Guid? card, string? manager, string status = CrmCallStatuses.Missed,
            string direction = CrmCallDirections.Incoming, DateTime? at = null, Guid? office = null)
        {
            var call = new CrmCallEntity { Id = Guid.NewGuid(), OfficeId = office ?? Office, CardId = card,
                ManagerUserId = manager, Status = status, Direction = direction, StartedAtUtc = at ?? From,
                Provider = CrmTelephonyProviders.Asterisk, ExternalCallId = Guid.NewGuid().ToString(),
                ClientPhoneNormalized = "79990001122", CalledPhone = "74950000000" };
            Db.CrmCalls.Add(call); return call.Id;
        }
        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); if (connection is not null) await connection.DisposeAsync(); }
    }
}
