using Orbita.Contracts;
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
        string? userId = null,
        Guid? officeId = null,
        Guid? instanceId = null,
        Guid? workerId = null,
        int page = 1,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveOfficeBitrixAsync(
        Guid officeId,
        string webhookUrl,
        CancellationToken ct = default);

    Task<(bool Success, string? Error, Guid? InstanceId)> SaveBitrixInstanceAsync(
        SaveBitrixInstanceFormModel model,
        Guid officeId,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeleteBitrixInstanceAsync(Guid id, Guid officeId, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveBitrixWorkforceAsync(
        SaveBitrixWorkforceFormModel model,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> ConfigureBitrixWorkforceReceiverAsync(
        ConfigureBitrixWorkforceReceiverFormModel model,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveDistributionRouteAsync(
        bool isAutoDistributionEnabled,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        Guid officeId,
        IReadOnlyList<SaveBitrixLeadQuotaRequest>? bitrixLeadQuotas = null,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveBitrixTransmissionAsync(
        Guid officeId,
        bool transmissionEnabled,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> CreateUserAsync(
        string email,
        string fullName,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateUserFullNameAsync(
        string userId,
        string fullName,
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
        bool crmEnabled,
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
