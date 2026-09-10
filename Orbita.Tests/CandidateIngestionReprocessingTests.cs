using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CandidateIngestionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reprocessing_NewResponsePhoneEnrichesTransferredCard_NotNewLead(bool phoneChangedMetric)
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var destination = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = destination, Name = "Повторная обработка", CrmEnabled = true });
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, fullName: "Гор Олег Александрович",
            firstName: "Олег", lastName: "Гор", middleName: "Александрович", age: 66,
            city: "рабочий поселок Чик", phoneRaw: "+79930099416", phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        var response = TestCandidatePersonFactory.CreateResponse(OfficeId, person.Id, WorkerId,
            phone: "79930099416", sourceResponseId: "original", fullName: person.FullName, age: 66, city: person.City);
        db.CandidateResponses.Add(response);
        var card = new CrmCandidateCardEntity { Id = Guid.NewGuid(), ResponseId = response.Id,
            OfficeId = OfficeId, IsClosed = true, CloseReason = CrmCloseReasons.NoAnswer, ClosedAtUtc = DateTime.UtcNow,
            Stage = CrmStages.Ndz73, CreatedAtUtc = DateTime.UtcNow.AddDays(-4) };
        db.CrmCandidateCards.Add(card); await db.SaveChangesAsync();
        var settings = new CrmReprocessingOptions { Enabled = true, DestinationOfficeId = destination,
            SourceOfficeNames = ["Test Office"], ClosedFromUtc = DateTimeOffset.UtcNow.AddDays(-1) };
        Assert.Equal(1, await new CrmReprocessingService(db, new CrmLeadDistributionService(db, null!), Options.Create(settings)).ProcessBatchAsync());
        var request = new WorkerCandidateBatchRequest([new WorkerCandidateDto(Guid.NewGuid(), "acc", "Avito",
            "new-source", "", person.FullName, 66, null, "+79910001122", person.City, "Курьер", "", "", "", "", "",
            DateTime.UtcNow, PhoneMetricKind: phoneChangedMetric ? ResponsePhoneMetricKinds.PhoneChanged : null)]);
        await CreateService(db).IngestBatchAsync(WorkerId, request);
        Assert.Single(await db.CrmCandidateCards.ToListAsync());
        Assert.Equal(destination, card.OfficeId);
        Assert.Equal(response.Id, card.ResponseId);
        Assert.Equal(person.Id, (await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "new-source")).PersonId);
        var phones = await db.CandidateContactPhones.Where(x => x.PersonId == person.Id).ToListAsync();
        Assert.Equal(2, phones.Count);
        Assert.Contains(phones, x => x.PhoneNormalized == "79910001122" && !x.IsPrimary);
        Assert.Contains(phones, x => x.PhoneNormalized == "79930099416" && x.IsPrimary);
        // Replayed input cannot add another contact or another CRM card.
        await CreateService(db).IngestBatchAsync(WorkerId, request);
        Assert.Equal(2, await db.CandidateContactPhones.CountAsync(x => x.PersonId == person.Id));
        Assert.Single(await db.CrmCandidateCards.ToListAsync());
    }
}
