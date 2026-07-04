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
        string? userId = null,
        Guid? officeId = null,
        Guid? workerId = null,
        int page = 1,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveOfficeBitrixAsync(
        Guid officeId,
        string webhookUrl,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> CreateUserAsync(
        string email,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateUserOfficeAsync(
        string userId,
        Guid? officeId,
        CancellationToken ct = default);

    Task<(bool Success, string? Error, Guid? OfficeId, string? RegistrationSecret)> CreateOfficeAsync(
        string name,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateOfficeAsync(
        Guid officeId,
        string name,
        bool isEnabled,
        bool bitrixTransmissionEnabled,
        CancellationToken ct = default);

    Task<(bool Success, string? Error, string? RegistrationSecret)> RotateOfficeRegistrationSecretAsync(
        Guid officeId,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeleteUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> ResetUserPasswordAsync(string userId, string password, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateUserRoleAsync(string userId, string role, CancellationToken ct = default);

    Task<(bool Success, string? Error)> LockUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UnlockUserAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> RevokeUserSessionsAsync(string userId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> RenameWorkerAsync(Guid workerId, string displayName, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SetWorkerEnabledAsync(Guid workerId, bool enabled, CancellationToken ct = default);

    Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(Guid workerId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UploadWorkerReleaseAsync(
        IFormFile packageFile,
        string? version,
        string? releaseNotes,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SetWorkerReleaseLatestAsync(string version, CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeleteWorkerReleaseAsync(string version, CancellationToken ct = default);

    Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);
}