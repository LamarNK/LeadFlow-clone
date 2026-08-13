using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

/// <summary>Очередь исходящих сообщений менеджера, которые воркер должен отправить в Avito.</summary>
public interface IOutboundChatDispatch
{
    Task<IReadOnlyList<WorkerPendingChatMessageDto>> GetPendingAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task<bool> ClaimForDeliveryAsync(Guid messageId, CancellationToken cancellationToken);

    Task AckSentAsync(IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken);
}

public sealed class NullOutboundChatDispatch : IOutboundChatDispatch
{
    public static readonly NullOutboundChatDispatch Instance = new();

    public Task<IReadOnlyList<WorkerPendingChatMessageDto>> GetPendingAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkerPendingChatMessageDto>>([]);

    public Task<bool> ClaimForDeliveryAsync(Guid messageId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task AckSentAsync(IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
