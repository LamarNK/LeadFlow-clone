namespace LeadFlow.Core.Services.Avito;

using LeadFlow.Core.Services.Worker;

internal sealed class CandidatesIncrementalScrollState(IReadOnlyCollection<string> phoneWatchNameKeys)
{
    private readonly HashSet<string> remainingPhoneWatchNames = phoneWatchNameKeys
        .Where(static key => !string.IsNullOrWhiteSpace(key))
        .ToHashSet(StringComparer.Ordinal);

    public bool SeenUnknownCard { get; private set; }
    public int ConsecutiveKnownOnlyLoadRounds { get; private set; }
    public int ParsedItems { get; private set; }
    public bool HasRemainingPhoneWatch => remainingPhoneWatchNames.Count > 0;

    public void ObserveNames(IReadOnlyCollection<string> fullNames)
    {
        ParsedItems += fullNames.Count;
        foreach (var fullName in fullNames)
        {
            remainingPhoneWatchNames.Remove(ResponsePhoneWatchEvaluator.BuildFullNameKey(fullName));
        }
    }

    public bool ObserveKnownHistory(bool allKnown, bool allowEarlyStop)
    {
        if (allKnown)
        {
            ConsecutiveKnownOnlyLoadRounds++;
        }
        else
        {
            SeenUnknownCard = true;
            ConsecutiveKnownOnlyLoadRounds = 0;
        }

        return allowEarlyStop && CandidatesScrollStop.ShouldStopAfterKnownHistory(
            SeenUnknownCard,
            ConsecutiveKnownOnlyLoadRounds,
            HasRemainingPhoneWatch);
    }
}
