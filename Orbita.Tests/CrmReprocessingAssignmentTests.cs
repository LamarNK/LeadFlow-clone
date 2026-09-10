using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmWorkspaceServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reprocessing_AssignsOnlyToDestinationShift_KeepingFirstOwner(bool onShift)
    {
        await using var h = await Harness.CreateAsync();
        SeedOffice(h.Db, crmEnabled: true);
        var sourceName = (await h.Db.Offices.SingleAsync(x => x.Id == OfficeId)).Name;
        var destinationId = Guid.NewGuid();
        h.Db.Offices.Add(new OfficeEntity { Id = destinationId, Name = "Повторная обработка", CrmEnabled = true });
        await h.Db.SaveChangesAsync();
        var previous = await h.CreateManagerAsync("old@demo.local", 20, false);
        var next = await h.CreateDeskUserAsync("next@demo.local", 20, onShift, PanelRoles.Manager, destinationId);
        var response = await SeedResponseAsync(h.Db, "repeat-shift");
        var card = NewCard(response.Id);
        card.ManagerUserId = previous.Id;
        card.IsClosed = true; card.CloseReason = CrmCloseReasons.NoAnswer; card.ClosedAtUtc = h.Clock.GetUtcNow().UtcDateTime;
        h.Db.CrmCandidateCards.Add(card);
        h.Db.CrmDailyDistributionSessions.Add(new CrmDailyDistributionSessionEntity
        {
            Id = Guid.NewGuid(), OfficeId = destinationId,
            LocalDate = CrmDailyDistribution.BusinessDate(h.Clock.GetUtcNow().UtcDateTime),
            FirstShiftStartedAtUtc = h.Clock.GetUtcNow().UtcDateTime.AddHours(-1),
            DistributeAfterUtc = h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-55),
            DistributedAtUtc = h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-55)
        });
        await h.Db.SaveChangesAsync();
        var service = new CrmReprocessingService(h.Db, new CrmLeadDistributionService(h.Db, h.Users, h.Clock),
            Options.Create(new CrmReprocessingOptions { Enabled = true, DestinationOfficeId = destinationId,
                SourceOfficeNames = [sourceName], ClosedFromUtc = h.Clock.GetUtcNow().AddDays(-1) }), clock: h.Clock);
        Assert.Equal(1, await service.ProcessBatchAsync());
        Assert.Equal(onShift ? next.Id : null, card.ManagerUserId);
        Assert.Equal(previous.Id, card.InitialManagerUserId);
        Assert.Equal(OfficeId, card.EntryOfficeId);
        Assert.Equal(OfficeId, card.InitialAssignedOfficeId);
        Assert.Equal(destinationId, card.OfficeId);
    }
}
