namespace LeadFlow.Core.Services.Worker;

public interface IResponsePhoneObservationStore
{
    /// <summary>Поиск только по субпрофиль + ФИО (без AccountId / SourceResponseId).</summary>
    Task<ResponsePhoneObservation?> GetAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(ResponsePhoneObservation observation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Открытое наблюдение: уже публиковали и окно ещё не закрыто — нельзя скипать phone-reveal.
    /// </summary>
    Task<bool> IsOpenWatchAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default);
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

    public Task<bool> IsOpenWatchAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public static class ResponsePhoneObservationWatch
{
    public static bool IsOpen(ResponsePhoneObservation? observation) =>
        observation is not null
        && !observation.ClosedAfterStableSend
        && !string.IsNullOrWhiteSpace(observation.PublishedSourceResponseId);
}
