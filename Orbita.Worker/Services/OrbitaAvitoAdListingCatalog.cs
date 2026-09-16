using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaAvitoAdListingCatalog(OrbitaApiClient api) : IAvitoAdListingCatalog
{
    public async Task<IReadOnlyList<AvitoAdListingRecord>> GetAccountListingsAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var dtos = await api.GetAvitoAdsAsync(accountId, cancellationToken).ConfigureAwait(false);
        return dtos.Select(dto => new AvitoAdListingRecord
        {
            Id = dto.Id,
            WorkerId = dto.WorkerId == Guid.Empty ? workerId : dto.WorkerId,
            AccountId = dto.AccountId,
            AvitoSubProfileId = dto.AvitoSubProfileId ?? string.Empty,
            AvitoItemId = dto.AvitoItemId,
            Title = dto.Title,
            Url = dto.Url,
            StatusText = dto.StatusText,
            PublishedAtUtc = dto.PublishedAtUtc,
            PublicationDateSource = dto.PublicationDateSource,
            AgeDays = dto.AgeDays,
            RemainingDays = dto.RemainingDays,
            ExpiresAtUtc = dto.ExpiresAtUtc,
            LastSeenAtUtc = dto.LastSeenAtUtc,
            DetailCheckedAtUtc = dto.DetailCheckedAtUtc,
            LastSuccessfulListCheckAtUtc = dto.LastSuccessfulListCheckAtUtc,
            IsActive = dto.IsActive,
            State = dto.State,
            LastParseError = dto.LastParseError,
            SourceTab = dto.SourceTab,
            ErrorReason = dto.ErrorReason,
            CanPublish = dto.CanPublish,
            ImageUrl = dto.ImageUrl,
            Salary = dto.Salary,
            City = dto.City,
            AddressText = dto.AddressText,
            DistrictText = dto.DistrictText,
            Views = dto.Views,
            Contacts = dto.Contacts,
            Favorites = dto.Favorites,
            CreatedAtUtc = dto.CreatedAtUtc,
            UpdatedAtUtc = dto.UpdatedAtUtc
        }).ToList();
    }

    public Task<IReadOnlyList<WorkerAvitoAdListScheduleDto>> GetAccountSchedulesAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken cancellationToken) =>
        api.GetAvitoAdSchedulesAsync(accountId, cancellationToken);

    public async Task SaveSubProfileSyncAsync(
        Guid workerId,
        Guid accountId,
        string avitoSubProfileId,
        IReadOnlyList<AvitoAdListingRecord> records,
        bool listComplete,
        DateTime capturedAtUtc,
        DateTime nextListCheckAtUtc,
        CancellationToken cancellationToken)
    {
        var scoped = records
            .Where(x => x.AccountId == accountId
                        && string.Equals(x.AvitoSubProfileId, avitoSubProfileId ?? string.Empty, StringComparison.Ordinal))
            .Select(x => new WorkerAvitoAdSyncItemDto(
                x.AvitoItemId,
                x.Title,
                x.Url,
                x.StatusText,
                x.PublishedAtUtc,
                x.PublicationDateSource,
                x.AgeDays,
                x.RemainingDays,
                x.ExpiresAtUtc,
                x.LastSeenAtUtc,
                x.DetailCheckedAtUtc,
                x.IsActive,
                x.State,
                x.LastParseError,
                x.SourceTab,
                x.ErrorReason,
                x.CanPublish,
                x.ImageUrl,
                x.Salary,
                x.City,
                x.AddressText,
                x.DistrictText,
                x.Views,
                x.Contacts,
                x.Favorites))
            .ToList();

        await api.SyncAvitoAdsAsync(
                new WorkerAvitoAdSyncRequest(
                    workerId,
                    accountId,
                    avitoSubProfileId ?? string.Empty,
                    listComplete,
                    capturedAtUtc,
                    scoped,
                    nextListCheckAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
