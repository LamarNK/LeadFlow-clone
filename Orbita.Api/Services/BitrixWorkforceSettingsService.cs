using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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

            try
            {
                var integrationSettings = BitrixInstanceIntegrationSettings.Parse(
                    instance.IntegrationSettingsJson,
                    defaultBitrixOptions.Value);
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
            }
            catch (Exception ex)
            {
                return (
                    null,
                    $"Невозможно включить writer: проверьте доступ к CRM, воронке, стадиям, user и timeman входящего вебхука. {ex.Message}");
            }
        }

        var now = DateTime.UtcNow;
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
