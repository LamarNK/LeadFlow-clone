using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class SettingsService(
    OrbitaApiClient api,
    IHttpContextAccessor httpContextAccessor,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : ISettingsService
{
    public async Task<SettingsIndexViewModel> GetIndexAsync(
        string? tab,
        string? q,
        string? level,
        string? service,
        DateTime? date,
        string? action,
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
                "profiles" => SettingsIndexBuilder.BuildProfilesTab(previewUsers, previewOffices, currentUserId),
                "workers" => SettingsIndexBuilder.BuildWorkersTab(
                    DesignPreviewData.AdminWorkers,
                    previewOffices,
                    DesignPreviewData.WorkerRegistrationInfo),
                "audit" => SettingsIndexBuilder.BuildAuditTab(
                    q,
                    action,
                    date,
                    DesignPreviewData.BuildPanelAuditPage(q, action, date, page)),
                "integrations" => SettingsIndexBuilder.BuildIntegrationsTab(
                    previewOffices,
                    BuildPreviewBitrixDistribution(previewOffices, officeId, instanceId)),
                "leadflow-import" => SettingsIndexBuilder.BuildLeadFlowImportTab(previewOffices),
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
                currentUserId),
            "workers" => SettingsIndexBuilder.BuildWorkersTab(
                await api.GetAdminWorkersAsync(ct) ?? [],
                offices,
                await api.GetWorkerRegistrationInfoAsync(ct)),
            "audit" => await BuildAuditTabAsync(q, action, date, page, ct),
            "integrations" => await BuildIntegrationsTabAsync(officeId, instanceId, offices, ct),
            "leadflow-import" => SettingsIndexBuilder.BuildLeadFlowImportTab(offices),
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

        if (model.Id is Guid existingId && existingId != Guid.Empty)
        {
            var (instance, error) = await api.UpdateBitrixInstanceAsync(
                existingId,
                new UpdateBitrixInstanceRequest(
                    model.Name,
                    model.Signature,
                    string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim(),
                    null,
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
                null,
                model.IsEnabled),
            officeId,
            ct);
        return created is null ? (false, createError, null) : (true, null, created.Id);
    }

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

    public async Task<(bool Success, string? Error)> SaveDistributionRouteAsync(
        bool isAutoDistributionEnabled,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        Guid officeId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (route, error) = await api.SaveDistributionRouteAsync(
            new SaveDistributionRouteRequest(isAutoDistributionEnabled, nodes),
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

        return await UpdateOfficeAsync(officeId, office.Name, office.IsEnabled, transmissionEnabled, ct);
    }

    public Task<(bool Success, string? Error)> CreateUserAsync(
        string email,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.CreatePanelUserAsync(email, password, role, officeId, ct);

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
        bool bitrixTransmissionEnabled,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (office, error) = await api.UpdateOfficeAsync(officeId, name, isEnabled, bitrixTransmissionEnabled, ct);
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

    private async Task<SettingsIndexViewModel> BuildAuditTabAsync(
        string? q,
        string? action,
        DateTime? date,
        int page,
        CancellationToken ct)
    {
        var pageDto = await api.GetPanelAuditAsync(
            q,
            action,
            date ?? DateTime.UtcNow.Date,
            page,
            SettingsIndexBuilder.AuditPageSize,
            ct) ?? new PanelAuditPageDto([], 0, page, SettingsIndexBuilder.AuditPageSize);

        return SettingsIndexBuilder.BuildAuditTab(q, action, date, pageDto);
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

    private async Task<SettingsIndexViewModel> BuildIntegrationsTabAsync(
        Guid? officeId,
        Guid? instanceId,
        IReadOnlyList<OfficeDto> offices,
        CancellationToken ct)
    {
        var selectedOfficeId = officeId ?? officeContext.EffectiveOfficeId ?? offices.FirstOrDefault()?.Id;
        var bitrixDistribution = await BuildBitrixDistributionAsync(selectedOfficeId, offices, instanceId, ct);
        return SettingsIndexBuilder.BuildIntegrationsTab(offices, bitrixDistribution);
    }

    private static BitrixDistributionSettingsViewModel BuildPreviewBitrixDistribution(
        IReadOnlyList<OfficeDto> offices,
        Guid? officeId,
        Guid? instanceId)
    {
        var selectedOfficeId = officeId ?? offices.FirstOrDefault()?.Id;
        var selectedOffice = offices.FirstOrDefault(x => x.Id == selectedOfficeId);
        BitrixInstanceEditorViewModel? editor = null;
        if (instanceId == Guid.Empty)
        {
            editor = MySettingsService.CreateNewEditor();
        }
        else if (instanceId is Guid selectedInstanceId)
        {
            editor = DesignPreviewData.GetPreviewBitrixInstance(selectedInstanceId) is { } detail
                ? MySettingsService.MapEditor(detail)
                : null;
        }

        return new BitrixDistributionSettingsViewModel
        {
            SelectedOfficeId = selectedOfficeId,
            SelectedOfficeName = selectedOffice?.Name,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            BitrixInstances = new BitrixInstancesRegistryViewModel
            {
                OfficeId = selectedOfficeId,
                OfficeName = selectedOffice?.Name,
                CanManage = selectedOfficeId.HasValue,
                CanManageTransmission = false,
                Instances = DesignPreviewData.PreviewBitrixInstances
                    .Select(MySettingsService.MapListItem)
                    .ToList(),
                Editor = editor
            },
            Distribution = new DistributionEditorViewModel
            {
                OfficeId = selectedOfficeId,
                OfficeName = selectedOffice?.Name,
                CanManage = selectedOfficeId.HasValue,
                IsAutoDistributionEnabled = DesignPreviewData.PreviewDistributionRoute.IsAutoDistributionEnabled,
                RouteJson = System.Text.Json.JsonSerializer.Serialize(DesignPreviewData.PreviewDistributionRoute),
                InstancesJson = System.Text.Json.JsonSerializer.Serialize(
                    DesignPreviewData.PreviewBitrixInstances.Select(x => new
                    {
                        x.Id,
                        x.Name,
                        x.Signature,
                        Label = MySettingsService.FormatBitrixLabel(x.Name, x.Signature)
                    }))
            }
        };
    }

    private async Task<BitrixDistributionSettingsViewModel> BuildBitrixDistributionAsync(
        Guid? officeId,
        IReadOnlyList<OfficeDto> offices,
        Guid? instanceId,
        CancellationToken ct)
    {
        var officeOptions = offices.Select(o => new EventFilterOptionViewModel
        {
            Value = o.Id.ToString(),
            Label = o.Name
        }).ToList();

        if (officeId is not Guid selectedOfficeId)
        {
            return new BitrixDistributionSettingsViewModel { OfficeOptions = officeOptions };
        }

        var selectedOffice = offices.FirstOrDefault(x => x.Id == selectedOfficeId);
        var instances = await api.GetBitrixInstancesAsync(selectedOfficeId, ct) ?? [];
        var route = await api.GetDistributionRouteAsync(selectedOfficeId, ct)
            ?? new DistributionRouteDto(Guid.Empty, selectedOfficeId, false, [], null);

        BitrixInstanceEditorViewModel? editor = null;
        if (instanceId == Guid.Empty)
        {
            editor = MySettingsService.CreateNewEditor();
        }
        else if (instanceId is Guid selectedInstanceId)
        {
            var detail = await api.GetBitrixInstanceAsync(selectedInstanceId, selectedOfficeId, ct);
            if (detail is not null)
            {
                editor = MySettingsService.MapEditor(detail);
            }
        }

        var instancePayload = instances
            .Where(x => x.IsEnabled)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Signature,
                Label = MySettingsService.FormatBitrixLabel(x.Name, x.Signature)
            })
            .ToList();

        return new BitrixDistributionSettingsViewModel
        {
            SelectedOfficeId = selectedOfficeId,
            SelectedOfficeName = selectedOffice?.Name,
            OfficeOptions = officeOptions,
            BitrixInstances = new BitrixInstancesRegistryViewModel
            {
                OfficeId = selectedOfficeId,
                OfficeName = selectedOffice?.Name,
                CanManage = true,
                CanManageTransmission = true,
                TransmissionEnabled = route.IsAutoDistributionEnabled,
                Instances = instances.Select(MySettingsService.MapListItem).ToList(),
                Editor = editor
            },
            Distribution = new DistributionEditorViewModel
            {
                OfficeId = selectedOfficeId,
                OfficeName = selectedOffice?.Name,
                CanManage = true,
                IsAutoDistributionEnabled = route.IsAutoDistributionEnabled,
                RouteJson = System.Text.Json.JsonSerializer.Serialize(route),
                InstancesJson = System.Text.Json.JsonSerializer.Serialize(instancePayload)
            }
        };
    }

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "offices" => "offices",
            "profiles" => "profiles",
            "workers" => "workers",
            "leadflow-import" => "leadflow-import",
            "worker-releases" => "worker-releases",
            "audit" => "audit",
            "logs" => "logs",
            "integrations" => "integrations",
            _ => "users"
        };
}