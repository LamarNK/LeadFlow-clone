using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmTelephonyServiceTests
{
    [Theory]
    [InlineData("NOANSWER", CrmCallStatuses.Missed)]
    [InlineData("CANCEL", CrmCallStatuses.Missed)]
    [InlineData("BUSY", CrmCallStatuses.Rejected)]
    [InlineData("CHANUNAVAIL", CrmCallStatuses.Failed)]
    public async Task MissedIncomingWithoutCard_NotifiesBoundManager_AndKeepsCallId(string signal, string status)
    {
        await using var h = await Harness.CreateAsync();
        var receiver = await h.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        var payload = new AsteriskCallWebhookPayload("unknown-person", "79995556677", "74950000000", "inbound",
            "201", h.Now.ToUnixTimeSeconds().ToString(), "0", signal, signal, "19");
        for (var i = 0; i < 2; i++)
            await h.Sut.ReceiveAsteriskCallAsync(receiver.PublicId, receiver.Secret, payload, null, 0, null, null);
        var call = await h.Db.CrmCalls.SingleAsync();
        var alert = await h.Db.CrmDeskAlerts.SingleAsync();
        Assert.Null(call.CardId); Assert.Null(alert.CardId);
        Assert.Equal(call.Id, alert.Id);
        Assert.Equal(status, call.Status);
        Assert.Equal(Harness.ManagerId, alert.RecipientUserId);
        Assert.Single(h.Notifier.Notifications);
        Assert.Contains("79995556677", alert.Message);
    }

    [Fact]
    public async Task LateCallResult_UnknownThenMissedThenAnswered_HidesFalseAlertAndCannotDowngrade()
    {
        await using var h = await Harness.CreateAsync();
        (await h.Db.CrmCandidateCards.SingleAsync(x => x.Id == h.CardId)).ManagerUserId = Harness.ManagerId;
        await h.Db.SaveChangesAsync();
        var receiver = await h.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        async Task Send(string status, string duration = "0", string extension = "201") =>
            await h.Sut.ReceiveAsteriskCallAsync(receiver.PublicId, receiver.Secret,
                new AsteriskCallWebhookPayload("late-result", "79991112233", "74950000000", "inbound",
                    extension, h.Now.ToUnixTimeSeconds().ToString(), duration, status, status, "16"), null, 0, null, null);
        await Send("UNKNOWN");
        Assert.Empty(await h.Db.CrmDeskAlerts.ToListAsync());
        await Send("NOANSWER");
        var alert = await h.Db.CrmDeskAlerts.SingleAsync();
        Assert.Equal(h.CardId, alert.CardId);
        var notifications = new CrmDeadlineNotificationService(h.Db, new FixedTimeProvider(h.Now),
            Options.Create(new CrmDeadlineNotificationOptions()), h.Notifier,
            NullLogger<CrmDeadlineNotificationService>.Instance);
        Assert.Equal(1, (await notifications.GetSummaryAsync(h.OfficeId, Harness.ManagerId)).UnreadCount);
        await h.AddAsteriskManagerAsync("other-answering-manager", "202", false);
        await Send("ANSWER", "12", "202");
        await Send("CANCEL"); // delayed/replayed unanswered fragment from the first endpoint
        var call = await h.Db.CrmCalls.SingleAsync();
        Assert.Equal(CrmCallStatuses.Answered, call.Status);
        Assert.Equal(12, call.DurationSeconds);
        Assert.Equal("other-answering-manager", call.ManagerUserId);
        Assert.Empty((await notifications.GetAsync(h.OfficeId, Harness.ManagerId, false, 20)).Items);
        Assert.Equal(0, (await notifications.GetSummaryAsync(h.OfficeId, Harness.ManagerId)).UnreadCount);
        Assert.Single(await h.Db.CrmDeskAlerts.ToListAsync()); // preserve history, do not delete
        Assert.Single(h.Notifier.Notifications);
    }

    [Fact]
    public async Task UnassignedCardNotifiesLineOwnerWithoutExposingCard()
    {
        await using var h = await Harness.CreateAsync();
        (await h.Db.CrmCandidateCards.SingleAsync(x => x.Id == h.CardId)).ManagerUserId = null;
        await h.Db.SaveChangesAsync();
        var receiver = await h.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await h.Sut.ReceiveAsteriskCallAsync(receiver.PublicId, receiver.Secret,
            new AsteriskCallWebhookPayload("unassigned", "79991112233", "74950000000", "inbound",
                "201", h.Now.ToUnixTimeSeconds().ToString(), "0", "NOANSWER", "NOANSWER", "19"), null, 0, null, null);
        var alert = await h.Db.CrmDeskAlerts.SingleAsync();
        Assert.Equal(Harness.ManagerId, alert.RecipientUserId);
        Assert.Null(alert.CardId);
        Assert.Equal("Входящий от +79991112233", alert.Title);
        Assert.Equal(h.CardId, (await h.Db.CrmCalls.SingleAsync()).CardId);
    }

    [Theory]
    [InlineData("outbound", "NOANSWER")]
    [InlineData("inbound", "ANSWER")]
    [InlineData("inbound", "UNKNOWN")]
    public async Task NoMissedAlertForOutgoingAnsweredOrUnknown(string direction, string status)
    {
        await using var h = await Harness.CreateAsync();
        var receiver = await h.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await h.Sut.ReceiveAsteriskCallAsync(receiver.PublicId, receiver.Secret,
            new AsteriskCallWebhookPayload("not-missed", "79991112233", "74950000000", direction,
                "201", h.Now.ToUnixTimeSeconds().ToString(), "0", status, status, "16"), null, 0, null, null);
        Assert.Empty(await h.Db.CrmDeskAlerts.ToListAsync());
        Assert.Empty(h.Notifier.Notifications);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaterPhoneMatchLinksAlertOnlyToRecipientsCard(bool owned)
    {
        await using var h = await Harness.CreateAsync();
        var card = await h.Db.CrmCandidateCards.SingleAsync(x => x.Id == h.CardId);
        card.ManagerUserId = owned ? Harness.ManagerId : "another-manager";
        var callId = Guid.NewGuid();
        h.Db.CrmCalls.Add(new Orbita.Api.Data.CrmCallEntity { Id = callId, OfficeId = h.OfficeId,
            ManagerUserId = Harness.ManagerId, ClientPhoneNormalized = "79991112233",
            StartedAtUtc = h.Now.UtcDateTime, Status = CrmCallStatuses.Missed,
            Direction = CrmCallDirections.Incoming, Provider = CrmTelephonyProviders.Asterisk,
            ExternalCallId = "later-phone-match" });
        h.Db.CrmDeskAlerts.Add(new Orbita.Api.Data.CrmDeskAlertEntity { Id = callId, OfficeId = h.OfficeId,
            RecipientUserId = Harness.ManagerId, Kind = CrmTaskNotificationKinds.MissedCall });
        await h.Db.SaveChangesAsync();
        Assert.Equal(1, await h.Sut.ReconcileUnmatchedCallsAsync());
        Assert.Equal(h.CardId, (await h.Db.CrmCalls.SingleAsync()).CardId);
        Assert.Equal(owned ? h.CardId : (Guid?)null, (await h.Db.CrmDeskAlerts.SingleAsync()).CardId);
    }
}
