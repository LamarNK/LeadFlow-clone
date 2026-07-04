namespace LeadFlow.Core.Services.Worker;

/// <summary>Итог публикации одной пачки откликов субпрофиля.</summary>
public sealed record CandidateBatchPublishResult(
    int ReadyCount,
    int PublishedCount,
    int DeferredByCycleLimit)
{
    public static CandidateBatchPublishResult Empty { get; } = new(0, 0, 0);
}