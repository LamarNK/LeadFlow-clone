namespace LeadFlow.Core.Services.Worker;

/// <summary>Итог публикации одной пачки откликов субпрофиля.</summary>
public sealed record CandidateBatchPublishResult(
    int ReadyCount,
    int PublishedCount,
    int DeferredByCycleLimit,
    int SkippedPersonDuplicates = 0,
    int CollectedCount = 0,
    int WatchRefreshedCount = 0,
    int PhoneChangedCount = 0,
    /// <summary>
    /// Карточки без раскрытого телефона (маска/лимит бюджета/неудачный клик):
    /// не публикуются и не являются дублями — остаются в очереди следующего прохода.
    /// </summary>
    int SkippedNoPhoneCount = 0)
{
    public static CandidateBatchPublishResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
