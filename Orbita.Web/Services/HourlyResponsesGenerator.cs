using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

/// <summary>
/// Суточный профиль откликов: ночь → утренний всплеск → спад → дневной пик → вечер → ночь → лёгкий подъём к 24:00.
/// </summary>
internal static class HourlyResponsesGenerator
{
    private static readonly int[] DailyProfile =
    [
        3, 2, 2, 3, 4, 5, 8, 18, 32, 48, 52, 42, 68, 88, 92, 78, 62, 48, 35, 25, 18, 12, 8, 6, 14
    ];

    public static IReadOnlyList<DashboardChartPointViewModel> BuildDailyPoints()
    {
        var points = new List<DashboardChartPointViewModel>(DailyProfile.Length);
        for (var i = 0; i < DailyProfile.Length; i++)
        {
            points.Add(new DashboardChartPointViewModel
            {
                Label = $"{i:00}:00",
                Value = DailyProfile[i],
                ShowAxisLabel = i % 4 == 0
            });
        }

        return points;
    }

    public static IReadOnlyList<DashboardChartPointViewModel> BuildEmptyDailyPoints()
    {
        return Enumerable.Range(0, 24)
            .Select(i => new DashboardChartPointViewModel
            {
                Label = $"{i:00}:00",
                Value = 0,
                ShowAxisLabel = i % 4 == 0
            })
            .ToList();
    }

    public static IReadOnlyList<int> DailyValues => DailyProfile;
}