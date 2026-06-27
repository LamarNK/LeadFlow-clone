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
        string? action,
        string? userId = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var activeTab = NormalizeTab(tab);
        page = Math.Max(1, page);
        var currentUserId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (previewOptions.Value.Enabled)
        {
            var previewUsers = DesignPreviewData.PanelUsers;
            var previewIntegrations = DesignPreviewData.BitrixIntegrations;
            return activeTab switch
            {
                "logs" => SettingsIndexBuilder.BuildLogsTab(
                    q,
                    level,
                    service,
                    date,
                    DesignPreviewData.BuildServiceLogsPage(q, level, service, date, page)),
                "profiles" => SettingsIndexBuilder.BuildProfilesTab(previewUsers, previewIntegrations, currentUserId),
                "workers" => SettingsIndexBuilder.BuildWorkersTab(
                    DesignPreviewData.AdminWorkers,
                    DesignPreviewData.WorkerRegistrationInfo),
                "audit" => SettingsIndexBuilder.BuildAuditTab(
                    q,
                    action,
                    date,
                    DesignPreviewData.BuildPanelAuditPage(q, action, date, page)),
                "integrations" => SettingsIndexBuilder.BuildIntegrationsTab(
                    previewIntegrations,
                    userId,
                    string.IsNullOrWhiteSpace(userId) ? null : DesignPreviewData.MyBitrixIntegration),
                _ => SettingsIndexBuilder.BuildUsersTab(
                    previewUsers,
                    previewIntegrations,
                    currentUserId ?? "preview-admin")
            };
        }

        var integrations = await api.GetAdminBitrixIntegrationsAsync(ct) ?? [];
        return activeTab switch
        {
            "logs" => await BuildLogsTabAsync(q, level, service, date, page, ct),
            "profiles" => SettingsIndexBuilder.BuildProfilesTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                integrations,
                currentUserId),
            "workers" => SettingsIndexBuilder.BuildWorkersTab(
                await api.GetAdminWorkersAsync(ct) ?? [],
                await api.GetWorkerRegistrationInfoAsync(ct)),
            "audit" => await BuildAuditTabAsync(q, action, date, page, ct),
            "integrations" => await BuildIntegrationsTabAsync(userId, integrations, ct),
            _ => SettingsIndexBuilder.BuildUsersTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                integrations,
                currentUserId)
        };
    }

    public async Task<(bool Success, string? Error)> SaveUserBitrixAsync(
        string userId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (integration, error) = await api.SaveAdminUserBitrixIntegrationAsync(userId, webhookUrl, ct);
        return integration is not null ? (true, null) : (false, error);
    }

    public Task<(bool Success, string? Error)> CreateUserAsync(
        string email,
        string password,
        string role,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.CreatePanelUserAsync(email, password, role, ct);

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
        int page,
        CancellationToken ct)
    {
        var pageDto = await api.GetServiceLogsAsync(
            q,
            level,
            service,
            date ?? DateTime.UtcNow.Date,
            page,
            SettingsIndexBuilder.LogsPageSize,
            ct) ?? new ServiceLogsPageDto([], 0, page, SettingsIndexBuilder.LogsPageSize);

        return SettingsIndexBuilder.BuildLogsTab(q, level, service, date, pageDto);
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

    private async Task<SettingsIndexViewModel> BuildIntegrationsTabAsync(
        string? userId,
        IReadOnlyList<BitrixIntegrationListItemDto> integrations,
        CancellationToken ct)
    {
        BitrixIntegrationDto? editIntegration = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            editIntegration = await api.GetAdminUserBitrixIntegrationAsync(userId, ct);
        }

        return SettingsIndexBuilder.BuildIntegrationsTab(integrations, userId, editIntegration);
    }

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "profiles" => "profiles",
            "workers" => "workers",
            "audit" => "audit",
            "logs" => "logs",
            "integrations" => "integrations",
            _ => "users"
        };
}