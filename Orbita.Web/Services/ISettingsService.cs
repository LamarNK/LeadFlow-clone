using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface ISettingsService
{
    Task<SettingsIndexViewModel> GetIndexAsync(
        string? tab,
        string? q,
        string? level,
        string? service,
        DateTime? date,
        string? action,
        int page = 1,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> CreateUserAsync(string email, string password, string role, CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeleteUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> ResetUserPasswordAsync(string userId, string password, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateUserRoleAsync(string userId, string role, CancellationToken ct = default);

    Task<(bool Success, string? Error)> LockUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UnlockUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> RevokeUserSessionsAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> RenameWorkerAsync(Guid workerId, string displayName, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SetWorkerEnabledAsync(Guid workerId, bool enabled, CancellationToken ct = default);

    Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(Guid workerId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);
}