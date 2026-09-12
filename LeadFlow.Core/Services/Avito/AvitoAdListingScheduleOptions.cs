namespace LeadFlow.Core.Services.Avito;

public sealed class AvitoAdListingScheduleOptions
{
    public TimeSpan ListCheckInterval { get; init; } = TimeSpan.FromHours(12);
    public int MaxDetailPagesPerRun { get; init; } = 4;
    public int FreshAgeDays { get; init; } = 20;
    public int ApproachingDays { get; init; } = AvitoAdListingStateCalculator.ApproachingDays;
    public TimeSpan UnknownDateRecheckAfter { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan ApproachingRecheckAfter { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan StaleDetailRecheckAfter { get; init; } = TimeSpan.FromHours(72);
}
