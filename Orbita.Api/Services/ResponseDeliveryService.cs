using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Multi-channel delivery: CRM and/or Bitrix. Partial success is allowed.
/// Does not change worker collection ownership.
/// </summary>
public sealed class ResponseDeliveryService(
    OrbitaDbContext db,
    CrmWorkspaceService crm,
    DistributionEngine distributionEngine,
    CandidateAutoDistributionService autoDistribution,
    ManualBitrixSendService manualBitrixSend,
    CandidateDuplicateService duplicateService,
    IPanelRealtimeNotifier panelRealtime,
    ResponseCacheInvalidator cacheInvalidator)
{
    public async Task<DeliverResponseResultDto> DeliverAsync(
        Guid responseId,
        DeliverResponseRequest request,
        OfficeScope scope,
        string source = DistributionModes.Manual,
        CancellationToken ct = default)
    {
        await using var invalidationBatch = cacheInvalidator.BeginBatch();

        if (!request.ToCrm && !request.ToBitrix)
        {
            return Fail(ResponseStatuses.ActionRequired, "Выберите канал: CRM и/или Bitrix.");
        }

        var entity = await db.CandidateResponses
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == responseId, ct);
        if (entity is null)
        {
            return Fail(ResponseStatuses.Error, "Отклик не найден.");
        }

        var sharedWithScope = false;
        if (!scope.IsGlobalAdmin && scope.OfficeId is Guid scopeOffice)
        {
            sharedWithScope = await db.ResponseCrmDeliveries.AsNoTracking()
                .AnyAsync(
                    d => d.ResponseId == entity.Id
                         && d.OfficeId == scopeOffice
                         && d.Outcome == ResponseCrmDeliveryOutcomes.Sent,
                    ct);
            if (!sharedWithScope)
            {
                sharedWithScope = await db.CrmCandidateCards.AsNoTracking()
                    .AnyAsync(c => c.ResponseId == entity.Id && c.OfficeId == scopeOffice, ct);
            }
        }

        if (!scope.CanAccessResponse(entity.OfficeId, entity.Worker?.OfficeId, sharedWithScope))
        {
            return Fail(ResponseStatuses.Error, "Нет доступа к отклику.");
        }

        if (entity.IsLocalDuplicate)
        {
            return Fail(ResponseStatuses.Duplicate, entity.DuplicateSummary.Length > 0
                ? entity.DuplicateSummary
                : "Локальный дубль.");
        }

        var officeIds = ResolveOfficeIds(request);
        var bitrixIds = ResolveBitrixInstanceIds(request);

        // Primary office for Bitrix route / entity bind: first selected, else response/worker office.
        var primaryOfficeId = officeIds.Count > 0
            ? officeIds[0]
            : entity.OfficeId ?? entity.Worker?.OfficeId;

        if (request.ToCrm && officeIds.Count == 0)
        {
            return Fail(ResponseStatuses.ActionRequired, "Укажите офис для CRM.");
        }

        if (request.ToBitrix && primaryOfficeId is null)
        {
            return Fail(ResponseStatuses.ActionRequired, "Укажите офис назначения.");
        }

        // Validate CRM offices are enabled and accept CRM.
        if (request.ToCrm)
        {
            var enabledCrmOffices = await db.Offices.AsNoTracking()
                .Where(x => officeIds.Contains(x.Id) && x.IsEnabled && x.CrmEnabled)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (enabledCrmOffices.Count == 0)
            {
                return Fail(ResponseStatuses.Error, "Нет доступных офисов с включённой CRM.");
            }

            officeIds = enabledCrmOffices;
            primaryOfficeId = officeIds[0];
        }
        else if (primaryOfficeId is Guid checkOffice
                 && !await db.Offices.AnyAsync(x => x.Id == checkOffice && x.IsEnabled, ct))
        {
            return Fail(ResponseStatuses.Error, "Офис не найден или отключён.");
        }

        var channels = new List<DeliverResponseChannelResultDto>();

        if (request.ToCrm)
        {
            foreach (var officeId in officeIds)
            {
                channels.Add(await DeliverCrmAsync(entity, officeId, source, ct));
            }
        }

        if (request.ToBitrix && primaryOfficeId is Guid bitrixOffice)
        {
            if (bitrixIds.Count == 0)
            {
                // No explicit portals → office distribution route (legacy single path).
                channels.Add(await DeliverBitrixAsync(
                    entity,
                    bitrixOffice,
                    bitrixInstanceId: null,
                    useRoute: true,
                    scope,
                    source,
                    ct));
            }
            else
            {
                foreach (var instanceId in bitrixIds)
                {
                    channels.Add(await DeliverBitrixAsync(
                        entity,
                        bitrixOffice,
                        bitrixInstanceId: instanceId,
                        useRoute: false,
                        scope,
                        source,
                        ct));
                }
            }
        }

        // Ownership: bind once to worker home office — never transfer to selected CRM targets.
        var anySuccess = channels.Any(x => x.Success);
        if (anySuccess && entity.OfficeId is null)
        {
            var homeOfficeId = entity.Worker?.OfficeId ?? primaryOfficeId;
            if (homeOfficeId is Guid bindOffice)
            {
                entity.OfficeId = bindOffice;
                if (entity.PersonId != Guid.Empty)
                {
                    var person = await db.CandidatePersons.FirstOrDefaultAsync(x => x.Id == entity.PersonId, ct);
                    if (person is not null && person.OfficeId is null)
                    {
                        person.OfficeId = bindOffice;
                    }
                }
            }
        }

        entity.ProcessedAt = DateTime.UtcNow;
        entity.Status = ResolveStatus(channels, request);
        entity.ErrorMessage = BuildSummary(channels) ?? string.Empty;
        await db.SaveChangesAsync(ct);
        await cacheInvalidator.InvalidateAsync(entity.OfficeId ?? primaryOfficeId);
        foreach (var officeId in officeIds)
        {
            await cacheInvalidator.InvalidateAsync(officeId);
        }

        // The response can be delivered to several CRM offices. Each board needs its own scoped event.
        var crmResults = channels.Where(x => x.Channel == "CRM").ToList();
        var responseVisibilityOfficeIds = new HashSet<Guid>();
        if (entity.OfficeId is Guid responseOfficeId)
        {
            responseVisibilityOfficeIds.Add(responseOfficeId);
        }
        else if (primaryOfficeId is Guid fallbackOfficeId)
        {
            responseVisibilityOfficeIds.Add(fallbackOfficeId);
        }

        foreach (var (officeId, result) in officeIds.Zip(crmResults))
        {
            if (result.Success)
            {
                responseVisibilityOfficeIds.Add(officeId);
                panelRealtime.Notify([PanelChangeKind.Crm], officeId, entity.WorkerId);
            }
        }

        foreach (var officeId in responseVisibilityOfficeIds)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Responses, PanelChangeKind.Dashboard, PanelChangeKind.NavBadges],
                officeId,
                entity.WorkerId);
        }

        await invalidationBatch.FlushAsync();

        return new DeliverResponseResultDto(
            anySuccess,
            entity.Status,
            string.IsNullOrWhiteSpace(entity.ErrorMessage) ? null : entity.ErrorMessage,
            entity.OfficeId,
            channels);
    }

    private static IReadOnlyList<Guid> ResolveOfficeIds(DeliverResponseRequest request)
    {
        if (request.OfficeIds is { Count: > 0 })
        {
            return request.OfficeIds.Where(id => id != Guid.Empty).Distinct().ToList();
        }

        if (request.OfficeId is Guid single && single != Guid.Empty)
        {
            return [single];
        }

        return [];
    }

    private static IReadOnlyList<Guid> ResolveBitrixInstanceIds(DeliverResponseRequest request)
    {
        if (request.BitrixInstanceIds is { Count: > 0 })
        {
            return request.BitrixInstanceIds.Where(id => id != Guid.Empty).Distinct().ToList();
        }

        if (request.BitrixInstanceId is Guid single && single != Guid.Empty)
        {
            return [single];
        }

        return [];
    }

    public async Task<(BulkDeliverResponsesResultDto? Result, string? Error)> DeliverBulkAsync(
        BulkDeliverResponsesRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (request.ResponseIds.Count == 0)
        {
            return (null, "Выберите хотя бы один отклик.");
        }

        if (!request.ToCrm && !request.ToBitrix)
        {
            return (null, "Выберите канал: CRM и/или Bitrix.");
        }

        if (request.ResponseIds.Count > BulkResponsesBitrixSendService.MaxBatchSize)
        {
            return (null, $"За один раз можно отправить не более {BulkResponsesBitrixSendService.MaxBatchSize} откликов.");
        }

        var items = new List<BulkDeliverItemResultDto>();
        var succeeded = 0;
        var failed = 0;
        await using var invalidationBatch = cacheInvalidator.BeginBatch();
        foreach (var id in request.ResponseIds.Distinct())
        {
            var result = await DeliverAsync(
                id,
                new DeliverResponseRequest(
                    request.OfficeId,
                    request.ToCrm,
                    request.ToBitrix,
                    request.BitrixInstanceId,
                    request.UseBitrixRoute,
                    request.OfficeIds,
                    request.BitrixInstanceIds),
                scope,
                DistributionModes.Manual,
                ct);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            items.Add(new BulkDeliverItemResultDto(id, result.Success, result.Status, result.ErrorMessage));
        }

        await invalidationBatch.FlushAsync();

        return (new BulkDeliverResponsesResultDto(items.Count, succeeded, failed, items), null);
    }

    /// <summary>Auto path after ingest for a worker's delivery flags.</summary>
    public async Task ApplyAutoDeliveryAsync(
        CandidateResponseEntity entity,
        WorkerEntity worker,
        CancellationToken ct = default)
    {
        if (!worker.AutoDeliverToCrm && !worker.AutoDeliverToBitrix)
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Ожидает действия оператора.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var result = await DeliverAsync(
            entity.Id,
            new DeliverResponseRequest(
                worker.OfficeId,
                worker.AutoDeliverToCrm,
                worker.AutoDeliverToBitrix,
                BitrixInstanceId: null,
                UseBitrixRoute: true),
            OfficeScope.GlobalAdmin,
            DistributionModes.Auto,
            ct);

        // DeliverAsync reloads and saves; refresh entity status for callers that still hold the instance.
        await db.Entry(entity).ReloadAsync(ct);
        _ = result;
    }

    private async Task<DeliverResponseChannelResultDto> DeliverCrmAsync(
        CandidateResponseEntity entity,
        Guid officeId,
        string source,
        CancellationToken ct)
    {
        var localDup = await duplicateService.FindLocalDuplicateAsync(officeId, entity.PersonId, entity.Id, ct);
        if (localDup is not null)
        {
            StageCrm(entity.Id, officeId, null, ResponseCrmDeliveryOutcomes.Duplicate, source,
                "Дубль в CRM-контуре офиса.");
            return Channel("CRM", false, ResponseCrmDeliveryOutcomes.Duplicate, "Дубль в CRM-контуре офиса.");
        }

        var (cardId, error) = await crm.TryCreateCardForDeliveryAsync(entity, officeId, save: false, ct);
        if (error is not null || cardId is null)
        {
            StageCrm(entity.Id, officeId, null, ResponseCrmDeliveryOutcomes.Unavailable, source, error);
            return Channel("CRM", false, ResponseCrmDeliveryOutcomes.Unavailable, error ?? "CRM недоступна.");
        }

        StageCrm(entity.Id, officeId, cardId, ResponseCrmDeliveryOutcomes.Sent, source);
        return Channel("CRM", true, ResponseCrmDeliveryOutcomes.Sent, null, cardId);
    }

    private async Task<DeliverResponseChannelResultDto> DeliverBitrixAsync(
        CandidateResponseEntity entity,
        Guid officeId,
        Guid? bitrixInstanceId,
        bool useRoute,
        OfficeScope scope,
        string source,
        CancellationToken ct)
    {
        // Ensure entity has office for Bitrix services that still read OfficeId.
        entity.OfficeId ??= officeId;

        if (bitrixInstanceId is Guid instanceId && instanceId != Guid.Empty)
        {
            var manual = await manualBitrixSend.SendAsync(entity.Id, instanceId, scope, ct);
            return Channel(
                "Bitrix",
                manual.Success,
                manual.Success ? ResponseBitrixDeliveryOutcomes.Sent : (manual.Status ?? ResponseBitrixDeliveryOutcomes.Error),
                manual.ErrorMessage,
                bitrixInstanceId: manual.BitrixInstanceId,
                bitrixEntityId: manual.BitrixEntityId);
        }

        if (!useRoute)
        {
            return Channel("Bitrix", false, ResponseBitrixDeliveryOutcomes.Unavailable, "Не выбран Битрикс.");
        }

        var plan = await distributionEngine.GetPlanAsync(officeId, ct);
        var distributionResult = await autoDistribution.DistributeAsync(entity, plan, ct);
        ApplyBitrixDistributionResult(entity, distributionResult);
        var ok = distributionResult.Status == ResponseStatuses.Sent;
        return Channel(
            "Bitrix",
            ok,
            ok ? ResponseBitrixDeliveryOutcomes.Sent : distributionResult.Status,
            string.IsNullOrWhiteSpace(distributionResult.ErrorMessage) ? null : distributionResult.ErrorMessage,
            bitrixInstanceId: distributionResult.BitrixInstanceId,
            bitrixEntityId: distributionResult.BitrixEntityId);
    }

    private void StageCrm(
        Guid responseId,
        Guid officeId,
        Guid? cardId,
        string outcome,
        string source,
        string? errorMessage = null)
    {
        db.ResponseCrmDeliveries.Add(new ResponseCrmDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            OfficeId = officeId,
            CardId = cardId,
            Outcome = outcome,
            ErrorMessage = errorMessage ?? string.Empty,
            Source = source,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static void ApplyBitrixDistributionResult(CandidateResponseEntity entity, AutoDistributionResult result)
    {
        entity.BitrixInstanceId = result.BitrixInstanceId;
        entity.BitrixEntityId = result.BitrixEntityId ?? string.Empty;
        entity.BitrixContactId = result.BitrixContactId ?? string.Empty;
        entity.DistributionMode = string.IsNullOrWhiteSpace(result.DistributionMode)
            ? DistributionModes.Auto
            : result.DistributionMode;
        entity.IsBitrixDuplicate = result.IsBitrixDuplicate;
        entity.DuplicateBitrixInstanceId = result.DuplicateBitrixInstanceId;
        if (!string.IsNullOrWhiteSpace(result.DuplicateSummary))
        {
            entity.DuplicateSummary = result.DuplicateSummary;
        }
    }

    private static string ResolveStatus(
        IReadOnlyList<DeliverResponseChannelResultDto> channels,
        DeliverResponseRequest request)
    {
        if (channels.Count == 0)
        {
            return ResponseStatuses.ActionRequired;
        }

        var allOk = channels.All(x => x.Success);
        if (allOk)
        {
            return ResponseStatuses.Sent;
        }

        var anyOk = channels.Any(x => x.Success);
        if (anyOk)
        {
            // Partial: still "Sent" if CRM or Bitrix landed; details in ErrorMessage.
            return ResponseStatuses.Sent;
        }

        if (channels.Any(x => x.Outcome is ResponseStatuses.Duplicate or ResponseCrmDeliveryOutcomes.Duplicate
                or ResponseBitrixDeliveryOutcomes.Duplicate))
        {
            return ResponseStatuses.Duplicate;
        }

        if (channels.Any(x => x.Outcome is ResponseStatuses.Error or ResponseCrmDeliveryOutcomes.Error
                or ResponseBitrixDeliveryOutcomes.Error))
        {
            return ResponseStatuses.Error;
        }

        return ResponseStatuses.ActionRequired;
    }

    private static string? BuildSummary(IReadOnlyList<DeliverResponseChannelResultDto> channels)
    {
        var parts = channels
            .Select(x => x.Success
                ? $"{x.Channel}: ok"
                : $"{x.Channel}: {x.ErrorMessage ?? x.Outcome}")
            .ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static DeliverResponseChannelResultDto Channel(
        string channel,
        bool success,
        string outcome,
        string? error,
        Guid? cardId = null,
        Guid? bitrixInstanceId = null,
        string? bitrixEntityId = null) =>
        new(channel, success, outcome, error, cardId, bitrixInstanceId, bitrixEntityId);

    private static DeliverResponseResultDto Fail(string status, string message) =>
        new(false, status, message, null, []);
}
