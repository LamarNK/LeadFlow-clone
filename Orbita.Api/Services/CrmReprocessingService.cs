using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Consumes committed closures, regardless of which API/bulk path created them.
/// Card, tasks and immutable event context move in one transaction. A failed cycle
/// leaves closures eligible for retry; no in-memory cursor can lose work on restart.
/// </summary>
public sealed class CrmReprocessingService(
    OrbitaDbContext db,
    CrmLeadDistributionService distribution,
    IOptions<CrmReprocessingOptions> options,
    IPanelRealtimeNotifier? realtime = null,
    TimeProvider? clock = null)
{
    public const string ActorId = "system";
    private readonly CrmReprocessingOptions settings = options.Value;

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        if (!settings.Enabled) return 0;
        if (settings.DestinationOfficeId == Guid.Empty || settings.ClosedFromUtc is null
            || settings.SourceOfficeNames.Length == 0)
            throw new InvalidOperationException("Повторная обработка: задайте офис назначения, исходные офисы и начальную дату закрытия.");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql())
        {
            // Serialize distribution counters too, across API replicas.
            var acquired = await db.Database.SqlQuery<int>(
                $"SELECT CASE WHEN pg_try_advisory_xact_lock(740991) THEN 1 ELSE 0 END AS \"Value\"")
                .SingleAsync(ct);
            if (acquired == 0) return 0;
        }

        var destination = await db.Offices.SingleOrDefaultAsync(x => x.Id == settings.DestinationOfficeId, ct);
        if (destination is null || !destination.IsEnabled || !destination.CrmEnabled
            || !CrmStages.Resolve(destination.CrmStagesJson).Contains(CrmStages.Lead))
            throw new InvalidOperationException("Повторная обработка: принимающий офис должен быть включён, принимать CRM и иметь этап «Лид». Карточки оставлены на месте.");

        var names = settings.SourceOfficeNames.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray();
        // Normalize in .NET: SQLite LOWER and some database collations differ for Cyrillic.
        var offices = await db.Offices.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var sourceIds = offices.Where(x => x.Id != destination.Id && names.Contains(x.Name.Trim().ToLowerInvariant()))
            .Select(x => x.Id).ToArray();
        var from = settings.ClosedFromUtc.Value.UtcDateTime;
        var ids = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => sourceIds.Contains(x.OfficeId) && x.IsClosed
                && x.CloseReason != null && x.CloseReason.Trim().ToUpper() == CrmCloseReasons.NoAnswer
                && x.ClosedAtUtc >= from)
            .OrderBy(x => x.ClosedAtUtc).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(Math.Clamp(settings.BatchSize, 1, 100)).ToArrayAsync(ct);
        if (ids.Length == 0) return 0;

        var cards = db.Database.IsNpgsql()
            ? await db.CrmCandidateCards.FromSqlInterpolated(
                $"SELECT * FROM \"CrmCandidateCards\" WHERE \"Id\" = ANY({ids}) FOR UPDATE SKIP LOCKED").ToListAsync(ct)
            : await db.CrmCandidateCards.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        // A human may have reopened/changed a candidate between selection and lock.
        cards = cards.Where(x => x.IsClosed && x.ClosedAtUtc >= from
            && string.Equals(x.CloseReason?.Trim(), CrmCloseReasons.NoAnswer, StringComparison.OrdinalIgnoreCase)
            && sourceIds.Contains(x.OfficeId)).ToList();
        ids = cards.Select(x => x.Id).ToArray();
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var previousOffices = cards.Select(x => x.OfficeId).Distinct().ToArray();
        var persons = await db.CrmCandidateCards.Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Response.PersonId, x.Response.Person.PhoneRaw, x.Response.Person.PhoneNormalized })
            .Distinct().ToListAsync(ct);
        var phoneService = new CandidatePersonPhoneService(db);
        foreach (var person in persons)
            await phoneService.StageCrmContactAsync(person.PersonId, person.PhoneRaw, person.PhoneNormalized, ct);

        // Legacy analytics used the current office as a fallback. Freeze that existing
        // attribution before moving, and explicitly mark it inferred, not proven.
        var legacyHistory = await db.CrmCandidateHistory
            .Where(x => ids.Contains(x.CardId) && x.OfficeId == null).ToListAsync(ct);
        var byId = cards.ToDictionary(x => x.Id);
        foreach (var history in legacyHistory)
        {
            history.OfficeId = byId[history.CardId].OfficeId;
            history.ContextInferred = true;
        }

        var tasks = await db.CrmTasks.Where(x => x.CardId != null && ids.Contains(x.CardId.Value)
            && x.Status != CrmTaskStatuses.Completed && x.Status != CrmTaskStatuses.Cancelled).ToListAsync(ct);
        foreach (var task in tasks)
        {
            task.Status = CrmTaskStatuses.Cancelled;
            task.UpdatedAtUtc = now;
            task.ReminderVersion = Guid.NewGuid();
            task.ReminderVersionChangedAtUtc = now;
            AddEvent(byId[task.CardId!.Value], "TaskCancelled", $"Повторная обработка: отменена задача {task.Id}", now);
        }
        var allTaskIds = await db.CrmTasks.Where(x => x.CardId != null && ids.Contains(x.CardId.Value))
            .Select(x => x.Id).ToArrayAsync(ct);
        var notifications = await db.CrmTaskNotifications
            .Where(x => allTaskIds.Contains(x.TaskId) && x.DismissedAtUtc == null).ToListAsync(ct);
        foreach (var notification in notifications) notification.DismissedAtUtc = now;
        var messages = await db.CrmOutboundChatMessages.Where(x => ids.Contains(x.CardId)
            && x.Status == CrmOutboundChatStatuses.Planned && x.CancelledAtUtc == null).ToListAsync(ct);
        foreach (var message in messages)
        {
            message.CancelledAtUtc = now;
            AddEvent(byId[message.CardId], "ChatCancelled", "Перенос на повторную обработку", now);
        }

        foreach (var card in cards)
        {
            var sourceId = card.OfficeId;
            var owner = card.ManagerUserId;
            card.EntryOfficeId ??= sourceId;
            card.EnteredCrmAtUtc ??= card.CreatedAtUtc;
            // Preserve precisely the old receipt-query fallback; never create a new receipt.
            card.InitialManagerUserId ??= owner;
            if (card.InitialManagerUserId is not null) card.InitialAssignedOfficeId ??= sourceId;
            AddEvent(card, "ReprocessingSent", $"{sourceId} → {destination.Id}; причина НДЗ; закрыто {card.ClosedAtUtc:O}", now);
            AddEvent(card, "Reopened", "Автоматический возврат из НДЗ на повторную обработку", now);
            card.OfficeId = destination.Id;
            card.ManagerUserId = null;
            card.Stage = CrmStages.Lead;
            card.StageChangedAtUtc = now;
            card.UpdatedAtUtc = now;
            card.IsClosed = false;
            card.CloseReason = null;
            card.ClosedAtUtc = null;
            card.IsInActiveLoad = true;
            card.NextActionAtUtc = null;
            AddEvent(card, "ReprocessingReceived", $"Получено из офиса {sourceId}; прежний ответственный {owner ?? "не назначен"}", now);
            await distribution.TryAutoAssignNewCardAsync(card, ActorId, "Система", ct);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        foreach (var office in previousOffices.Append(destination.Id).Distinct())
            realtime?.Notify([PanelChangeKind.Crm], office);
        return cards.Count;
    }

    private void AddEvent(CrmCandidateCardEntity card, string action, string details, DateTime now) =>
        db.AddCrmHistory(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(), CardId = card.Id, Action = action, Details = details,
            ActorUserId = ActorId, ActorName = "Система", CreatedAtUtc = now,
            PreviousCloseReason = card.CloseReason
        });
}
