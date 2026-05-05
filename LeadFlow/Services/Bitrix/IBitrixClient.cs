using LeadFlow.Models;

namespace LeadFlow.Services.Bitrix;

public interface IBitrixClient
{
    Task<BitrixDuplicateLookupResult> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<CandidateResponse>> GetExistingLeadsAsync(AppSettings settings, CancellationToken cancellationToken);
    Task<BitrixCreateLeadResponse> CreateLeadAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken);

    /// <summary>Создаёт только сделку для существующего контакта (повтор после сбоя или ручная отправка).</summary>
    Task<BitrixCreateLeadResponse> CreateDealForContactAsync(
        CandidateResponse response,
        string contactId,
        AppSettings settings,
        CancellationToken cancellationToken);
}
