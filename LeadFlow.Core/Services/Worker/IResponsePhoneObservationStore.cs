namespace LeadFlow.Core.Services.Worker;

public interface IResponsePhoneObservationStore
{
    /// <summary>Поиск только по субпрофиль + ФИО (без AccountId / SourceResponseId).</summary>
    Task<ResponsePhoneObservation?> GetAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(ResponsePhoneObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>Заглушка: watch-логика отключена (всегда «как новый»).</summary>
public sealed class NullResponsePhoneObservationStore : IResponsePhoneObservationStore
{
    public Task<ResponsePhoneObservation?> GetAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ResponsePhoneObservation?>(null);

    public Task UpsertAsync(ResponsePhoneObservation observation, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
