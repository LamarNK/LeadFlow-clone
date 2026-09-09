namespace LeadFlow.Core.Services.Avito;

/// <summary>Phone-watch cards are contractual work and must not compete with the random browsing budget.</summary>
internal static class CandidatePhoneRevealBudget
{
    public static int Resolve(int regularBudget, int openPhoneWatchCount) =>
        Math.Max(Math.Max(0, regularBudget), Math.Max(0, openPhoneWatchCount));
}
