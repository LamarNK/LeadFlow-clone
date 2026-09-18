using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerScheduleManager(
    OrbitaDbContext db,
    IPanelRealtimeNotifier realtime,
    IWorkerPushNotifier? workerPush = null,
    LeadExportQuotaService? leadExportQuota = null,
    WorkerScheduleService? fallbackSchedule = null)
{
    private static readonly string[] GroupNames = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];

    public async Task<WorkerScheduleOfficeDto?> GetAsync(OfficeScope scope, Guid? requestedOfficeId, int? selectedDayOff, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return null;
        await EnsureOfficeAsync(id, null, ct);
        var settings = await db.WorkerScheduleOffices.AsNoTracking().SingleAsync(x => x.OfficeId == id, ct);
        var groups = await db.WorkerScheduleGroups.AsNoTracking()
            .Include(x => x.Assignments).ThenInclude(x => x.Worker)
            .Where(x => x.OfficeId == id)
            .OrderBy(x => x.DayOff)
            .ToListAsync(ct);
        var allWorkers = await db.Workers.AsNoTracking().Where(x => x.OfficeId == id).OrderBy(x => x.DisplayName).ToListAsync(ct);
        var assignmentMap = groups.SelectMany(x => x.Assignments.Select(a => (a, x))).ToDictionary(x => x.a.WorkerId);
        var candidates = allWorkers.Select(worker =>
        {
            assignmentMap.TryGetValue(worker.Id, out var assigned);
            return new WorkerScheduleCandidateDto(worker.Id, worker.DisplayName, worker.IsEnabled,
                WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, DateTime.UtcNow), assigned.x?.Id, assigned.x?.Name, assigned.a?.Shift);
        }).ToList();
        return new WorkerScheduleOfficeDto(MapSettings(settings), groups.Select(x => MapGroup(x, settings)).ToList(), candidates, selectedDayOff);
    }

    public async Task<(bool Success, string? Error, WorkerScheduleConflict? Conflict)> AddAsync(
        OfficeScope scope, Guid? requestedOfficeId, AddWorkerScheduleRequest request, string? userId, CancellationToken ct = default)
    {
        if (request.Shift is not (WorkerScheduleShifts.Shift1 or WorkerScheduleShifts.Shift2))
            return (false, "Неизвестная смена.", null);
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.", null);
        var distinctWorkerIds = request.WorkerIds.Distinct().ToArray();
        if (request.WorkerIds.Count != distinctWorkerIds.Length)
            return (false, "Список воркеров содержит дубликаты.", null);
        var group = await db.WorkerScheduleGroups.Include(x => x.Assignments).ThenInclude(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == request.GroupId && x.OfficeId == id, ct);
        if (group is null) return (false, "Группа не найдена.", null);
        var workers = await db.Workers.Where(x => x.OfficeId == id && distinctWorkerIds.Contains(x.Id)).ToListAsync(ct);
        if (workers.Count != distinctWorkerIds.Length) return (false, "Один или несколько воркеров не найдены.", null);
        var existing = await db.WorkerScheduleAssignments.Include(x => x.Group).Where(x => distinctWorkerIds.Contains(x.WorkerId)).ToListAsync(ct);
        var conflict = existing.FirstOrDefault();
        if (conflict is not null)
            return (false, $"Воркер уже находится в группе «{conflict.Group.Name}».", new WorkerScheduleConflict("Воркер уже назначен в расписание.", conflict.WorkerId, conflict.GroupId, conflict.Group.Name));
        foreach (var worker in workers)
            db.WorkerScheduleAssignments.Add(new WorkerScheduleAssignmentEntity { WorkerId = worker.Id, GroupId = group.Id, Shift = request.Shift });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<WorkerScheduleAssignmentEntity>().Where(x => x.State == EntityState.Added))
                entry.State = EntityState.Detached;
            var raced = await db.WorkerScheduleAssignments.AsNoTracking().Include(x => x.Group)
                .FirstOrDefaultAsync(x => request.WorkerIds.Contains(x.WorkerId), ct);
            if (raced is null) throw;
            return (false, $"Воркер уже находится в группе «{raced.Group.Name}».",
                new WorkerScheduleConflict("Воркер уже назначен в расписание.", raced.WorkerId, raced.GroupId, raced.Group.Name));
        }
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id);
        return (true, null, null);
    }

    public async Task<(bool Success, string? Error)> MoveAsync(OfficeScope scope, Guid? requestedOfficeId, MoveWorkerScheduleRequest request, string? userId, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.");
        if (request.Shift is not (WorkerScheduleShifts.Shift1 or WorkerScheduleShifts.Shift2)) return (false, "Неизвестная смена.");
        var groupExists = await db.WorkerScheduleGroups.AnyAsync(x => x.Id == request.GroupId && x.OfficeId == id, ct);
        if (!groupExists) return (false, "Группа не найдена.");
        var workerExists = await db.Workers.AnyAsync(x => x.Id == request.WorkerId && x.OfficeId == id, ct);
        if (!workerExists) return (false, "Воркер не найден.");
        var assignment = await db.WorkerScheduleAssignments.FirstOrDefaultAsync(x => x.WorkerId == request.WorkerId, ct);
        if (assignment is null) db.WorkerScheduleAssignments.Add(new WorkerScheduleAssignmentEntity { WorkerId = request.WorkerId, GroupId = request.GroupId, Shift = request.Shift });
        else { assignment.GroupId = request.GroupId; assignment.Shift = request.Shift; }
        await db.SaveChangesAsync(ct);
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id, request.WorkerId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RemoveAsync(OfficeScope scope, Guid? requestedOfficeId, Guid workerId, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.");
        var assignment = await db.WorkerScheduleAssignments.Include(x => x.Worker).FirstOrDefaultAsync(x => x.WorkerId == workerId && x.Worker.OfficeId == id, ct);
        if (assignment is null) return (false, "Назначение не найдено.");
        db.Remove(assignment);
        await db.SaveChangesAsync(ct);
        if (fallbackSchedule is not null) await fallbackSchedule.ApplyAsync(ct);
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id, workerId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateSettingsAsync(OfficeScope scope, Guid? requestedOfficeId, UpdateWorkerScheduleSettingsRequest request, string? userId, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.");
        if (!TryTime(request.DayStartLocalTime, out var dayStart) || !TryTime(request.DayEndLocalTime, out var dayEnd)
            || !TryTime(request.NightStartLocalTime, out var nightStart) || !TryTime(request.NightEndLocalTime, out var nightEnd))
            return (false, "Время должно быть в формате HH:mm.");
        if (!WindowsPartitionDay(dayStart, dayEnd, nightStart, nightEnd))
            return (false, "Дневное и ночное окна должны без пересечений покрывать полные сутки.");
        var settings = await EnsureOfficeAsync(id, userId, ct);
        settings.DayStartLocalTime = dayStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        settings.DayEndLocalTime = dayEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        settings.NightStartLocalTime = nightStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        settings.NightEndLocalTime = nightEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId.Trim()); }
            catch { return (false, "Неизвестный часовой пояс."); }
            settings.TimeZoneId = request.TimeZoneId.Trim();
        }
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateGroupAsync(OfficeScope scope, Guid? requestedOfficeId, Guid groupId, UpdateWorkerScheduleGroupRequest request, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.");
        if (request.CurrentWeekShift is not (WorkerScheduleShifts.DayFirst or WorkerScheduleShifts.NightFirst)) return (false, "Неизвестное начальное состояние смен.");
        var group = await db.WorkerScheduleGroups.FirstOrDefaultAsync(x => x.Id == groupId && x.OfficeId == id, ct);
        if (group is null) return (false, "Группа не найдена.");
        for (var attempt = 0; ; attempt++)
        {
            group.CurrentWeekShift = request.CurrentWeekShift;
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                await db.Entry(group).ReloadAsync(ct);
            }
        }
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error, WorkerScheduleDistributionResult? Result)> AutoDistributeAsync(OfficeScope scope, Guid? requestedOfficeId, CancellationToken ct = default)
    {
        var officeId = await ResolveOfficeIdAsync(scope, requestedOfficeId, ct);
        if (officeId is not Guid id) return (false, "Выберите офис.", null);
        await EnsureOfficeAsync(id, null, ct);
        var groups = await db.WorkerScheduleGroups.Where(x => x.OfficeId == id).OrderBy(x => x.DayOff).ToListAsync(ct);
        var workers = await db.Workers.Where(x => x.OfficeId == id).OrderBy(x => x.DisplayName).ToListAsync(ct);
        var settings = await db.WorkerScheduleOffices.SingleAsync(x => x.OfficeId == id, ct);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ResolveZone(settings.TimeZoneId));
        var currentDay = ToScheduleDay(localNow.DayOfWeek);
        var localDate = DateOnly.FromDateTime(localNow);
        foreach (var group in groups)
        {
            group.CurrentWeekShift = WorkerScheduleCalculator.ReferenceOrientation(group.DayOff, currentDay);
            group.LastAppliedOffDate = LatestDayOff(localDate, currentDay, group.DayOff);
        }
        var current = await db.WorkerScheduleAssignments.Where(x => x.Worker.OfficeId == id).ToListAsync(ct);
        var currentByWorker = current.ToDictionary(x => x.WorkerId);
        var desiredWorkerIds = workers.Select(x => x.Id).ToHashSet();
        foreach (var stale in current.Where(x => !desiredWorkerIds.Contains(x.WorkerId)))
            db.WorkerScheduleAssignments.Remove(stale);
        var counts = groups.ToDictionary(x => x.Id, _ => 0);
        for (var i = 0; i < workers.Count; i++)
        {
            var groupIndex = i * groups.Count / Math.Max(1, workers.Count);
            var group = groups[Math.Min(groupIndex, groups.Count - 1)];
            var position = counts[group.Id];
            var shift = position % 2 == 0 ? WorkerScheduleShifts.Shift1 : WorkerScheduleShifts.Shift2;
            if (currentByWorker.TryGetValue(workers[i].Id, out var assignment))
            {
                assignment.GroupId = group.Id;
                assignment.Shift = shift;
            }
            else
            {
                db.WorkerScheduleAssignments.Add(new WorkerScheduleAssignmentEntity
                {
                    WorkerId = workers[i].Id,
                    GroupId = group.Id,
                    Shift = shift
                });
            }
            counts[group.Id] = position + 1;
        }
        await db.SaveChangesAsync(ct);
        await ApplyAsync(ct);
        realtime.Notify([PanelChangeKind.Schedule, PanelChangeKind.Workers, PanelChangeKind.Dashboard], id);
        return (true, null, new WorkerScheduleDistributionResult(workers.Count, counts));
    }

    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var offices = await db.Offices.AsNoTracking().Where(x => x.IsEnabled).Select(x => x.Id).ToListAsync(ct);
        foreach (var officeId in offices) await EnsureOfficeAsync(officeId, null, ct);
        var settings = await db.WorkerScheduleOffices.AsNoTracking().ToDictionaryAsync(x => x.OfficeId, ct);
        var groups = await db.WorkerScheduleGroups.Include(x => x.Assignments).Where(x => settings.Keys.Contains(x.OfficeId)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var touchedOffices = await ApplyDayOffTransitionsAsync(groups, settings, now, ct);
        var workers = await db.Workers
            .Where(x => db.WorkerScheduleAssignments.Any(a => a.WorkerId == x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var changedWorkers = new List<WorkerEntity>();
        var officesToReset = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var config = settings[group.OfficeId];
            var zone = ResolveZone(config.TimeZoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
            foreach (var assignment in group.Assignments)
            {
                var shouldRun = WorkerScheduleCalculator.IsActiveNow(group.DayOff, group.CurrentWeekShift, assignment.Shift, local,
                    config.DayStartLocalTime, config.DayEndLocalTime, config.NightStartLocalTime, config.NightEndLocalTime);
                workers.TryGetValue(assignment.WorkerId, out var worker);
                var shouldPause = worker is null || !worker.IsEnabled || !shouldRun;
                if (worker is null || worker.IsMonitoringPaused == shouldPause) continue;
                if (!shouldPause && !officesToReset.Contains(worker.OfficeId)
                    && !await db.Workers.AnyAsync(x => x.OfficeId == worker.OfficeId && x.Id != worker.Id
                        && x.IsEnabled && !x.IsMonitoringPaused, ct))
                    officesToReset.Add(worker.OfficeId);
                worker.IsMonitoringPaused = shouldPause;
                changedWorkers.Add(worker);
                touchedOffices.Add(worker.OfficeId);
            }
        }
        if (officesToReset.Count > 0 && leadExportQuota is not null)
            await leadExportQuota.ResetSessionsForOfficesAsync(officesToReset, ct);
        if (changedWorkers.Count > 0) await db.SaveChangesAsync(ct);
        foreach (var worker in changedWorkers)
        {
            if (workerPush is not null) await workerPush.PushConfigChangedAsync(worker.Id, ct);
            realtime.Notify([PanelChangeKind.Workers, PanelChangeKind.Dashboard], worker.OfficeId, worker.Id);
        }
        foreach (var officeId in touchedOffices)
            realtime.Notify([PanelChangeKind.Schedule], officeId);
        return changedWorkers.Count;
    }

    private async Task<HashSet<Guid>> ApplyDayOffTransitionsAsync(
        IReadOnlyList<WorkerScheduleGroupEntity> groups,
        IReadOnlyDictionary<Guid, WorkerScheduleOfficeEntity> settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var touchedOffices = new HashSet<Guid>();
            foreach (var group in groups)
            {
                var zone = ResolveZone(settings[group.OfficeId].TimeZoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
                var localDate = DateOnly.FromDateTime(local);
                var currentDay = ToScheduleDay(local.DayOfWeek);
                var latestOffDate = LatestDayOff(localDate, currentDay, group.DayOff);
                if (group.LastAppliedOffDate == latestOffDate) continue;

                if (!group.LastAppliedOffDate.HasValue)
                {
                    group.CurrentWeekShift = WorkerScheduleCalculator.ReferenceOrientation(group.DayOff, currentDay);
                }
                else
                {
                    var missedCycles = Math.Max(0, latestOffDate.DayNumber - group.LastAppliedOffDate.Value.DayNumber) / 7;
                    if (missedCycles % 2 != 0)
                        group.CurrentWeekShift = WorkerScheduleShifts.Opposite(group.CurrentWeekShift);
                }
                group.LastAppliedOffDate = latestOffDate;
                touchedOffices.Add(group.OfficeId);
            }

            if (touchedOffices.Count == 0) return touchedOffices;
            try
            {
                await db.SaveChangesAsync(ct);
                return touchedOffices;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // Another scheduler won the same day-off transition. Reload every group and
                // calculate again so a stale orientation can never be flipped a second time.
                foreach (var group in groups)
                    await db.Entry(group).ReloadAsync(ct);
            }
        }
    }

    public async Task<WorkerScheduleOfficeEntity> EnsureOfficeAsync(Guid officeId, string? userId, CancellationToken ct)
    {
        var settings = await db.WorkerScheduleOffices.FirstOrDefaultAsync(x => x.OfficeId == officeId, ct);
        if (settings is null)
        {
            settings = new WorkerScheduleOfficeEntity { OfficeId = officeId, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = userId };
            db.WorkerScheduleOffices.Add(settings);
        }
        var existingDays = await db.WorkerScheduleGroups.Where(x => x.OfficeId == officeId).Select(x => x.DayOff).ToListAsync(ct);
        for (var day = 0; day < 7; day++)
        {
            if (existingDays.Contains(day)) continue;
            var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ResolveZone(settings.TimeZoneId));
            var today = ToScheduleDay(localToday.DayOfWeek);
            var localDate = DateOnly.FromDateTime(localToday);
            db.WorkerScheduleGroups.Add(new WorkerScheduleGroupEntity
            {
                Id = Guid.NewGuid(), OfficeId = officeId, DayOff = day, Name = GroupNames[day],
                CurrentWeekShift = WorkerScheduleCalculator.ReferenceOrientation(day, today),
                LastAppliedOffDate = localDate.AddDays(-((today - day + 7) % 7))
            });
        }
        if (!db.ChangeTracker.HasChanges()) return settings;
        try
        {
            await db.SaveChangesAsync(ct);
            return settings;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<WorkerScheduleGroupEntity>().Where(x => x.State == EntityState.Added))
                entry.State = EntityState.Detached;
            if (db.Entry(settings).State == EntityState.Added)
                db.Entry(settings).State = EntityState.Detached;
            var existing = await db.WorkerScheduleOffices.FirstOrDefaultAsync(x => x.OfficeId == officeId, ct);
            if (existing is null) throw;
            return existing;
        }
    }

    private async Task<Guid?> ResolveOfficeIdAsync(OfficeScope scope, Guid? requested, CancellationToken ct)
    {
        var id = scope.ResolveFilter(requested);
        return id is Guid officeId && await db.Offices.AsNoTracking().AnyAsync(x => x.Id == officeId && x.IsEnabled, ct) ? officeId : null;
    }

    private static WorkerScheduleSettingsDto MapSettings(WorkerScheduleOfficeEntity x) => new(x.OfficeId, x.OfficeId, x.DayStartLocalTime, x.DayEndLocalTime, x.NightStartLocalTime, x.NightEndLocalTime, x.TimeZoneId, x.UpdatedAtUtc);

    private static WorkerScheduleGroupDto MapGroup(WorkerScheduleGroupEntity group, WorkerScheduleOfficeEntity settings)
    {
        var zone = ResolveZone(settings.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var today = ToScheduleDay(local.DayOfWeek);
        var calendar = WorkerScheduleCalculator.Calculate(group.DayOff, group.CurrentWeekShift, today);
        var day = calendar[today];
        var workers = group.Assignments.OrderBy(x => x.Worker.DisplayName).Select(x =>
        {
            var state = x.Shift == WorkerScheduleShifts.Shift1 ? day.Shift1 : day.Shift2;
            return new WorkerScheduleWorkerDto(x.WorkerId, x.Worker.DisplayName, x.Worker.IsEnabled, x.Shift,
                WorkerOnlineRules.IsOnline(x.Worker.LastSeenAtUtc, DateTime.UtcNow), x.Worker.IsMonitoringPaused,
                state, NextWorkingDay(calendar, today, x.Shift), x.Worker.LastSeenAtUtc);
        }).ToList();
        return new WorkerScheduleGroupDto(group.Id, group.OfficeId, group.DayOff, group.Name, group.CurrentWeekShift, group.LastAppliedOffDate, workers.Count, workers.Count(x => x.Shift == WorkerScheduleShifts.Shift1), workers.Count(x => x.Shift == WorkerScheduleShifts.Shift2), workers, calendar);
    }

    private static string? NextWorkingDay(IReadOnlyList<WorkerScheduleDayDto> calendar, int today, string shift)
    {
        for (var offset = 1; offset <= 7; offset++)
        {
            var day = calendar[(today + offset) % 7];
            var state = shift == WorkerScheduleShifts.Shift1 ? day.Shift1 : day.Shift2;
            if (state != WorkerScheduleShifts.Off) return day.Label;
        }
        return null;
    }

    private static bool TryTime(string? value, out TimeOnly time) => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out time);
    private static bool WindowsPartitionDay(TimeOnly dayStart, TimeOnly dayEnd, TimeOnly nightStart, TimeOnly nightEnd)
    {
        if (dayStart == dayEnd || nightStart == nightEnd) return false;
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var time = new TimeOnly(minute / 60, minute % 60);
            if (InWindow(time, dayStart, dayEnd) == InWindow(time, nightStart, nightEnd)) return false;
        }
        return true;
    }
    private static bool InWindow(TimeOnly time, TimeOnly from, TimeOnly to) =>
        from < to ? time >= from && time < to : time >= from || time < to;
    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
    private static int ToScheduleDay(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static DateOnly LatestDayOff(DateOnly localDate, int currentDay, int dayOff) =>
        localDate.AddDays(-((currentDay - dayOff + 7) % 7));
}
