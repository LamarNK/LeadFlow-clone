namespace LeadFlow.Core.Services.Multilogin;

public sealed record MultiloginProfileSearchResult(
    IReadOnlyList<MultiloginProfileSummary> Profiles,
    bool IsComplete)
{
    public static MultiloginProfileSearchResult EmptyComplete { get; } = new([], true);
}
