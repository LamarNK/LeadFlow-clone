using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaOutboundChatDispatch(OrbitaApiClient apiClient) : IOutboundChatDispatch
{
    public Task<IReadOnlyList<WorkerPendingChatMessageDto>> GetPendingAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        apiClient.GetPendingChatMessagesAsync(accountId, cancellationToken);

    public Task<bool> ClaimForDeliveryAsync(Guid messageId, CancellationToken cancellationToken) =>
        apiClient.ClaimOutboundChatAsync(messageId, cancellationToken);

    public async Task AckSentAsync(IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        if (!await apiClient.AckOutboundChatAsync(messageIds, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Orbita did not accept the outbound chat delivery acknowledgement.");
        }
    }
}
