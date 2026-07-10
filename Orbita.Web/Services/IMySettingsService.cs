using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IMySettingsService
{
    Task<MySettingsIndexViewModel> GetIndexAsync(string? tab, Guid? instanceId = null, CancellationToken ct = default);

    Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveBitrixTransmissionAsync(bool transmissionEnabled, CancellationToken ct = default);

    Task<(bool Success, string? Error, Guid? InstanceId)> SaveBitrixInstanceAsync(
        SaveBitrixInstanceFormModel model,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeleteBitrixInstanceAsync(Guid id, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SaveDistributionRouteAsync(
        bool isAutoDistributionEnabled,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        CancellationToken ct = default);
}