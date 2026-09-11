using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IResponsesService
{
    Task<ResponsesIndexViewModel> GetIndexAsync(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        string? vacancy,
        string? search,
        Guid? selectedId,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        IReadOnlyList<string>? bitrixDestinations = null);

    Task<(bool Success, string? Error)> ResendToBitrixAsync(Guid id, CancellationToken ct = default);

    Task<(bool Success, string? Error)> SendToBitrixAsync(
        Guid id,
        Guid bitrixInstanceId,
        CancellationToken ct = default);

    Task<(BulkSendBitrixResultDto? Result, string? Error)> BulkSendToBitrixAsync(
        IReadOnlyList<Guid> responseIds,
        Guid bitrixInstanceId,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DeliverAsync(
        Guid id,
        IReadOnlyList<Guid> officeIds,
        bool toCrm,
        bool toBitrix,
        IReadOnlyList<Guid> bitrixInstanceIds,
        CancellationToken ct = default);

    Task<(BulkDeliverResponsesResultDto? Result, string? Error)> DeliverBulkAsync(
        IReadOnlyList<Guid> responseIds,
        IReadOnlyList<Guid> officeIds,
        bool toCrm,
        bool toBitrix,
        IReadOnlyList<Guid> bitrixInstanceIds,
        CancellationToken ct = default);

    Task<ResponseDetailJsonViewModel?> GetDetailJsonAsync(Guid id, CancellationToken ct = default);

    Task<(bool Success, string? Error)> UpdateAsync(
        Guid id,
        string fullName,
        string phoneRaw,
        string city,
        int? age,
        string? gender,
        CancellationToken ct = default);

    Task<(Stream? Stream, string? ContentType)> GetAvatarAsync(Guid id, CancellationToken ct = default);

    /// <summary>Fresh office/Bitrix options for the deliver modal (not cached on the page).</summary>
    Task<ResponsesDeliverOptionsViewModel> GetDeliverOptionsAsync(CancellationToken ct = default);
}
