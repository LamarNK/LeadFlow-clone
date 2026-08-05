using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class SettingsService(
    OrbitaApiClient api,
    IHttpContextAccessor httpContextAccessor,
    IOptions<DesignPreviewOptions> previewOptions) : ISettingsService
{
    public async Task<SettingsIndexViewModel> GetIndexAsync(
        string? tab,
        string? q,
        string? level,
        string? service,
        DateTime? date,
        string? userId = null,
        Guid? officeId = null,
        Guid? instanceId = null,
        Guid? workerId = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var activeTab = NormalizeTab(tab);
        page = Math.Max(1, page);
        var currentUserId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (previewOptions.Value.Enabled)
        {
            var previewUsers = DesignPreviewData.PanelUsers;
            var previewOffices = DesignPreviewData.Offices;
            var previewModel = activeTab switch
            {
                "logs" => SettingsIndexBuilder.BuildLogsTab(
                    q,
                    level,
                    service,
                    date,
                    DesignPreviewData.BuildServiceLogsPage(q, level, service, date, page)),
                "offices" => SettingsIndexBuilder.BuildOfficesTab(
                    previewOffices,
                    DesignPreviewData.GetOfficeDetail(officeId)),
                "profiles" => SettingsIndexBuilder.BuildProfilesTab(
                    previewUsers,
                    previewOffices,
                    SettingsIndexBuilder.DefaultAccessProfiles,
                    currentUserId),
                "workers" => SettingsIndexBuilder.BuildWorkersTab(
                    DesignPreviewData.AdminWorkers,
                    previewOffices,
                    DesignPreviewData.WorkerRegistrationInfo),
                "worker-releases" => SettingsIndexBuilder.BuildWorkerReleasesTab(DesignPreviewData.WorkerReleases),
                _ => SettingsIndexBuilder.BuildUsersTab(
                    previewUsers,
                    previewOffices,
                    currentUserId ?? "preview-admin")
            };
            return previewModel with { Header = PageHeaderBuilder.SettingsAdmin() };
        }

        var offices = await api.GetOfficesAsync(ct) ?? [];
        var model = activeTab switch
        {
            "logs" => await BuildLogsTabAsync(q, level, service, date, workerId, page, ct),
            "offices" => await BuildOfficesTabAsync(officeId, ct),
            "profiles" => SettingsIndexBuilder.BuildProfilesTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                offices,
                await api.GetAccessProfilesAsync(ct) ?? SettingsIndexBuilder.DefaultAccessProfiles,
                currentUserId),
            "workers" => SettingsIndexBuilder.BuildWorkersTab(
                await api.GetAdminWorkersAsync(ct) ?? [],
                offices,
                await api.GetWorkerRegistrationInfoAsync(ct)),
            "worker-releases" => await BuildWorkerReleasesTabAsync(ct),
            _ => SettingsIndexBuilder.BuildUsersTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                offices,
                currentUserId)
        };
        return model with { Header = PageHeaderBuilder.SettingsAdmin() };
    }

    public async Task<(bool Success, string? Error)> SaveOfficeBitrixAsync(
        Guid officeId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (integration, error) = await api.SaveAdminOfficeBitrixIntegrationAsync(officeId, webhookUrl, ct);
        return integration is not null ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error, Guid? InstanceId)> SaveBitrixInstanceAsync(
        SaveBitrixInstanceFormModel model,
        Guid officeId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null, model.Id ?? DesignPreviewData.PreviewBitrixInstanceId);
        }

        var integrationSettings = MapIntegrationSettings(model);
        if (model.Id is Guid existingId && existingId != Guid.Empty)
        {
            var (instance, error) = await api.UpdateBitrixInstanceAsync(
                existingId,
                new UpdateBitrixInstanceRequest(
                    model.Name,
                    model.Signature,
                    string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim(),
                    integrationSettings,
                    model.IsEnabled),
                officeId,
                ct);
            return instance is null ? (false, error, null) : (true, null, instance.Id);
        }

        if (string.IsNullOrWhiteSpace(model.WebhookUrl))
        {
            return (false, "Укажите URL вебхука для нового Битрикса.", null);
        }

        var (created, createError) = await api.CreateBitrixInstanceAsync(
            new CreateBitrixInstanceRequest(
                model.Name,
                model.Signature,
                model.WebhookUrl.Trim(),
                integrationSettings,
                model.IsEnabled),
            officeId,
            ct);
        return created is null ? (false, createError, null) : (true, null, created.Id);
    }

    internal static BitrixInstanceIntegrationSettingsDto MapIntegrationSettings(
        SaveBitrixInstanceFormModel model) =>
        new(
            string.IsNullOrWhiteSpace(model.EntityType) ? "Deal" : model.EntityType.Trim(),
            model.ResponsibleId,
            model.LeadSource?.Trim() ?? string.Empty,
            model.DealIdempotencyUfCode?.Trim() ?? string.Empty,
            model.DealAgeUfCode?.Trim() ?? string.Empty,
            model.DealProfessionUfCode?.Trim() ?? string.Empty,
            model.DealCityUfCode?.Trim() ?? string.Empty,
            model.CheckDuplicatesInBitrix);

    public async Task<(bool Success, string? Error)> DeleteBitrixInstanceAsync(
        Guid id,
        Guid officeId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (success, error) = await api.DeleteBitrixInstanceAsync(id, officeId, ct);
        return success ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error)> SaveBitrixWorkforceAsync(
        SaveBitrixWorkforceFormModel model,
        CancellationToken ct = default)
    {
        if (model.OfficeId == Guid.Empty || model.BitrixInstanceId == Guid.Empty)
        {
            return (false, "Выберите офис и Битрикс.");
        }

        var (managerIds, managerError) = ParseManagerUserIds(model.ManagerUserIdsText);
        if (managerError is not null)
        {
            return (false, managerError);
        }

        var stageRules = (model.StageRules ?? [])
            .OrderBy(x => x.SortOrder)
            .Select(x => new BitrixWorkforceStageRuleDto(
                x.Id,
                x.Scenario?.Trim() ?? string.Empty,
                x.SourceStageId?.Trim() ?? string.Empty,
                x.TargetStageId?.Trim() ?? string.Empty,
                x.UsesMorningWindow,
                x.SortOrder,
                x.IsEnabled))
            .ToList();

        var request = new UpdateBitrixWorkforceSettingsRequest(
            model.OperationMode?.Trim() ?? BitrixWorkforceDistribution.DisabledMode,
            model.DealCategoryId,
            model.TimeZoneId?.Trim() ?? "Europe/Moscow",
            managerIds,
            stageRules,
            model.MorningWindowStartMinutes,
            model.MorningWindowEndMinutes,
            model.LateJoinReserveMinutes,
            model.SingleManagerInitialReleasePercent,
            model.RetryDelaySeconds,
            model.MaxAttempts,
            model.PreserveManualNewOwner,
            model.SyncContactOwner,
            model.FillOnlyEmptyAvitoFields,
            model.WriterRulesConfirmed);

        var (saved, error) = await api.UpdateBitrixWorkforceSettingsAsync(
            model.BitrixInstanceId,
            request,
            model.OfficeId,
            ct);
        return saved is null ? (false, error) : (true, null);
    }

    public async Task<(bool Success, string? Error)> ConfigureBitrixWorkforceReceiverAsync(
        ConfigureBitrixWorkforceReceiverFormModel model,
        CancellationToken ct = default)
    {
        if (model.OfficeId == Guid.Empty || model.BitrixInstanceId == Guid.Empty)
        {
            return (false, "Выберите офис и Битрикс.");
        }

        if (string.IsNullOrWhiteSpace(model.ApplicationToken))
        {
            return (false, "Укажите application_token исходящего вебхука Bitrix24.");
        }

        var (receiver, error) = await api.ConfigureBitrixWorkforceReceiverAsync(
            model.BitrixInstanceId,
            new ConfigureBitrixWorkforceReceiverRequest(
                model.ApplicationToken.Trim(),
                string.IsNullOrWhiteSpace(model.ExpectedMemberId)
                    ? null
                    : model.ExpectedMemberId.Trim()),
            model.OfficeId,
            ct);
        return receiver is null ? (false, error) : (true, null);
    }

    public async Task<(bool Success, string? Error)> SaveDistributionRouteAsync(
        bool isAutoDistributionEnabled,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        Guid officeId,
        IReadOnlyList<SaveBitrixLeadQuotaRequest>? bitrixLeadQuotas = null,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (route, error) = await api.SaveDistributionRouteAsync(
            new SaveDistributionRouteRequest(isAutoDistributionEnabled, nodes, bitrixLeadQuotas),
            officeId,
            ct);
        return route is not null ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error)> SaveBitrixTransmissionAsync(
        Guid officeId,
        bool transmissionEnabled,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var office = await api.GetOfficeAsync(officeId, ct);
        if (office is null)
        {
            return (false, "Офис не найден.");
        }

        // Preserve CRM acceptance flag when toggling Bitrix transmission.
        var (updated, error) = await api.UpdateOfficeAsync(
            officeId,
            office.Name,
            office.IsEnabled,
            transmissionEnabled,
            office.CrmEnabled,
            ct);
        return updated is not null ? (true, null) : (false, error);
    }

    public Task<(bool Success, string? Error)> CreateUserAsync(
        string email,
        string fullName,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.CreatePanelUserAsync(email, fullName, password, role, officeId, ct);

    public Task<(bool Success, string? Error)> UpdateUserFullNameAsync(
        string userId,
        string fullName,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.UpdatePanelUserFullNameAsync(userId, fullName, ct);

    public Task<(bool Success, string? Error)> UpdateAccessProfileAsync(
        string profileId,
        IReadOnlyList<string> permissions,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.UpdateAccessProfileAsync(profileId, permissions, ct);

    public Task<(bool Success, string? Error)> UpdateUserOfficeAsync(
        string userId,
        Guid? officeId,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.UpdatePanelUserOfficeAsync(userId, officeId, ct);

    public async Task<(bool Success, string? Error, Guid? OfficeId, string? RegistrationSecret)> CreateOfficeAsync(
        string name,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null, DesignPreviewData.PreviewOfficeId, "preview-office-secret");
        }

        var (office, error) = await api.CreateOfficeAsync(name, ct);
        return office is null ? (false, error, null, null) : (true, null, office.Id, null);
    }

    public async Task<(bool Success, string? Error)> UpdateOfficeAsync(
        Guid officeId,
        string name,
        bool isEnabled,
        bool crmEnabled,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        // Preserve Bitrix transmission flag when editing core office settings.
        var current = await api.GetOfficeAsync(officeId, ct);
        var bitrixTransmission = current?.BitrixTransmissionEnabled ?? true;
        var (office, error) = await api.UpdateOfficeAsync(
            officeId,
            name,
            isEnabled,
            bitrixTransmission,
            crmEnabled,
            ct);
        return office is not null ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error, string? RegistrationSecret)> RotateOfficeRegistrationSecretAsync(
        Guid officeId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null, "preview-rotated-secret");
        }

        var (result, error) = await api.RotateOfficeRegistrationSecretAsync(officeId, ct);
        return result is null ? (false, error, null) : (true, null, result.RegistrationSecret);
    }

    public Task<(bool Success, string? Error)> DeleteUserAsync(string userId, CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.DeletePanelUserAsync(userId, ct);

    public Task<(bool Success, string? Error)> ResetUserPasswordAsync(
        string userId,
        string password,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.ResetPanelUserPasswordAsync(userId, password, ct);

    public Task<(bool Success, string? Error)> UpdateUserRoleAsync(
        string userId,
        string role,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.UpdatePanelUserRoleAsync(userId, role, ct);

    public Task<(bool Success, string? Error)> LockUserAsync(string userId, CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.LockPanelUserAsync(userId, ct);

    public Task<(bool Success, string? Error)> UnlockUserAsync(string userId, CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.UnlockPanelUserAsync(userId, ct);

    public Task<(bool Success, string? Error)> RevokeUserSessionsAsync(string userId, CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.RevokePanelUserSessionsAsync(userId, ct);

    public Task<(bool Success, string? Error)> RenameWorkerAsync(
        Guid workerId,
        string displayName,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.RenameAdminWorkerAsync(workerId, displayName, ct);

    public Task<(bool Success, string? Error)> SetWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.SetAdminWorkerEnabledAsync(workerId, enabled, ct);

    public async Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return ("preview-api-key-demo", null);
        }

        var (result, error) = await api.RotateWorkerApiKeyAsync(workerId, ct);
        return result is null ? (null, error) : (result.ApiKey, null);
    }

    public async Task<(bool Success, string? Error)> UploadWorkerReleaseAsync(
        IFormFile packageFile,
        string? version,
        string? releaseNotes,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"orbita-web-upload-{Guid.NewGuid():N}.msi");
        try
        {
            await using (var output = File.Create(tempPath))
            {
                await packageFile.CopyToAsync(output, ct);
            }

            await using var uploadStream = File.OpenRead(tempPath);
            var (release, error) = await api.UploadWorkerReleaseAsync(
                uploadStream,
                packageFile.Length,
                packageFile.FileName,
                version,
                releaseNotes,
                ct);
            return release is null ? (false, error) : (true, null);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public Task<(bool Success, string? Error)> SetWorkerReleaseLatestAsync(
        string version,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.SetWorkerReleaseLatestAsync(version, ct);

    public Task<(bool Success, string? Error)> DeleteWorkerReleaseAsync(
        string version,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.DeleteWorkerReleaseAsync(version, ct);

    public Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.ChangeOwnPasswordAsync(currentPassword, newPassword, ct);

    private async Task<SettingsIndexViewModel> BuildLogsTabAsync(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        Guid? workerId,
        int page,
        CancellationToken ct)
    {
        var workers = await api.GetAdminWorkersAsync(ct) ?? [];
        var workerOptions = SettingsIndexBuilder.BuildWorkerOptions(workers, workerId);

        if (workerId.HasValue)
        {
            var workerPage = await api.GetWorkerLogsAsync(
                workerId.Value,
                q,
                level,
                date ?? DateTime.UtcNow.Date,
                page,
                SettingsIndexBuilder.LogsPageSize,
                ct) ?? new WorkerLogsPageDto([], 0, page, SettingsIndexBuilder.LogsPageSize);

            return SettingsIndexBuilder.BuildWorkerLogsTab(
                q,
                level,
                date,
                workerPage,
                workerId.Value,
                workerOptions);
        }

        var pageDto = await api.GetServiceLogsAsync(
            q,
            level,
            service,
            date ?? DateTime.UtcNow.Date,
            page,
            SettingsIndexBuilder.LogsPageSize,
            ct) ?? new ServiceLogsPageDto([], 0, page, SettingsIndexBuilder.LogsPageSize);

        return SettingsIndexBuilder.BuildLogsTab(q, level, service, date, pageDto, workerOptions: workerOptions);
    }

    private async Task<SettingsIndexViewModel> BuildWorkerReleasesTabAsync(CancellationToken ct)
    {
        var releases = await api.GetWorkerReleasesAsync(ct)
            ?? new WorkerReleaseListResponse(null, []);
        return SettingsIndexBuilder.BuildWorkerReleasesTab(releases);
    }

    private async Task<SettingsIndexViewModel> BuildOfficesTabAsync(
        Guid? officeId,
        CancellationToken ct)
    {
        var offices = await api.GetOfficesAsync(ct) ?? [];
        OfficeDetailDto? selected = null;
        if (officeId is Guid parsedOfficeId)
        {
            selected = await api.GetOfficeAsync(parsedOfficeId, ct);
        }

        return SettingsIndexBuilder.BuildOfficesTab(offices, selected);
    }

    private static (IReadOnlyList<long> ManagerIds, string? Error) ParseManagerUserIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ([], null);
        }

        var tokens = value.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<long>(tokens.Length);
        var seen = new HashSet<long>();
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, out var id) || id <= 0)
            {
                return ([], $"Некорректный Bitrix ID менеджера: «{token}».");
            }

            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return (result, null);
    }

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "offices" => "offices",
            "profiles" => "profiles",
            "workers" => "workers",
            "worker-releases" => "worker-releases",
            "logs" => "logs",
            _ => "users"
        };
}
