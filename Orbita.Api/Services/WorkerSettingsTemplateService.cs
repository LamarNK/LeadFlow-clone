using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerSettingsTemplateService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IOrbitaQueryCache? queryCache = null,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    public async Task<(IReadOnlyList<WorkerSettingsTemplateDto>? Templates, string? Error)> ListAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var (worker, error) = await GetAccessibleWorkerAsync(workerId, scope, ct);
        if (error is not null || worker is null)
        {
            return (null, error);
        }

        Task<IReadOnlyList<WorkerSettingsTemplateDto>> Load(CancellationToken token) =>
            ListForOfficeAsync(worker.OfficeId, token);
        var templates = queryCache is null
            ? await Load(ct)
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Reference,
                worker.OfficeId,
                $"office:{worker.OfficeId:D}",
                new { Kind = "worker-settings-templates" },
                OrbitaCachePolicy.Reference,
                Load,
                ct);
        return (templates, null);
    }

    public async Task<(WorkerSettingsTemplateMutationResultDto? Result, string? Error)> CreateAsync(
        Guid workerId,
        CreateWorkerSettingsTemplateRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (worker, error) = await GetAccessibleWorkerAsync(workerId, scope, ct);
        if (error is not null || worker is null)
        {
            return (null, error);
        }

        var (name, nameError) = WorkerSettingsTemplateRules.NormalizeName(request.Name);
        if (nameError is not null || name is null)
        {
            return (null, nameError);
        }

        if (await NameExistsAsync(worker.OfficeId, name, excludeId: null, ct))
        {
            return (null, "Шаблон с таким названием уже есть в этом офисе.");
        }

        var now = DateTime.UtcNow;
        var entity = new WorkerSettingsTemplateEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = worker.OfficeId,
            Name = name,
            NameNormalized = WorkerSettingsTemplateRules.NormalizeKey(name),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        ApplyPayload(entity, WorkerSettingsTemplatePayload.Normalize(request.Settings));
        db.WorkerSettingsTemplates.Add(entity);
        await db.SaveChangesAsync(ct);
        panelRealtime?.Notify([PanelChangeKind.Reference], worker.OfficeId, workerId);

        var dto = ToDto(entity);
        return (new WorkerSettingsTemplateMutationResultDto(
            dto,
            await ListForOfficeAsync(worker.OfficeId, ct),
            "Шаблон сохранён."), null);
    }

    public async Task<(WorkerSettingsTemplateMutationResultDto? Result, string? Error)> UpdateAsync(
        Guid workerId,
        Guid templateId,
        UpdateWorkerSettingsTemplateRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (worker, error) = await GetAccessibleWorkerAsync(workerId, scope, ct);
        if (error is not null || worker is null)
        {
            return (null, error);
        }

        var (name, nameError) = WorkerSettingsTemplateRules.NormalizeName(request.Name);
        if (nameError is not null || name is null)
        {
            return (null, nameError);
        }

        var entity = await db.WorkerSettingsTemplates
            .FirstOrDefaultAsync(x => x.Id == templateId && x.OfficeId == worker.OfficeId, ct);
        if (entity is null)
        {
            return (null, "Шаблон не найден.");
        }

        if (await NameExistsAsync(worker.OfficeId, name, entity.Id, ct))
        {
            return (null, "Шаблон с таким названием уже есть в этом офисе.");
        }

        entity.Name = name;
        entity.NameNormalized = WorkerSettingsTemplateRules.NormalizeKey(name);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        ApplyPayload(entity, WorkerSettingsTemplatePayload.Normalize(request.Settings));
        await db.SaveChangesAsync(ct);
        panelRealtime?.Notify([PanelChangeKind.Reference], worker.OfficeId, workerId);

        var dto = ToDto(entity);
        return (new WorkerSettingsTemplateMutationResultDto(
            dto,
            await ListForOfficeAsync(worker.OfficeId, ct),
            "Шаблон обновлён."), null);
    }

    public async Task<(WorkerSettingsTemplateMutationResultDto? Result, string? Error)> DeleteAsync(
        Guid workerId,
        Guid templateId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var (worker, error) = await GetAccessibleWorkerAsync(workerId, scope, ct);
        if (error is not null || worker is null)
        {
            return (null, error);
        }

        var entity = await db.WorkerSettingsTemplates
            .FirstOrDefaultAsync(x => x.Id == templateId && x.OfficeId == worker.OfficeId, ct);
        if (entity is null)
        {
            return (null, "Шаблон не найден.");
        }

        db.WorkerSettingsTemplates.Remove(entity);
        await db.SaveChangesAsync(ct);
        panelRealtime?.Notify([PanelChangeKind.Reference], worker.OfficeId, workerId);
        return (new WorkerSettingsTemplateMutationResultDto(
            null,
            await ListForOfficeAsync(worker.OfficeId, ct),
            "Шаблон удалён."), null);
    }

    private async Task<(WorkerEntity? Worker, string? Error)> GetAccessibleWorkerAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct)
    {
        if (!scope.HasAccess)
        {
            return (null, "Воркер не найден.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        return worker is null ? (null, "Воркер не найден.") : (worker, null);
    }

    private async Task<bool> NameExistsAsync(
        Guid officeId,
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        var key = WorkerSettingsTemplateRules.NormalizeKey(name);
        return await db.WorkerSettingsTemplates.AnyAsync(
            x => x.OfficeId == officeId
                 && x.NameNormalized == key
                 && (excludeId == null || x.Id != excludeId),
            ct);
    }

    private async Task<IReadOnlyList<WorkerSettingsTemplateDto>> ListForOfficeAsync(
        Guid officeId,
        CancellationToken ct)
    {
        var rows = await db.WorkerSettingsTemplates
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private static void ApplyPayload(WorkerSettingsTemplateEntity entity, WorkerSettingsTemplatePayload payload)
    {
        entity.MaxConcurrentAccounts = payload.MaxConcurrentAccounts;
        entity.ResponseFilterEnabled = payload.ResponseFilterEnabled;
        entity.ResponseFilterExcludeFemale = payload.ResponseFilterExcludeFemale;
        entity.ResponseFilterExcludeMale = payload.ResponseFilterExcludeMale;
        entity.ResponseFilterMaxAgeMale = payload.ResponseFilterMaxAgeMale;
        entity.ResponseFilterMaxAgeFemale = payload.ResponseFilterMaxAgeFemale;
        entity.ResponseFilterMaxAgeDays = payload.ResponseFilterMaxResponseAgeDays;
        entity.ResponseHighlightEnabled = payload.ResponseHighlightEnabled;
        entity.ResponseHighlightAgeBuckets = payload.ResponseHighlightAgeBuckets;
        entity.AutoScheduleEnabled = payload.AutoScheduleEnabled;
        entity.AutoScheduleDays = payload.AutoScheduleDays;
        entity.AutoScheduleFromLocalTime = payload.AutoScheduleFromLocalTime;
        entity.AutoScheduleToLocalTime = payload.AutoScheduleToLocalTime;
        entity.MessengerAutoReplyEnabled = payload.MessengerAutoReplyEnabled;
        entity.MessengerAutoReplyMessage = payload.MessengerAutoReplyMessage;
        entity.PhoneUnchangedHours = payload.PhoneUnchangedHours;
        entity.AutoDeliverToCrm = payload.AutoDeliverToCrm;
        entity.AutoDeliverToBitrix = payload.AutoDeliverToBitrix;
        entity.AdsPowerEnabled = payload.AdsPowerEnabled;
        entity.MultiloginEnabled = payload.MultiloginEnabled;
        entity.LocalChromeEnabled = payload.LocalChromeEnabled;
    }

    private static WorkerSettingsTemplateDto ToDto(WorkerSettingsTemplateEntity entity) =>
        new(
            entity.Id,
            entity.OfficeId,
            entity.Name,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            new WorkerSettingsTemplatePayload(
                entity.MaxConcurrentAccounts,
                entity.ResponseFilterEnabled,
                entity.ResponseFilterExcludeFemale,
                entity.ResponseFilterExcludeMale,
                entity.ResponseFilterMaxAgeMale,
                entity.ResponseFilterMaxAgeFemale,
                entity.ResponseFilterMaxAgeDays,
                entity.ResponseHighlightEnabled,
                entity.ResponseHighlightAgeBuckets,
                entity.AutoScheduleEnabled,
                entity.AutoScheduleDays,
                entity.AutoScheduleFromLocalTime,
                entity.AutoScheduleToLocalTime,
                entity.MessengerAutoReplyEnabled,
                entity.MessengerAutoReplyMessage,
                entity.PhoneUnchangedHours,
                entity.AutoDeliverToCrm,
                entity.AutoDeliverToBitrix,
                entity.AdsPowerEnabled,
                entity.MultiloginEnabled,
                entity.LocalChromeEnabled));
}
