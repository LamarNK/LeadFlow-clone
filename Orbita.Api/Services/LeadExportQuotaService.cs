using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

public sealed class LeadExportQuotaService(OrbitaDbContext db)
{
    public async Task<bool> CanExportToBitrixAsync(Guid bitrixInstanceId, CancellationToken ct = default)
    {
        var instance = await db.BitrixInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == bitrixInstanceId, ct)
            .ConfigureAwait(false);
        if (instance?.LeadExportLimit is not int limit || limit <= 0)
        {
            return true;
        }

        return instance.LeadExportSessionCount < limit;
    }

    public async Task RecordSuccessfulExportAsync(Guid bitrixInstanceId, CancellationToken ct = default)
    {
        var instance = await db.BitrixInstances
            .FirstOrDefaultAsync(x => x.Id == bitrixInstanceId, ct)
            .ConfigureAwait(false);
        if (instance is null)
        {
            return;
        }

        instance.LeadExportSessionCount++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetSessionsForOfficeAsync(Guid officeId, CancellationToken ct = default)
    {
        var instances = await db.BitrixInstances
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var instance in instances)
        {
            instance.LeadExportSessionCount = 0;
            instance.LeadExportSessionStartedAtUtc = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetSessionsForOfficesAsync(IEnumerable<Guid> officeIds, CancellationToken ct = default)
    {
        var ids = officeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var instances = await db.BitrixInstances
            .Where(x => ids.Contains(x.OfficeId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var instance in instances)
        {
            instance.LeadExportSessionCount = 0;
            instance.LeadExportSessionStartedAtUtc = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static string BuildLimitReachedMessage(int limit, string? bitrixName = null) =>
        bitrixName is { Length: > 0 }
            ? $"Достигнут лимит выгрузки для «{bitrixName}» ({limit} лидов). Дубли в лимит не входят. Остановите и снова запустите мониторинг или увеличьте лимит в настройках связей."
            : $"Достигнут лимит выгрузки для Битрикса ({limit} лидов). Дубли в лимит не входят. Остановите и снова запустите мониторинг или увеличьте лимит в настройках связей.";
}