using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class WorkerScheduleWebService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IWorkerScheduleWebService
{
    public async Task<WorkerScheduleOfficeDto?> GetAsync(int? dayOff, CancellationToken ct = default)
    {
        if (!previewOptions.Value.Enabled) return await api.GetWorkerScheduleAsync(dayOff, ct);
        var officeId = officeContext.EffectiveOfficeId ?? DesignPreviewData.PreviewOfficeId;
        var workers = DesignPreviewData.GetWorkers(officeId).OrderBy(x => x.DisplayName).ToList();
        var settings = new WorkerScheduleSettingsDto(Guid.Parse("94000000-0000-0000-0000-000000000001"), officeId,
            "07:00", "19:00", "19:00", "07:00", "Europe/Moscow", DateTime.UtcNow);
        var names = new[] { "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье" };
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId));
        var today = localToday.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localToday.DayOfWeek - 1;
        var groups = new List<WorkerScheduleGroupDto>();
        var candidates = new List<WorkerScheduleCandidateDto>();
        for (var day = 0; day < 7; day++)
        {
            var id = Guid.Parse($"94000000-0000-0000-0000-{day + 2:000000000000}");
            var orientation = WorkerScheduleCalculator.ReferenceOrientation(day, today);
            var calendar = WorkerScheduleCalculator.Calculate(day, orientation, today);
            var assigned = workers.Where((_, index) => index % 7 == day).Select((worker, index) =>
            {
                var shift = index % 2 == 0 ? WorkerScheduleShifts.Shift1 : WorkerScheduleShifts.Shift2;
                var todayState = shift == WorkerScheduleShifts.Shift1 ? calendar[today].Shift1 : calendar[today].Shift2;
                var nextWorkingDay = Enumerable.Range(1, 7)
                    .Select(offset => calendar[(today + offset) % 7])
                    .FirstOrDefault(next => (shift == WorkerScheduleShifts.Shift1 ? next.Shift1 : next.Shift2) != WorkerScheduleShifts.Off)
                    ?.Label;
                return new WorkerScheduleWorkerDto(worker.Id, worker.DisplayName, worker.IsEnabled, shift,
                    worker.IsOnline, worker.IsMonitoringPaused, todayState, nextWorkingDay, worker.LastSeenAtUtc);
            }).ToList();
            groups.Add(new WorkerScheduleGroupDto(id, officeId, day, names[day], orientation, null,
                assigned.Count, assigned.Count(x => x.Shift == WorkerScheduleShifts.Shift1), assigned.Count(x => x.Shift == WorkerScheduleShifts.Shift2), assigned,
                calendar));
            candidates.AddRange(assigned.Select(x => new WorkerScheduleCandidateDto(x.WorkerId, x.DisplayName, true, x.IsOnline, id, names[day], x.Shift)));
        }
        return new WorkerScheduleOfficeDto(settings, groups, candidates.OrderBy(x => x.DisplayName).ToList(), dayOff);
    }
}
