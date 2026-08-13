using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerOutboundChatService(OrbitaDbContext db, IPanelRealtimeNotifier? panelRealtime = null)
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<WorkerPendingChatMessageDto>> GetPendingAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct = default)
    {
        var ownsAccount = await db.WorkerAccounts.AsNoTracking()
            .AnyAsync(x => x.WorkerId == workerId && x.AccountId == accountId, ct);
        if (!ownsAccount)
        {
            return [];
        }

        var expiredClaimAt = DateTime.UtcNow - ClaimTimeout;
        var expiredClaims = db.CrmOutboundChatMessages
            .Where(x => x.Status == CrmOutboundChatStatuses.Sending
                        && x.DeliveryClaimedAtUtc < expiredClaimAt);
        if (db.Database.IsRelational())
        {
            await expiredClaims.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, CrmOutboundChatStatuses.Planned)
                .SetProperty(x => x.DeliveryClaimedByWorkerId, (string?)null)
                .SetProperty(x => x.DeliveryClaimedAtUtc, (DateTime?)null), ct);
        }
        else
        {
            var expired = await expiredClaims.ToListAsync(ct);
            foreach (var message in expired)
            {
                message.Status = CrmOutboundChatStatuses.Planned;
                message.DeliveryClaimedByWorkerId = null;
                message.DeliveryClaimedAtUtc = null;
            }

            if (expired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        return await db.CrmOutboundChatMessages
            .AsNoTracking()
            .Where(x => x.Status == CrmOutboundChatStatuses.Planned && x.CancelledAtUtc == null)
            .Join(
                db.CandidateResponses.Where(r => r.AccountId == accountId),
                message => message.ResponseId,
                response => response.Id,
                (message, response) => new { message, response })
            .Join(
                db.CrmCandidateCards.Where(card => !card.IsClosed),
                entry => entry.message.CardId,
                card => card.Id,
                (entry, _) => entry)
            .OrderBy(x => x.message.CreatedAtUtc)
            .Select(x => new WorkerPendingChatMessageDto(
                x.message.Id,
                x.response.SourceResponseId,
                x.message.Text,
                x.response.AvitoSubProfileId,
                x.message.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<bool> ClaimForDeliveryAsync(
        Guid workerId,
        Guid messageId,
        CancellationToken ct = default)
    {
        if (messageId == Guid.Empty)
        {
            return false;
        }

        var claimedAt = DateTime.UtcNow;
        var workerIdText = workerId.ToString("D");
        var claimable = db.CrmOutboundChatMessages
            .Where(message => message.Id == messageId
                              && message.Status == CrmOutboundChatStatuses.Planned
                              && message.CancelledAtUtc == null
                              && db.CrmCandidateCards.Any(card => card.Id == message.CardId && !card.IsClosed)
                              && db.CandidateResponses.Any(response => response.Id == message.ResponseId
                                  && db.WorkerAccounts.Any(account => account.WorkerId == workerId
                                      && account.AccountId == response.AccountId)));
        if (db.Database.IsRelational())
        {
            var updated = await claimable.ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, CrmOutboundChatStatuses.Sending)
                .SetProperty(message => message.DeliveryClaimedByWorkerId, workerIdText)
                .SetProperty(message => message.DeliveryClaimedAtUtc, claimedAt), ct);
            return updated == 1;
        }

        var message = await claimable.SingleOrDefaultAsync(ct);
        if (message is null)
        {
            return false;
        }

        message.Status = CrmOutboundChatStatuses.Sending;
        message.DeliveryClaimedByWorkerId = workerIdText;
        message.DeliveryClaimedAtUtc = claimedAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AckSentAsync(
        Guid workerId,
        IReadOnlyList<Guid> sentIds,
        CancellationToken ct = default)
    {
        var ids = sentIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return true;
        }

        var workerAccountIds = await db.WorkerAccounts.AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .Select(x => x.AccountId)
            .ToListAsync(ct);
        if (workerAccountIds.Count == 0)
        {
            return false;
        }

        var workerIdText = workerId.ToString("D");
        var messages = await db.CrmOutboundChatMessages
            .Where(x => ids.Contains(x.Id)
                        && x.Status == CrmOutboundChatStatuses.Sending
                        && x.CancelledAtUtc == null
                        && x.DeliveryClaimedByWorkerId == workerIdText)
            .ToListAsync(ct);
        if (messages.Count == 0)
        {
            return true;
        }

        var responseIds = messages.Select(x => x.ResponseId).Distinct().ToList();
        var ownedResponseIds = await db.CandidateResponses.AsNoTracking()
            .Where(x => responseIds.Contains(x.Id) && workerAccountIds.Contains(x.AccountId))
            .Select(x => x.Id)
            .ToListAsync(ct);
        var owned = ownedResponseIds.ToHashSet();

        var now = DateTime.UtcNow;
        Guid? officeId = null;
        foreach (var message in messages)
        {
            if (!owned.Contains(message.ResponseId))
            {
                continue;
            }

            if (string.Equals(message.Status, CrmOutboundChatStatuses.Sent, StringComparison.Ordinal))
            {
                continue;
            }

            message.Status = CrmOutboundChatStatuses.Sent;
            message.SentAtUtc = now;
            message.DeliveryClaimedByWorkerId = null;
            message.DeliveryClaimedAtUtc = null;
            db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
            {
                Id = Guid.NewGuid(),
                CardId = message.CardId,
                Action = "ChatSent",
                Details = message.Text,
                ActorUserId = "system",
                ActorName = "Воркер",
                CreatedAtUtc = now
            });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            officeId = await db.CrmCandidateCards.AsNoTracking()
                .Where(x => x.Id == messages[0].CardId)
                .Select(x => (Guid?)x.OfficeId)
                .FirstOrDefaultAsync(ct);
            if (officeId is Guid oid)
            {
                panelRealtime?.Notify([PanelChangeKind.Crm], oid);
            }
        }

        return true;
    }
}
