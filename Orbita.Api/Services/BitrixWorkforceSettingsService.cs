using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixWorkforceSettingsService(
    OrbitaDbContext db,
    IOptions<BitrixWorkforceOptions> options,
    IOptions<OrbitaBitrixSettings> defaultBitrixOptions,
    BitrixInstanceService bitrixInstances,
    IBitrixWorkforceClient bitrixClient)
{
    public async Task<BitrixWorkforceSettingsDto?> GetAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct = default)
    {
        var instance = await FindAccessibleInstanceAsync(bitrixInstanceId, scope, officeId, ct);
        if (instance is null)
        {
            return null;
        }

        return await BuildDtoAsync(instance.Id, ct);
    }

    public async Task<(BitrixWorkforceSettingsDto? Settings, string? Error)> SaveAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        UpdateBitrixWorkforceSettingsRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        var instance = await FindAccessibleInstanceAsync(bitrixInstanceId, scope, officeId, ct);
        if (instance is null)
        {
            return (null, "Битрикс не найден.");
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return (null, validationError);
        }

        var mode = NormalizeMode(request.OperationMode);
        await using var settingsGateTransaction = await BeginSettingsGateTransactionAsync(
            bitrixInstanceId,
            ct);
        if (!await LockAndReloadInstanceAsync(instance, ct))
        {
            return (null, "Битрикс не найден.");
        }

        var (resolvedOfficeId, _) = OfficeIdResolver.Resolve(scope, officeId);
        if (resolvedOfficeId != instance.OfficeId)
        {
            return (null, "Битрикс не найден.");
        }

        var configuration = await db.BitrixWorkforceConfigurations
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == bitrixInstanceId, ct);
        var existingManagers = await db.BitrixWorkforceManagers
            .Where(x => x.BitrixInstanceId == bitrixInstanceId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        var existingRules = await db.BitrixWorkforceStageRules
            .Where(x => x.BitrixInstanceId == bitrixInstanceId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        if (mode == BitrixWorkforceDistribution.WriterMode)
        {
            if (!instance.IsEnabled)
            {
                return (null, "Невозможно включить writer: подключение Bitrix24 отключено.");
            }

            if (configuration is null
                || configuration.OperationMode is not (
                    BitrixWorkforceDistribution.ShadowMode
                    or BitrixWorkforceDistribution.WriterMode)
                || !MatchesSavedConfiguration(
                    request,
                    configuration,
                    existingManagers,
                    existingRules))
            {
                return (
                    null,
                    "Перед writer сохраните новые параметры в shadow-режиме, проверьте журнал решений и затем включите writer без изменения правил.");
            }

            var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                return (null, "Невозможно включить writer: входящий REST-вебхук Bitrix24 не настроен.");
            }

            var canonicalPortalHost = BitrixWebhookValidator.TryGetPortalHost(webhookUrl);
            if (string.IsNullOrWhiteSpace(canonicalPortalHost))
            {
                return (
                    null,
                    "Невозможно включить writer: не удалось определить портал Bitrix24 из входящего REST-вебхука.");
            }

            if (settingsGateTransaction is not null)
            {
                await AcquireAdvisoryLockAsync(
                    $"writer|{canonicalPortalHost.ToLowerInvariant()}|{request.DealCategoryId}",
                    ct);
            }

            if (await HasCompetingWriterAsync(
                    bitrixInstanceId,
                    canonicalPortalHost,
                    request.DealCategoryId,
                    ct))
            {
                return (
                    null,
                    "Нельзя включить второй writer для того же портала и воронки: сначала отключите текущий writer.");
            }

            if (!string.Equals(
                    instance.PortalHost,
                    canonicalPortalHost,
                    StringComparison.OrdinalIgnoreCase))
            {
                instance.PortalHost = canonicalPortalHost;
            }

            try
            {
                var integrationSettings = BitrixInstanceIntegrationSettings.Parse(
                    instance.IntegrationSettingsJson,
                    defaultBitrixOptions.Value);
                if (EffectiveRules(request.StageRules).Any(x =>
                        x.IsEnabled
                        && string.Equals(
                            x.Scenario,
                            BitrixWorkforceDistribution.NewScenario,
                            StringComparison.Ordinal))
                    && integrationSettings.ResponsibleId <= 0)
                {
                    return (
                        null,
                        "Невозможно включить writer: укажите ответственного по умолчанию в настройках интеграции Bitrix24. Только этот пользователь считается безопасным исходным владельцем новой сделки.");
                }

                var requiredDealFieldCodes = new[]
                    {
                        integrationSettings.DealIdempotencyUfCode,
                        integrationSettings.DealAgeUfCode,
                        integrationSettings.DealProfessionUfCode,
                        integrationSettings.DealCityUfCode
                    }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await bitrixClient.ValidateDealFieldsAsync(
                    webhookUrl,
                    requiredDealFieldCodes,
                    ct);
                var enabledStageIds = EffectiveRules(request.StageRules)
                    .Where(x => x.IsEnabled)
                    .SelectMany(x => new[] { x.SourceStageId, x.TargetStageId })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await bitrixClient.ValidateDealPipelineAsync(
                    webhookUrl,
                    request.DealCategoryId,
                    enabledStageIds,
                    ct);

                var preflightManagerIds = request.ManagerUserIds
                    .Where(x => x > 0)
                    .Distinct()
                    .ToList();
                var managerStatuses = await bitrixClient.GetManagerStatusesAsync(
                    webhookUrl,
                    preflightManagerIds,
                    ct);
                var activeManagerIds = managerStatuses
                    .Where(x => x.IsActive)
                    .Select(x => x.BitrixUserId)
                    .ToHashSet();
                var missingOrInactiveManagerIds = preflightManagerIds
                    .Where(x => !activeManagerIds.Contains(x))
                    .ToList();
                if (missingOrInactiveManagerIds.Count > 0)
                {
                    return (
                        null,
                        "Невозможно включить writer: менеджеры Bitrix24 отсутствуют или неактивны: "
                        + string.Join(", ", missingOrInactiveManagerIds));
                }

                if (configuration.OperationMode == BitrixWorkforceDistribution.ShadowMode)
                {
                    var coverageError = await ValidateShadowCoverageAsync(
                        instance.Id,
                        webhookUrl,
                        configuration,
                        existingRules,
                        ct);
                    if (coverageError is not null)
                    {
                        return (null, coverageError);
                    }
                }
            }
            catch (Exception ex)
            {
                return (
                    null,
                    $"Невозможно включить writer: проверьте доступ к CRM, воронке, стадиям, user и timeman входящего вебхука. {ex.Message}");
            }
        }

        var now = DateTime.UtcNow;
        if (configuration is not null && now <= configuration.UpdatedAtUtc)
        {
            // UpdatedAtUtc is also the configuration revision used by in-flight
            // decisions and shadow coverage. Keep it strictly monotonic even when
            // two saves land within the database timestamp precision.
            now = configuration.UpdatedAtUtc.AddMilliseconds(1);
        }
        if (configuration is null)
        {
            configuration = new BitrixWorkforceConfigurationEntity
            {
                BitrixInstanceId = bitrixInstanceId
            };
            db.BitrixWorkforceConfigurations.Add(configuration);
        }

        configuration.OperationMode = mode;
        configuration.DealCategoryId = request.DealCategoryId;
        configuration.TimeZoneId = request.TimeZoneId.Trim();
        configuration.MorningWindowStartMinutes = request.MorningWindowStartMinutes;
        configuration.MorningWindowEndMinutes = request.MorningWindowEndMinutes;
        configuration.LateJoinReserveMinutes = request.LateJoinReserveMinutes;
        configuration.SingleManagerInitialReleasePercent = request.SingleManagerInitialReleasePercent;
        configuration.RetryDelaySeconds = request.RetryDelaySeconds;
        configuration.MaxAttempts = request.MaxAttempts;
        configuration.PreserveManualNewOwner = request.PreserveManualNewOwner;
        configuration.SyncContactOwner = request.SyncContactOwner;
        configuration.FillOnlyEmptyAvitoFields = request.FillOnlyEmptyAvitoFields;
        configuration.WriterRulesConfirmed =
            mode == BitrixWorkforceDistribution.WriterMode
            && request.WriterRulesConfirmed;
        configuration.UpdatedAtUtc = now;
        configuration.UpdatedByUserId = actorUserId;

        db.BitrixWorkforceManagers.RemoveRange(existingManagers);

        var managerIds = request.ManagerUserIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        for (var index = 0; index < managerIds.Count; index++)
        {
            db.BitrixWorkforceManagers.Add(new BitrixWorkforceManagerEntity
            {
                Id = Guid.NewGuid(),
                BitrixInstanceId = bitrixInstanceId,
                BitrixUserId = managerIds[index],
                IsEnabled = true,
                SortOrder = index
            });
        }

        db.BitrixWorkforceStageRules.RemoveRange(existingRules);

        foreach (var rule in EffectiveRules(request.StageRules).OrderBy(x => x.SortOrder))
        {
            db.BitrixWorkforceStageRules.Add(new BitrixWorkforceStageRuleEntity
            {
                Id = Guid.NewGuid(),
                BitrixInstanceId = bitrixInstanceId,
                Scenario = rule.Scenario.Trim(),
                SourceStageId = rule.SourceStageId.Trim(),
                TargetStageId = rule.TargetStageId.Trim(),
                UsesMorningWindow = rule.UsesMorningWindow,
                SortOrder = rule.SortOrder,
                IsEnabled = rule.IsEnabled
            });
        }

        await db.SaveChangesAsync(ct);
        if (settingsGateTransaction is not null)
        {
            await settingsGateTransaction.CommitAsync(ct);
        }

        return (await BuildDtoAsync(bitrixInstanceId, ct), null);
    }

    public async Task<(BitrixWorkforceReceiverDto? Receiver, string? Error)> ConfigureReceiverAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        ConfigureBitrixWorkforceReceiverRequest request,
        CancellationToken ct = default)
    {
        var instance = await FindAccessibleInstanceAsync(bitrixInstanceId, scope, officeId, ct);
        if (instance is null)
        {
            return (null, "Битрикс не найден.");
        }

        var token = request.ApplicationToken?.Trim() ?? string.Empty;
        if (token.Length < 16)
        {
            return (null, "Укажите application_token исходящего вебхука Bitrix24.");
        }

        var credential = await db.BitrixWorkforceEventCredentials
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == bitrixInstanceId, ct);
        if (credential is null)
        {
            credential = new BitrixWorkforceEventCredentialEntity
            {
                BitrixInstanceId = bitrixInstanceId,
                PublicId = Guid.NewGuid()
            };
            db.BitrixWorkforceEventCredentials.Add(credential);
        }

        credential.ApplicationTokenHash = HashToken(token);
        credential.ExpectedMemberId = NullIfWhiteSpace(request.ExpectedMemberId);
        credential.ConfiguredAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (MapReceiver(credential), null);
    }

    public async Task<IReadOnlyList<BitrixWorkforceAssignmentDto>?> GetRecentAssignmentsAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        int take,
        CancellationToken ct = default)
    {
        var instance = await FindAccessibleInstanceAsync(bitrixInstanceId, scope, officeId, ct);
        if (instance is null)
        {
            return null;
        }

        return await db.BitrixWorkforceAssignments
            .AsNoTracking()
            .Where(x => x.BitrixInstanceId == bitrixInstanceId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new BitrixWorkforceAssignmentDto(
                x.Id,
                x.BitrixInstanceId,
                x.DealId,
                x.ContactId,
                x.Scenario,
                x.OperationMode,
                x.FromStageId,
                x.ToStageId,
                x.PreviousResponsibleId,
                x.SelectedResponsibleId,
                x.Decision,
                x.Reason,
                x.CreatedAtUtc,
                x.DealAppliedAtUtc,
                x.ContactsAppliedAtUtc,
                x.AppliedAtUtc,
                x.Error))
            .ToListAsync(ct);
    }

    private async Task<string?> ValidateShadowCoverageAsync(
        Guid bitrixInstanceId,
        string webhookUrl,
        BitrixWorkforceConfigurationEntity configuration,
        IReadOnlyList<BitrixWorkforceStageRuleEntity> rules,
        CancellationToken ct)
    {
        var enabledRules = rules
            .Where(x => x.IsEnabled)
            .ToList();
        var sourceStageIds = enabledRules
            .Select(x => x.SourceStageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentDeals = (await bitrixClient.ListDealsAsync(
                webhookUrl,
                configuration.DealCategoryId,
                sourceStageIds,
                ct))
            .GroupBy(x => x.DealId)
            .Select(x => x.Last())
            .ToList();
        var statesByDealId = new Dictionary<long, BitrixWorkforceDealStateEntity>();
        foreach (var dealIds in currentDeals.Select(x => x.DealId).Distinct().Chunk(500))
        {
            var states = await db.BitrixWorkforceDealStates
                .AsNoTracking()
                .Where(x => x.BitrixInstanceId == bitrixInstanceId
                            && dealIds.Contains(x.DealId))
                .ToListAsync(ct);
            foreach (var state in states)
            {
                statesByDealId[state.DealId] = state;
            }
        }

        var uncoveredDealIds = new List<long>();
        foreach (var deal in currentDeals)
        {
            var rule = enabledRules.SingleOrDefault(x => string.Equals(
                x.SourceStageId,
                deal.StageId,
                StringComparison.OrdinalIgnoreCase));
            statesByDealId.TryGetValue(deal.DealId, out var state);
            var handledAt = state?.ActiveScenarioShadowHandledAtUtc;
            if (state?.ActiveScenarioWriterHandledAtUtc is DateTime writerHandledAt
                && (handledAt is null || writerHandledAt > handledAt))
            {
                handledAt = writerHandledAt;
            }

            var covered = rule is not null
                          && state is not null
                          && string.Equals(
                              state.LastObservedStageId,
                              deal.StageId,
                              StringComparison.OrdinalIgnoreCase)
                          && string.Equals(
                              state.ActiveScenario,
                              rule.Scenario,
                              StringComparison.Ordinal)
                          && handledAt is DateTime observedAt
                          && observedAt >= configuration.UpdatedAtUtc;
            if (!covered)
            {
                uncoveredDealIds.Add(deal.DealId);
            }
        }

        if (uncoveredDealIds.Count > 0)
        {
            const int sampleSize = 20;
            var sample = string.Join(", ", uncoveredDealIds.Take(sampleSize));
            var suffix = uncoveredDealIds.Count > sampleSize ? ", …" : string.Empty;
            return $"Нельзя включить writer: shadow ещё не обработал {uncoveredDealIds.Count} текущих сделок на исходных стадиях (ID: {sample}{suffix}). Дождитесь завершения shadow-reconcile и повторите проверку.";
        }

        var verificationDeals = (await bitrixClient.ListDealsAsync(
                webhookUrl,
                configuration.DealCategoryId,
                sourceStageIds,
                ct))
            .GroupBy(x => x.DealId)
            .Select(x => x.Last())
            .ToList();
        var firstSnapshot = currentDeals.ToDictionary(
            x => x.DealId,
            x => (Stage: x.StageId.Trim().ToUpperInvariant(), Revision: x.Revision.Trim()));
        var secondSnapshot = verificationDeals.ToDictionary(
            x => x.DealId,
            x => (Stage: x.StageId.Trim().ToUpperInvariant(), Revision: x.Revision.Trim()));
        if (firstSnapshot.Count != secondSnapshot.Count
            || firstSnapshot.Any(x => !secondSnapshot.TryGetValue(x.Key, out var revision)
                                      || revision != x.Value))
        {
            return "Нельзя включить writer: сделки изменились во время shadow-проверки. Дождитесь стабильного reconcile и повторите попытку.";
        }

        return null;
    }

    private async Task<bool> HasCompetingWriterAsync(
        Guid bitrixInstanceId,
        string portalHost,
        int dealCategoryId,
        CancellationToken ct)
    {
        var candidates = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .Join(
                db.BitrixInstances.AsNoTracking(),
                workforce => workforce.BitrixInstanceId,
                portal => portal.Id,
                (workforce, portal) => new { Workforce = workforce, Portal = portal })
            .Where(x => x.Portal.Id != bitrixInstanceId
                        && x.Portal.IsEnabled
                        && x.Workforce.OperationMode == BitrixWorkforceDistribution.WriterMode
                        && x.Workforce.DealCategoryId == dealCategoryId)
            .Select(x => x.Portal)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var candidateWebhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(candidate, ct);
            var candidateHost = BitrixWebhookValidator.TryGetPortalHost(candidateWebhookUrl)
                                ?? candidate.PortalHost?.Trim();
            if (string.Equals(candidateHost, portalHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IDbContextTransaction?> BeginSettingsGateTransactionAsync(
        Guid bitrixInstanceId,
        CancellationToken ct)
    {
        if (!string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return null;
        }

        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await AcquireAdvisoryLockAsync($"settings|{bitrixInstanceId:D}", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<bool> LockAndReloadInstanceAsync(
        BitrixInstanceEntity instance,
        CancellationToken ct)
    {
        if (string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            await db.BitrixInstances
                .FromSqlInterpolated(
                    $"SELECT * FROM \"BitrixInstances\" WHERE \"Id\" = {instance.Id} FOR UPDATE")
                .ToListAsync(ct);
        }

        await db.Entry(instance).ReloadAsync(ct);
        return db.Entry(instance).State != EntityState.Detached;
    }

    private Task<int> AcquireAdvisoryLockAsync(string canonical, CancellationToken ct)
    {
        var lockKey = BitConverter.ToInt64(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)),
            0);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            ct);
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    private async Task<BitrixWorkforceSettingsDto> BuildDtoAsync(
        Guid bitrixInstanceId,
        CancellationToken ct)
    {
        var configuration = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == bitrixInstanceId, ct);
        var managers = await db.BitrixWorkforceManagers
            .AsNoTracking()
            .Where(x => x.BitrixInstanceId == bitrixInstanceId && x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .Select(x => x.BitrixUserId)
            .ToListAsync(ct);
        var rules = await db.BitrixWorkforceStageRules
            .AsNoTracking()
            .Where(x => x.BitrixInstanceId == bitrixInstanceId)
            .OrderBy(x => x.SortOrder)
            .Select(x => new BitrixWorkforceStageRuleDto(
                x.Id,
                x.Scenario,
                x.SourceStageId,
                x.TargetStageId,
                x.UsesMorningWindow,
                x.SortOrder,
                x.IsEnabled))
            .ToListAsync(ct);
        var credential = await db.BitrixWorkforceEventCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == bitrixInstanceId, ct);

        return new BitrixWorkforceSettingsDto(
            bitrixInstanceId,
            configuration?.OperationMode ?? BitrixWorkforceDistribution.DisabledMode,
            configuration?.DealCategoryId ?? 0,
            configuration?.TimeZoneId ?? "Europe/Moscow",
            managers,
            rules,
            configuration?.MorningWindowStartMinutes ?? 480,
            configuration?.MorningWindowEndMinutes ?? 660,
            configuration?.LateJoinReserveMinutes ?? 120,
            configuration?.SingleManagerInitialReleasePercent ?? 50m,
            configuration?.RetryDelaySeconds ?? 60,
            configuration?.MaxAttempts ?? 20,
            configuration?.PreserveManualNewOwner ?? true,
            configuration?.SyncContactOwner ?? true,
            configuration?.FillOnlyEmptyAvitoFields ?? true,
            configuration?.WriterRulesConfirmed ?? false,
            credential is not null,
            credential is null ? null : BuildEndpoint(credential.PublicId),
            credential?.ExpectedMemberId,
            credential?.LastAcceptedAtUtc,
            configuration?.UpdatedAtUtc ?? DateTime.MinValue);
    }

    private static string? Validate(UpdateBitrixWorkforceSettingsRequest request)
    {
        var mode = NormalizeMode(request.OperationMode);
        if (mode.Length == 0)
        {
            return "Режим должен быть disabled, shadow или writer.";
        }

        if (request.DealCategoryId < 0)
        {
            return "ID воронки не может быть отрицательным.";
        }

        if (!BitrixWorkforceTimeZones.TryResolve(request.TimeZoneId, out _))
        {
            return "Указан неизвестный часовой пояс.";
        }

        if (request.MorningWindowStartMinutes is < 0 or >= 1440
            || request.MorningWindowEndMinutes is <= 0 or > 1440
            || request.MorningWindowStartMinutes >= request.MorningWindowEndMinutes)
        {
            return "Утреннее окно распределения задано неверно.";
        }

        if (request.LateJoinReserveMinutes is < 0 or > 1440)
        {
            return "Резерв позднего выхода должен быть от 0 до 1440 минут.";
        }

        if (request.SingleManagerInitialReleasePercent is < 0 or > 100)
        {
            return "Доля первичной выдачи должна быть от 0 до 100 процентов.";
        }

        if (request.RetryDelaySeconds is < 10 or > 3600 || request.MaxAttempts is < 1 or > 1000)
        {
            return "Параметры повторных попыток заданы неверно.";
        }

        if (request.ManagerUserIds.Any(x => x <= 0))
        {
            return "Bitrix ID менеджеров должны быть положительными числами.";
        }

        var rules = EffectiveRules(request.StageRules);
        var enabledRules = rules.Where(x => x.IsEnabled).ToList();

        if (enabledRules.Any(x => string.IsNullOrWhiteSpace(x.SourceStageId)
                                  || string.IsNullOrWhiteSpace(x.TargetStageId)
                                  || !IsKnownScenario(x.Scenario)))
        {
            return "Проверьте сценарии и ID стадий.";
        }

        if (rules
            .GroupBy(x => x.SourceStageId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(x => x.Count() > 1))
        {
            return "Одна стадия не может участвовать в нескольких правилах.";
        }

        var crossScenarioTargetCollision = enabledRules.Any(targetRule =>
            enabledRules.Any(sourceRule =>
                !string.Equals(
                    targetRule.Scenario.Trim(),
                    sourceRule.Scenario.Trim(),
                    StringComparison.Ordinal)
                && string.Equals(
                    targetRule.TargetStageId.Trim(),
                    sourceRule.SourceStageId.Trim(),
                    StringComparison.OrdinalIgnoreCase)));
        if (crossScenarioTargetCollision)
        {
            return "Target-стадия одного сценария не может быть Source-стадией другого сценария: это вызовет каскадное повторное распределение.";
        }

        if (mode == BitrixWorkforceDistribution.WriterMode)
        {
            if (!request.WriterRulesConfirmed)
            {
                return "Перед writer-режимом подтвердите бизнес-правила после проверки shadow-режима.";
            }

            if (!request.ManagerUserIds.Any() || enabledRules.Count == 0)
            {
                return "Для writer-режима нужны менеджеры и правила стадий.";
            }
        }

        return null;
    }

    private async Task<BitrixInstanceEntity?> FindAccessibleInstanceAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct)
    {
        var instance = await db.BitrixInstances
            .FirstOrDefaultAsync(x => x.Id == bitrixInstanceId, ct);
        if (instance is null)
        {
            return null;
        }

        var (resolvedOfficeId, _) = OfficeIdResolver.Resolve(scope, officeId);
        return resolvedOfficeId == instance.OfficeId ? instance : null;
    }

    private BitrixWorkforceReceiverDto MapReceiver(BitrixWorkforceEventCredentialEntity credential) =>
        new(
            true,
            BuildEndpoint(credential.PublicId),
            credential.ExpectedMemberId,
            credential.LastAcceptedAtUtc);

    private string BuildEndpoint(Guid publicId)
    {
        var relative = $"/api/v1/integrations/bitrix/events/{publicId:D}";
        var publicBaseUrl = options.Value.PublicBaseUrl?.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(publicBaseUrl) ? relative : publicBaseUrl + relative;
    }

    private static string NormalizeMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is BitrixWorkforceDistribution.DisabledMode
            or BitrixWorkforceDistribution.ShadowMode
            or BitrixWorkforceDistribution.WriterMode
            ? normalized
            : string.Empty;
    }

    private static bool IsKnownScenario(string? scenario) =>
        scenario?.Trim() is BitrixWorkforceDistribution.NewScenario
            or BitrixWorkforceDistribution.MissedCallScenario
            or BitrixWorkforceDistribution.SubstituteMissedCallScenario;

    private static IReadOnlyList<BitrixWorkforceStageRuleDto> EffectiveRules(
        IReadOnlyList<BitrixWorkforceStageRuleDto> rules) =>
        rules
            .Where(x => x.IsEnabled || !string.IsNullOrWhiteSpace(x.SourceStageId))
            .ToList();

    private static bool MatchesSavedConfiguration(
        UpdateBitrixWorkforceSettingsRequest request,
        BitrixWorkforceConfigurationEntity configuration,
        IReadOnlyList<BitrixWorkforceManagerEntity> managers,
        IReadOnlyList<BitrixWorkforceStageRuleEntity> rules)
    {
        if (configuration.DealCategoryId != request.DealCategoryId
            || !string.Equals(
                configuration.TimeZoneId.Trim(),
                request.TimeZoneId.Trim(),
                StringComparison.OrdinalIgnoreCase)
            || configuration.MorningWindowStartMinutes != request.MorningWindowStartMinutes
            || configuration.MorningWindowEndMinutes != request.MorningWindowEndMinutes
            || configuration.LateJoinReserveMinutes != request.LateJoinReserveMinutes
            || configuration.SingleManagerInitialReleasePercent
            != request.SingleManagerInitialReleasePercent
            || configuration.RetryDelaySeconds != request.RetryDelaySeconds
            || configuration.MaxAttempts != request.MaxAttempts
            || configuration.PreserveManualNewOwner != request.PreserveManualNewOwner
            || configuration.SyncContactOwner != request.SyncContactOwner
            || configuration.FillOnlyEmptyAvitoFields != request.FillOnlyEmptyAvitoFields)
        {
            return false;
        }

        var requestedManagers = request.ManagerUserIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        if (!managers.Select(x => x.BitrixUserId).SequenceEqual(requestedManagers))
        {
            return false;
        }

        var requestedRules = EffectiveRules(request.StageRules)
            .OrderBy(x => x.SortOrder)
            .ToList();
        if (rules.Count != requestedRules.Count)
        {
            return false;
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var saved = rules[index];
            var requested = requestedRules[index];
            if (!string.Equals(
                    saved.Scenario.Trim(),
                    requested.Scenario.Trim(),
                    StringComparison.Ordinal)
                || !string.Equals(
                    saved.SourceStageId.Trim(),
                    requested.SourceStageId.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    saved.TargetStageId.Trim(),
                    requested.TargetStageId.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                || saved.UsesMorningWindow != requested.UsesMorningWindow
                || saved.SortOrder != requested.SortOrder
                || saved.IsEnabled != requested.IsEnabled)
            {
                return false;
            }
        }

        return true;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class BitrixWorkforceTimeZones
{
    public static bool TryResolve(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (TryFind(id.Trim(), out timeZone))
        {
            return true;
        }

        var fallback = string.Equals(id.Trim(), "Europe/Moscow", StringComparison.OrdinalIgnoreCase)
            ? "Russian Standard Time"
            : string.Equals(id.Trim(), "Russian Standard Time", StringComparison.OrdinalIgnoreCase)
                ? "Europe/Moscow"
                : null;
        return fallback is not null && TryFind(fallback, out timeZone);
    }

    private static bool TryFind(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
