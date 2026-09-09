namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Когда останавливать бесконечный скролл списка откликов.
/// После порции новых карточек дальше идёт только уже известная история — не вычитываем весь архив.
/// Работает и для «сначала новые», и для «сначала старые»: пока неизвестных не видели, скроллим дальше.
/// </summary>
internal static class CandidatesScrollStop
{
    public const int KnownOnlyLoadRoundsToStop = 2;

    public static bool ShouldStopAfterKnownHistory(
        bool seenUnknownCard,
        int consecutiveKnownOnlyLoadRounds,
        bool hasOpenPhoneWatches = false) =>
        !hasOpenPhoneWatches
        && seenUnknownCard
        && consecutiveKnownOnlyLoadRounds >= KnownOnlyLoadRoundsToStop;
}
