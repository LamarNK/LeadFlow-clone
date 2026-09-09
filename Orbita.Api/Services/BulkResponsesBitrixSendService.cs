using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BulkResponsesBitrixSendService(
    ManualBitrixSendService manualSend,
    ResponseCacheInvalidator cacheInvalidator)
{
    public const int MaxBatchSize = 200;

    public async Task<(BulkSendBitrixResultDto? Result, string? Error)> SendAsync(
        IReadOnlyList<Guid> responseIds,
        Guid bitrixInstanceId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (responseIds.Count == 0)
        {
            return (null, "Выберите хотя бы один отклик.");
        }

        if (bitrixInstanceId == Guid.Empty)
        {
            return (null, "Выберите Битрикс для отправки.");
        }

        if (responseIds.Count > MaxBatchSize)
        {
            return (null, $"За один раз можно отправить не более {MaxBatchSize} откликов.");
        }

        var distinctIds = responseIds.Distinct().ToList();
        var items = new List<BulkSendBitrixItemResultDto>();
        var succeeded = 0;
        var failed = 0;

        await using var invalidationBatch = cacheInvalidator.BeginBatch();

        foreach (var responseId in distinctIds)
        {
            var sendResult = await manualSend.SendAsync(responseId, bitrixInstanceId, scope, ct);
            if (sendResult.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            items.Add(new BulkSendBitrixItemResultDto(
                responseId,
                sendResult.Success,
                sendResult.Status,
                sendResult.ErrorMessage));
        }

        await invalidationBatch.FlushAsync();

        return (new BulkSendBitrixResultDto(distinctIds.Count, succeeded, failed, items), null);
    }
}
