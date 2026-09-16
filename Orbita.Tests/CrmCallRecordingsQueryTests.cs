using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmCallRecordingsQueryTests
{
    private static readonly DateTime From = DateTimeOffset.Parse("2026-09-15T00:00:00+05:00").UtcDateTime;
    private static readonly DateTime To = From.AddDays(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveUsesCurrentOwner_KeepsUnlinkedCalls_AndFiltersSavedAudio(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var card = h.Card("a");
        var owned = h.Call(card, "b");
        var unassigned = h.Call(h.Card(null), "b");
        var unlinked = h.Call(null, "b");
        var notArchived = h.Call(card, "a"); notArchived.RecordingStoragePath = null;
        notArchived.RecordingUrl = "https://example.test/provider-recording";
        h.Call(card, "a", at: From.AddTicks(-1));
        h.Call(card, "a", at: To);
        await h.Db.SaveChangesAsync();
        var result = (await h.Query.GetAsync(null, h.Lead, From, To))!;
        Assert.Equal(3, result.Total);
        var row = Assert.Single(result.Rows, x => x.Id == owned.Id);
        Assert.Equal(card, row.CardId); Assert.Equal("Кандидат a", row.CandidateName);
        Assert.Equal("Сотрудник a", row.ResponsibleName);
        Assert.Null(Assert.Single(result.Rows, x => x.Id == unassigned.Id).ResponsibleName);
        Assert.Null(Assert.Single(result.Rows, x => x.Id == unlinked.Id).CardId);
        Assert.Equal("Сотрудник b", Assert.Single(result.Rows, x => x.Id == unlinked.Id).ResponsibleName);
        Assert.Equal(owned.Id, Assert.Single((await h.Query.GetAsync(null, h.Lead, From, To, managerUserId: "a"))!.Rows).Id);
        Assert.Equal(unlinked.Id, Assert.Single((await h.Query.GetAsync(null, h.Lead, From, To, managerUserId: "b"))!.Rows).Id);
        Assert.Equal(2, result.Managers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothListAndAudioEnforceRoles_CurrentOffice_AndCardOffice(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var own = h.Call(h.Card("a"), "a");
        var foreign = h.Call(null, "b", office: h.OtherOffice);
        var movedCard = h.Card("a");
        var moved = h.Call(movedCard, "a");
        h.Db.CrmCandidateCards.Local.Single(x => x.Id == movedCard).OfficeId = h.OtherOffice;
        await h.SaveAudio(own, foreign, moved);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(h.Admin.Claims));
        Assert.Null(await h.Query.GetAsync(null, anonymous, From, To));
        Assert.Null((await h.Query.OpenAsync(own.Id, anonymous)).Stream);
        foreach (var role in new[] { PanelRoles.Manager, PanelRoles.SeniorManager, PanelRoles.Operator, "" })
        {
            var actor = Principal("lead", role, h.Office);
            Assert.Null(await h.Query.GetAsync(h.Office, actor, From, To));
            Assert.Null((await h.Query.OpenAsync(own.Id, actor)).Stream);
        }
        Assert.Null(await h.Query.GetAsync(h.OtherOffice, h.Lead, From, To));
        Assert.Null(await h.Query.GetAsync(null, Principal("unknown", PanelRoles.OfficeLead, h.Office), From, To));
        var staleClaim = Principal("lead", PanelRoles.OfficeLead, h.OtherOffice);
        Assert.Equal(own.Id, Assert.Single((await h.Query.GetAsync(null, staleClaim, From, To))!.Rows).Id);
        Assert.Null((await h.Query.OpenAsync(foreign.Id, staleClaim)).Stream);
        Assert.Null((await h.Query.OpenAsync(moved.Id, h.Lead)).Stream);
        Assert.Equal(2, (await h.Query.GetAsync(null, h.Admin, From, To))!.Total);
        Assert.Equal(foreign.Id, Assert.Single((await h.Query.GetAsync(h.OtherOffice, h.Admin, From, To))!.Rows).Id);
        await using (var audio = (await h.Query.OpenAsync(foreign.Id, h.Admin)).Stream)
            Assert.NotNull(audio);
        // A role/office reassignment takes effect even when an older browser page still has the link.
        h.Db.PanelUserProfiles.Local.Single(x => x.UserId == "lead").OfficeId = h.OtherOffice;
        await h.Db.SaveChangesAsync();
        Assert.Null((await h.Query.OpenAsync(own.Id, h.Lead)).Stream);
        await using (var audio = (await h.Query.OpenAsync(foreign.Id, h.Lead)).Stream)
            Assert.NotNull(audio);
        h.Db.Offices.Local.Single(x => x.Id == h.OtherOffice).CrmEnabled = false;
        await h.Db.SaveChangesAsync();
        Assert.Empty((await h.Query.GetAsync(null, h.Lead, From, To))!.Rows);
        Assert.Null((await h.Query.OpenAsync(foreign.Id, h.Admin)).Stream);
        h.Db.Offices.Local.Single(x => x.Id == h.Office).IsEnabled = false;
        await h.Db.SaveChangesAsync();
        Assert.Empty((await h.Query.GetAsync(null, h.Admin, From, To))!.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiltersAndStablePaging_RejectInvalidRequests(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        for (var i = 0; i < 35; i++) h.Call(null, "a", at: From); // ties must not overlap pages
        var outgoing = h.Call(null, "b", direction: CrmCallDirections.Outgoing);
        outgoing.ClientPhoneNormalized = "79223334455";
        await h.Db.SaveChangesAsync();
        var first = (await h.Query.GetAsync(null, h.Lead, From, To, page: -1))!;
        var second = (await h.Query.GetAsync(null, h.Lead, From, To, page: int.MaxValue))!;
        Assert.Equal(36, first.Total); Assert.Equal(30, first.PageSize); Assert.Equal(30, first.Rows.Count);
        Assert.Equal(1, first.Page); Assert.Equal(2, second.Page); Assert.Equal(6, second.Rows.Count);
        Assert.Empty(first.Rows.Select(x => x.Id).Intersect(second.Rows.Select(x => x.Id)));
        foreach (var search in new[] { "+7 (922) 333-44-55", "8 (922) 333-44-55", "33344" })
            Assert.Equal(outgoing.Id, Assert.Single((await h.Query.GetAsync(null, h.Lead, From, To, phone: search))!.Rows).Id);
        Assert.Equal(outgoing.Id, Assert.Single((await h.Query.GetAsync(null, h.Lead, From, To,
            direction: CrmCallDirections.Outgoing))!.Rows).Id);
        Assert.Equal(35, (await h.Query.GetAsync(null, h.Lead, From, To, direction: CrmCallDirections.Incoming))!.Total);
        Assert.Empty((await h.Query.GetAsync(null, h.Lead, From, To, managerUserId: "missing"))!.Rows);
        Assert.Null(await h.Query.GetAsync(null, h.Lead, To, From));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, From));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, From.AddDays(367)));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, To, phone: "xx"));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, To, phone: "12"));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, To, phone: new string('7', 65)));
        Assert.Null(await h.Query.GetAsync(null, h.Lead, From, To, direction: "invalid"));
        Assert.Null(await h.Query.GetAsync(Guid.Empty, h.Admin, From, To));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioReadsExistingArchive_WithMetadata_AndMissingFileIsUnavailable(bool sqlite)
    {
        await using var h = await Harness.Create(sqlite);
        var call = h.Call(null, "a");
        call.RecordingFileName = "Разговор.wav"; call.RecordingContentType = "audio/wav";
        await h.SaveAudio(call);
        var result = await h.Query.OpenAsync(call.Id, h.Lead);
        await using (var stream = result.Stream)
        {
            Assert.NotNull(stream); Assert.True(stream.CanSeek);
            Assert.Equal("Разговор.wav", result.FileName); Assert.Equal("audio/wav", result.ContentType);
            Assert.Equal(Harness.Audio, await ReadAll(stream));
        }
        h.Storage.TryDelete(call.RecordingStoragePath!);
        Assert.Null((await h.Query.OpenAsync(call.Id, h.Lead)).Stream);
        Assert.Null((await h.Query.OpenAsync(Guid.NewGuid(), h.Lead)).Stream);
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    { using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); return buffer.ToArray(); }

    private static ClaimsPrincipal Principal(string id, string role, Guid? office = null) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Role, role), new Claim(OfficeClaims.OfficeId, office?.ToString() ?? ""),
            new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmBoard) }, "test"));

    private sealed class Harness : IAsyncDisposable
    {
        public static readonly byte[] Audio = [82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69];
        public Guid Office { get; } = Guid.NewGuid();
        public Guid OtherOffice { get; } = Guid.NewGuid();
        public ClaimsPrincipal Lead => Principal("lead", PanelRoles.OfficeLead, Office);
        public ClaimsPrincipal Admin => Principal("admin", PanelRoles.Admin);
        public OrbitaDbContext Db { get; }
        public CrmCallRecordingsQueryService Query { get; }
        public CrmCallRecordingStorageService Storage { get; }
        private readonly SqliteConnection? connection;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "orbita-recordings-test-" + Guid.NewGuid().ToString("N"));
        private Harness(OrbitaDbContext db, SqliteConnection? connection)
        {
            Db = db; this.connection = connection;
            Storage = new(Options.Create(new CrmCallRecordingOptions { DataPath = directory }));
            Query = new(db, Storage);
        }
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
                h.Db.PanelUserProfiles.Add(new PanelUserProfileEntity { UserId = id, OfficeId = h.Office, FullName = "Сотрудник " + id });
            await h.Db.SaveChangesAsync(); return h;
        }
        public Guid Card(string? manager)
        {
            var person = TestCandidatePersonFactory.CreatePerson(Office); Db.CandidatePersons.Add(person);
            var response = TestCandidatePersonFactory.CreateResponse(Office, person.Id);
            response.FullName = "Кандидат " + manager; Db.CandidateResponses.Add(response);
            var card = new CrmCandidateCardEntity { Id = Guid.NewGuid(), OfficeId = Office, ResponseId = response.Id,
                ManagerUserId = manager, Stage = CrmStages.Lead, CreatedAtUtc = From };
            Db.CrmCandidateCards.Add(card); return card.Id;
        }
        public CrmCallEntity Call(Guid? card, string? manager, DateTime? at = null, Guid? office = null,
            string direction = CrmCallDirections.Incoming)
        {
            var call = new CrmCallEntity { Id = Guid.NewGuid(), OfficeId = office ?? Office, CardId = card,
                ManagerUserId = manager, Direction = direction, StartedAtUtc = at ?? From, DurationSeconds = 75,
                Provider = CrmTelephonyProviders.Asterisk, ExternalCallId = Guid.NewGuid().ToString(),
                ClientPhoneNormalized = "79990001122", RecordingStoragePath = "fixture.bin" };
            Db.CrmCalls.Add(call); return call;
        }
        public async Task SaveAudio(params CrmCallEntity[] calls)
        {
            foreach (var call in calls)
            {
                using var source = new MemoryStream(Audio);
                call.RecordingStoragePath = await Storage.SaveAsync(call.Id, source, CancellationToken.None);
            }
            await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); if (connection is not null) await connection.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
