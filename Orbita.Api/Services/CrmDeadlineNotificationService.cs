using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CrmDeadlineNotificationService(
    OrbitaDbContext db,
    TimeProvider timeProvider,
    IOptions<CrmDeadlineNotificationOptions> options,
    ICrmNotificationRealtimeNotifier realtime,
    ILogger<CrmDeadlineNotificationService> logger)
{
    public async Task<int> ProcessDueTasksAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return 0;
        }

        var now = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
        var firstReminderMinutes = Math.Clamp(settings.FirstReminderMinutes, 2, 7 * 24 * 60);
        var finalReminderMinutes = Math.Clamp(settings.FinalReminderMinutes, 1, firstReminderMinutes - 1);
        var batchSize = Math.Clamp(settings.BatchSize, 1, 1000);
        var firstReminderAt = now.AddMinutes(firstReminderMinutes);
        var finalReminderAt = now.AddMinutes(finalReminderMinutes);

        var eligible = db.CrmTasks.AsNoTracking()
            .Where(task => task.Status == CrmTaskStatuses.Open && task.DueAtUtc != null)
            .Where(task => db.Offices.Any(office =>
                office.Id == task.OfficeId
                && office.IsEnabled
                && office.CrmEnabled
                && office.CrmDeadlineNotificationsEnabled
                && office.CrmDeadlineNotificationsEnabledAtUtc != null
                && (task.DueAtUtc > office.CrmDeadlineNotificationsEnabledAtUtc.Value
                    || task.ReminderVersionChangedAtUtc >= office.CrmDeadlineNotificationsEnabledAtUtc.Value)))
            .Where(task => db.PanelUserProfiles.Any(profile =>
                profile.UserId == task.AssigneeUserId
                && profile.OfficeId == task.OfficeId));

        var candidates = new List<(CrmTaskEntity Task, string Kind)>(batchSize);

        await AppendCandidatesAsync(
            eligible.Where(task => task.DueAtUtc <= now),
            CrmTaskNotificationKinds.Overdue,
            candidates,
            batchSize,
            ct);

        await AppendCandidatesAsync(
            eligible.Where(task => task.DueAtUtc > now && task.DueAtUtc <= finalReminderAt),
            CrmTaskNotificationKinds.DueIn1Hour,
            candidates,
            batchSize,
            ct);

        await AppendCandidatesAsync(
            eligible.Where(task => task.DueAtUtc > finalReminderAt && task.DueAtUtc <= firstReminderAt),
            CrmTaskNotificationKinds.DueIn24Hours,
            candidates,
            batchSize,
            ct);

        var created = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryCreateAsync(candidate.Task, candidate.Kind, now, ct))
            {
                created++;
            }
        }

        return created;
    }

    public async Task<CrmTaskNotificationsDto> GetAsync(
        Guid officeId,
        string recipientUserId,
        bool unreadOnly,
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var deadlineEnabled = await IsEnabledForOfficeAsync(officeId, ct);
        var deskAlertsQuery = db.CrmDeskAlerts
            .Where(x => x.OfficeId == officeId && x.RecipientUserId == recipientUserId);
        var deskUnread = await deskAlertsQuery.CountAsync(x => x.ReadAtUtc == null, ct);

        var taskItems = new List<CrmTaskNotificationDto>();
        var taskUnread = 0;
        if (deadlineEnabled)
        {
            var active = ActiveForUser(officeId, recipientUserId);
            taskUnread = await active.CountAsync(x => x.ReadAtUtc == null, ct);
            var rowsQuery = unreadOnly ? active.Where(x => x.ReadAtUtc == null) : active;
            var rows = await rowsQuery
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(limit)
                .ToListAsync(ct);

            var taskIds = rows.Select(x => x.TaskId).Distinct().ToArray();
            var tasks = await db.CrmTasks.AsNoTracking()
                .Where(x => taskIds.Contains(x.Id))
                .Select(x => new { x.Id, x.CardId, x.Title })
                .ToDictionaryAsync(x => x.Id, ct);

            taskItems = rows
                .Where(row => tasks.ContainsKey(row.TaskId))
                .Select(row =>
                {
                    var task = tasks[row.TaskId];
                    return Map(row, task.CardId, task.Title);
                })
                .ToList();
        }

        var alertsQuery = unreadOnly ? deskAlertsQuery.Where(x => x.ReadAtUtc == null) : deskAlertsQuery;
        var alerts = await alertsQuery
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
        var alertItems = alerts.Select(MapDeskAlert).ToList();

        var items = taskItems
            .Concat(alertItems)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .ToList();

        var enabled = deadlineEnabled || deskUnread > 0 || alertItems.Count > 0 || taskItems.Count > 0;
        // Always show CRM notification shell for desk roles once they have CRM office context.
        enabled = true;
        return new CrmTaskNotificationsDto(taskUnread + deskUnread, items, Enabled: enabled);
    }

    public async Task<CrmTaskNotificationSummaryDto> GetSummaryAsync(
        Guid officeId,
        string recipientUserId,
        CancellationToken ct = default)
    {
        var deadlineEnabled = await IsEnabledForOfficeAsync(officeId, ct);
        var taskUnread = deadlineEnabled
            ? await ActiveForUser(officeId, recipientUserId).CountAsync(x => x.ReadAtUtc == null, ct)
            : 0;
        var deskUnread = await db.CrmDeskAlerts
            .CountAsync(x => x.OfficeId == officeId && x.RecipientUserId == recipientUserId && x.ReadAtUtc == null, ct);
        return new CrmTaskNotificationSummaryDto(taskUnread + deskUnread, Enabled: true);
    }

    public async Task<bool> MarkReadAsync(
        Guid officeId,
        string recipientUserId,
        Guid notificationId,
        CancellationToken ct = default)
    {
        var notification = await ActiveForUser(officeId, recipientUserId)
            .FirstOrDefaultAsync(x => x.Id == notificationId, ct);
        if (notification is not null)
        {
            if (notification.ReadAtUtc is null)
            {
                notification.ReadAtUtc = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
                await db.SaveChangesAsync(ct);
            }

            return true;
        }

        var alert = await db.CrmDeskAlerts
            .FirstOrDefaultAsync(
                x => x.Id == notificationId && x.OfficeId == officeId && x.RecipientUserId == recipientUserId,
                ct);
        if (alert is null)
        {
            return false;
        }

        if (alert.ReadAtUtc is null)
        {
            alert.ReadAtUtc = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<int> MarkAllReadAsync(
        Guid officeId,
        string recipientUserId,
        CancellationToken ct = default)
    {
        var now = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
        var count = 0;
        var notifications = await ActiveForUser(officeId, recipientUserId)
            .Where(x => x.ReadAtUtc == null)
            .ToListAsync(ct);
        foreach (var notification in notifications)
        {
            notification.ReadAtUtc = now;
            count++;
        }

        var alerts = await db.CrmDeskAlerts
            .Where(x => x.OfficeId == officeId && x.RecipientUserId == recipientUserId && x.ReadAtUtc == null)
            .ToListAsync(ct);
        foreach (var alert in alerts)
        {
            alert.ReadAtUtc = now;
            count++;
        }

        if (count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return count;
    }

    private static CrmTaskNotificationDto MapDeskAlert(CrmDeskAlertEntity alert) =>
        new(
            alert.Id,
            Guid.Empty,
            alert.CardId,
            alert.Kind,
            alert.Title,
            alert.Message,
            alert.CreatedAtUtc,
            alert.CreatedAtUtc,
            alert.ReadAtUtc);

    /// <summary>Queues stale task notifications for dismissal in the caller's unit of work.</summary>
    public async Task DismissTaskAsync(Guid taskId, DateTime atUtc, CancellationToken ct = default)
    {
        var pending = await db.CrmTaskNotifications
            .Where(x => x.TaskId == taskId && x.DismissedAtUtc == null)
            .ToListAsync(ct);
        foreach (var notification in pending)
        {
            notification.DismissedAtUtc = DateTimeUtcHelper.EnsureUtc(atUtc);
        }
    }

    /// <summary>Queues all active office notifications for dismissal in the caller's unit of work.</summary>
    public async Task DismissOfficeAsync(Guid officeId, DateTime atUtc, CancellationToken ct = default)
    {
        var pending = await db.CrmTaskNotifications
            .Where(x => x.OfficeId == officeId && x.DismissedAtUtc == null)
            .ToListAsync(ct);
        foreach (var notification in pending)
        {
            notification.DismissedAtUtc = DateTimeUtcHelper.EnsureUtc(atUtc);
        }
    }

    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        var retentionDays = Math.Clamp(options.Value.RetentionDays, 7, 3650);
        var cutoff = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime).AddDays(-retentionDays);
        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 1000);
        var expired = await db.CrmTaskNotifications
            .Where(x => x.CreatedAtUtc < cutoff && (x.ReadAtUtc != null || x.DismissedAtUtc != null))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return 0;
        }

        db.CrmTaskNotifications.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    private IQueryable<CrmTaskNotificationEntity> ActiveForUser(Guid officeId, string recipientUserId) =>
        db.CrmTaskNotifications
            .Where(notification =>
                notification.OfficeId == officeId
                && notification.RecipientUserId == recipientUserId
                && notification.DismissedAtUtc == null
                && db.CrmTasks.Any(task =>
                    task.Id == notification.TaskId
                    && task.Status == CrmTaskStatuses.Open
                    && task.ReminderVersion == notification.ReminderVersion
                    && task.AssigneeUserId == notification.RecipientUserId)
                && db.PanelUserProfiles.Any(profile =>
                    profile.UserId == notification.RecipientUserId
                    && profile.OfficeId == notification.OfficeId));

    private Task<bool> IsEnabledForOfficeAsync(Guid officeId, CancellationToken ct) =>
        options.Value.Enabled
            ? db.Offices.AsNoTracking().AnyAsync(
                office => office.Id == officeId
                          && office.IsEnabled
                          && office.CrmEnabled
                          && office.CrmDeadlineNotificationsEnabled
                          && office.CrmDeadlineNotificationsEnabledAtUtc != null,
                ct)
            : Task.FromResult(false);

    private async Task AppendCandidatesAsync(
        IQueryable<CrmTaskEntity> query,
        string kind,
        List<(CrmTaskEntity Task, string Kind)> candidates,
        int batchSize,
        CancellationToken ct)
    {
        var remaining = batchSize - candidates.Count;
        if (remaining <= 0)
        {
            return;
        }

        var rows = await query
            .Where(task => !db.CrmTaskNotifications.Any(notification =>
                notification.TaskId == task.Id
                && notification.ReminderVersion == task.ReminderVersion
                && notification.Kind == kind))
            .OrderBy(task => task.DueAtUtc)
            .Take(remaining)
            .ToListAsync(ct);
        candidates.AddRange(rows.Select(task => (task, kind)));
    }

    private async Task<bool> TryCreateAsync(
        CrmTaskEntity candidate,
        string kind,
        DateTime now,
        CancellationToken ct)
    {
        // The task may have been completed, reassigned or rescheduled after the batch query.
        // Re-read it immediately before persistence so a stale snapshot cannot create a toast.
        var task = await ReloadEligibleTaskAsync(candidate.Id, candidate.ReminderVersion, kind, now, ct);
        if (task is null)
        {
            return false;
        }

        var dueAtUtc = DateTimeUtcHelper.EnsureUtc(task.DueAtUtc!.Value);
        var entity = new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = task.OfficeId,
            TaskId = task.Id,
            ReminderVersion = task.ReminderVersion,
            RecipientUserId = task.AssigneeUserId,
            Kind = kind,
            DueAtUtc = dueAtUtc,
            CreatedAtUtc = now
        };

        db.CrmTaskNotifications.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }

        // Save first (the database is the source of truth), then verify that the reminder is
        // still current before emitting the best-effort realtime hint.
        if (await ReloadEligibleTaskAsync(task.Id, task.ReminderVersion, kind, now, ct) is null)
        {
            entity.DismissedAtUtc = now;
            await db.SaveChangesAsync(ct);
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }

        var dto = Map(entity, task.CardId, task.Title);
        db.Entry(entity).State = EntityState.Detached;
        try
        {
            await realtime.NotifyAsync(task.AssigneeUserId, dto, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CRM deadline notification {NotificationId} was saved but realtime delivery to user {UserId} failed.",
                entity.Id,
                task.AssigneeUserId);
        }

        return true;
    }

    private async Task<CrmTaskEntity?> ReloadEligibleTaskAsync(
        Guid taskId,
        Guid reminderVersion,
        string expectedKind,
        DateTime now,
        CancellationToken ct)
    {
        var task = await db.CrmTasks.AsNoTracking()
            .Where(x => x.Id == taskId
                        && x.ReminderVersion == reminderVersion
                        && x.Status == CrmTaskStatuses.Open
                        && x.DueAtUtc != null)
            .Where(x => db.Offices.Any(office =>
                office.Id == x.OfficeId
                && office.IsEnabled
                && office.CrmEnabled
                && office.CrmDeadlineNotificationsEnabled
                && office.CrmDeadlineNotificationsEnabledAtUtc != null
                && (x.DueAtUtc > office.CrmDeadlineNotificationsEnabledAtUtc.Value
                    || x.ReminderVersionChangedAtUtc >= office.CrmDeadlineNotificationsEnabledAtUtc.Value)))
            .Where(x => db.PanelUserProfiles.Any(profile =>
                profile.UserId == x.AssigneeUserId
                && profile.OfficeId == x.OfficeId))
            .FirstOrDefaultAsync(ct);
        if (task?.DueAtUtc is not DateTime dueAtUtc)
        {
            return null;
        }

        var settings = options.Value;
        var firstReminderMinutes = Math.Clamp(settings.FirstReminderMinutes, 2, 7 * 24 * 60);
        var finalReminderMinutes = Math.Clamp(settings.FinalReminderMinutes, 1, firstReminderMinutes - 1);
        var actualKind = ResolveKind(
            DateTimeUtcHelper.EnsureUtc(dueAtUtc),
            now,
            now.AddMinutes(finalReminderMinutes),
            now.AddMinutes(firstReminderMinutes));
        return actualKind == expectedKind ? task : null;
    }

    private static string? ResolveKind(DateTime dueAtUtc, DateTime now, DateTime finalReminderAt, DateTime firstReminderAt)
    {
        if (dueAtUtc <= now)
        {
            return CrmTaskNotificationKinds.Overdue;
        }

        if (dueAtUtc <= finalReminderAt)
        {
            return CrmTaskNotificationKinds.DueIn1Hour;
        }

        return dueAtUtc <= firstReminderAt
            ? CrmTaskNotificationKinds.DueIn24Hours
            : null;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private static CrmTaskNotificationDto Map(
        CrmTaskNotificationEntity notification,
        Guid? cardId,
        string taskTitle) =>
        new(
            notification.Id,
            notification.TaskId,
            cardId,
            notification.Kind,
            taskTitle,
            MessageFor(notification.Kind),
            notification.DueAtUtc,
            notification.CreatedAtUtc,
            notification.ReadAtUtc);

    private static string MessageFor(string kind) => kind switch
    {
        CrmTaskNotificationKinds.DueIn24Hours => "До срока задачи осталось меньше 24 часов.",
        CrmTaskNotificationKinds.DueIn1Hour => "До срока задачи осталось меньше часа.",
        CrmTaskNotificationKinds.Overdue => "Срок задачи истёк.",
        _ => "Изменился срок CRM-задачи."
    };
}
