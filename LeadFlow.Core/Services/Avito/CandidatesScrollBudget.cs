namespace LeadFlow.Core.Services.Avito;

/// <summary>Longer scans are reserved for contractual phone watches that may sit deep in Avito history.</summary>
internal static class CandidatesScrollBudget
{
    private const int DefaultRounds = 48;
    private const int OpenPhoneWatchRounds = 96;

    public static int Resolve(bool hasOpenPhoneWatches) =>
        hasOpenPhoneWatches ? OpenPhoneWatchRounds : DefaultRounds;
}
