namespace Orbita.Web.Services;

internal static class DashboardRecentEvents
{
    public const int LookbackDays = 3;
    public const int Limit = 5;

    public static DateTime SinceUtc =>
        DateTime.SpecifyKind(DateTime.Today.AddDays(-(LookbackDays - 1)), DateTimeKind.Local)
            .ToUniversalTime();
}