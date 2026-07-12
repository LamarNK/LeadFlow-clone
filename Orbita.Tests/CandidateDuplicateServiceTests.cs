using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using CandidateParser = Orbita.Api.Services.CandidateParser;

namespace Orbita.Tests;

public sealed class CandidateDuplicateServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CheckAsync_LocalDuplicate_IsScopedToPersonWithinOffice()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");
        SeedOffice(db, OfficeB, "Office B");

        var personA = TestCandidatePersonFactory.CreatePerson(OfficeA);
        var personB = TestCandidatePersonFactory.CreatePerson(OfficeB);
        db.CandidatePersons.AddRange(personA, personB);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(OfficeA, personA.Id));
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(OfficeB, personB.Id));
        await db.SaveChangesAsync();

        var currentPerson = TestCandidatePersonFactory.CreatePerson(OfficeA, fullName: "Another User", lastName: "Another");
        db.CandidatePersons.Add(currentPerson);
        var current = TestCandidatePersonFactory.CreateResponse(OfficeA, currentPerson.Id, fullName: "Another User");
        db.CandidateResponses.Add(current);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var result = await sut.CheckAsync(current, null, checkDuplicatesInBitrix: false);

        Assert.False(result.IsLocalDuplicate);
    }

    [Fact]
    public async Task FindMatchingPersonAsync_SameFioAgeCityDifferentPhone_FindsExistingPerson()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeA,
            fullName: "Гор Олег Александрович",
            firstName: "Олег",
            lastName: "Гор",
            middleName: "Александрович",
            age: 66,
            city: "рабочий поселок Чик",
            phoneRaw: "+79930099416",
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeA,
            person.Id,
            phone: "79930099416",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var profile = new CandidateMatchProfile(
            "Гор Олег Александрович",
            66,
            "рабочий поселок Чик",
            "79910001122");
        var matched = await sut.FindMatchingPersonAsync(OfficeA, profile);

        Assert.NotNull(matched);
        Assert.Equal(person.Id, matched!.Id);
    }

    [Fact]
    public async Task FindLocalDuplicateAsync_SamePersonDifferentResponse_ReturnsEarlierResponse()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");

        var person = TestCandidatePersonFactory.CreatePerson(OfficeA);
        db.CandidatePersons.Add(person);
        var first = TestCandidatePersonFactory.CreateResponse(
            OfficeA,
            person.Id,
            sourceResponseId: "first",
            createdAt: DateTime.UtcNow.AddHours(-2));
        var second = TestCandidatePersonFactory.CreateResponse(
            OfficeA,
            person.Id,
            sourceResponseId: "second",
            createdAt: DateTime.UtcNow.AddHours(-1));
        db.CandidateResponses.AddRange(first, second);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var duplicate = await sut.FindLocalDuplicateAsync(OfficeA, person.Id, second.Id);

        Assert.NotNull(duplicate);
        Assert.Equal(first.Id, duplicate!.Id);
    }

    private static CandidateDuplicateService CreateService(OrbitaDbContext db) =>
        new(db, new CandidatePersonMatchService(db), CreateBitrixClient());

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedOffice(OrbitaDbContext db, Guid id, string name)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = id,
            Name = name,
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
    }

    private static BitrixClient CreateBitrixClient() =>
        new(new HttpClientFactoryStub(), new CandidateParser());

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}