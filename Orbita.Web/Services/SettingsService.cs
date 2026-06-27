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
        int page = 1,
        CancellationToken ct = default)
    {
        var activeTab = NormalizeTab(tab);
        page = Math.Max(1, page);
        var currentUserId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (previewOptions.Value.Enabled)
        {
            var previewUsers = DesignPreviewData.PanelUsers;
            return activeTab switch
            {
                "logs" => SettingsIndexBuilder.BuildLogsTab(
                    q,
                    level,
                    service,
                    date,
                    DesignPreviewData.BuildServiceLogsPage(q, level, service, date, page)),
                "profiles" => SettingsIndexBuilder.BuildProfilesTab(previewUsers, currentUserId),
                "workers" => SettingsIndexBuilder.BuildWorkersTab(
                    DesignPreviewData.AdminWorkers,
                    DesignPreviewData.WorkerRegistrationInfo),
                "audit" => SettingsIndexBuilder.BuildAuditTab(
                    q,
                    action,
                    date,
                    DesignPreviewData.BuildPanelAuditPage(q, action, date, page)),
                "profile" => SettingsIndexBuilder.BuildProfileTab(
                    DesignPreviewData.PanelProfile,
                    DesignPreviewData.PasswordPolicy),
                _ => SettingsIndexBuilder.BuildUsersTab(previewUsers, currentUserId ?? "preview-admin")
            };
        }

        return activeTab switch
        {
            "logs" => await BuildLogsTabAsync(q, level, service, date, page, ct),
            "profiles" => SettingsIndexBuilder.BuildProfilesTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                currentUserId),
            "workers" => SettingsIndexBuilder.BuildWorkersTab(
                await api.GetAdminWorkersAsync(ct) ?? [],
                await api.GetWorkerRegistrationInfoAsync(ct)),
            "audit" => await BuildAuditTabAsync(q, action, date, page, ct),
            "profile" => await BuildProfileTabAsync(ct),
            _ => SettingsIndexBuilder.BuildUsersTab(
                await api.GetPanelUsersAsync(ct) ?? [],
                currentUserId)
        };
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

    private async Task<SettingsIndexViewModel> BuildProfileTabAsync(CancellationToken ct)
    {
        var profile = await api.GetPanelProfileAsync(ct)
                      ?? new PanelProfileDto("—", PanelRoles.Admin);
        var policy = await api.GetPasswordPolicyAsync(ct);
        return SettingsIndexBuilder.BuildProfileTab(profile, policy);
    }

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "profiles" => "profiles",
            "workers" => "workers",
            "audit" => "audit",
            "logs" => "logs",
            "profile" => "profile",
            _ => "users"
        };
}