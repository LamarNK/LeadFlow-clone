using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IMySettingsService
{
    Task<MySettingsIndexViewModel> GetIndexAsync(string? tab, CancellationToken ct = default);
    Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> SaveBitrixAsync(string webhookUrl, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveBitrixTransmissionAsync(bool transmissionEnabled, CancellationToken ct = default);
}