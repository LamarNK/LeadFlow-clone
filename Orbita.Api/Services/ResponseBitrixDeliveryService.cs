using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponseBitrixDeliveryService(OrbitaDbContext db)
{
    public void Stage(
        Guid responseId,
        BitrixInstanceEntity instance,
        string outcome,
        string source,
        string? bitrixEntityId = null,
        string? bitrixEntityType = null,
        string? bitrixContactId = null,
        string? errorMessage = null)
    {
        db.ResponseBitrixDeliveries.Add(new ResponseBitrixDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            BitrixInstanceId = instance.Id,
            Outcome = outcome,
            BitrixEntityId = bitrixEntityId ?? string.Empty,
            BitrixEntityType = bitrixEntityType ?? string.Empty,
            BitrixContactId = bitrixContactId ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            Source = source,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public async Task<IReadOnlyDictionary<Guid, List<ResponseBitrixDeliveryDto>>> LoadByResponseIdsAsync(
        IReadOnlyList<Guid> responseIds,
        CancellationToken ct = default)
    {
        if (responseIds.Count == 0)
        {
            return new Dictionary<Guid, List<ResponseBitrixDeliveryDto>>();
        }

        var rows = await db.ResponseBitrixDeliveries
            .AsNoTracking()
            .Where(x => responseIds.Contains(x.ResponseId))
            .Include(x => x.BitrixInstance)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.ResponseId,
                x.BitrixInstanceId,
                BitrixName = x.BitrixInstance.Name,
                BitrixSignature = x.BitrixInstance.Signature,
                BitrixPortalHost = x.BitrixInstance.PortalHost,
                x.Outcome,
                x.BitrixEntityId,
                x.BitrixEntityType,
                x.ErrorMessage,
                x.Source,
                x.CreatedAtUtc
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.ResponseId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x =>
                {
                    var entityUrl = BitrixPortalLinks.TryBuildEntityDetailsUrl(
                        x.BitrixPortalHost,
                        string.IsNullOrWhiteSpace(x.BitrixEntityType) ? null : x.BitrixEntityType,
                        string.IsNullOrWhiteSpace(x.BitrixEntityId) ? null : x.BitrixEntityId);
                    return new ResponseBitrixDeliveryDto(
                        x.Id,
                        x.BitrixInstanceId,
                        FormatBitrixLabel(x.BitrixName, x.BitrixSignature),
                        x.Outcome,
                        string.IsNullOrWhiteSpace(x.BitrixEntityId) ? null : x.BitrixEntityId,
                        string.IsNullOrWhiteSpace(x.BitrixEntityType) ? null : x.BitrixEntityType,
                        entityUrl,
                        string.IsNullOrWhiteSpace(x.ErrorMessage) ? null : x.ErrorMessage,
                        x.Source,
                        x.CreatedAtUtc);
                }).ToList());
    }

    public static string FormatBitrixLabel(string? name, string? signature) =>
        !string.IsNullOrWhiteSpace(signature) ? signature
        : !string.IsNullOrWhiteSpace(name) ? name
        : "Битрикс";

    public static string FormatOutcomeLabel(string outcome) => outcome switch
    {
        ResponseBitrixDeliveryOutcomes.Sent => "Отправлен",
        ResponseBitrixDeliveryOutcomes.Duplicate => "Дубль",
        ResponseBitrixDeliveryOutcomes.Error => "Ошибка",
        ResponseBitrixDeliveryOutcomes.Unavailable => "Недоступен",
        _ => outcome
    };
}