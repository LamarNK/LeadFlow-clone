namespace Orbita.Web.Services;

/// <summary>
/// Sparkline-данные для KPI-карточек: ресэмплинг реальной серии без декоративной нормализации.
/// </summary>
internal static class SparklineGenerator
{
    public const int PointCount = 20;

    public static IReadOnlyList<int> FromSeries(IReadOnlyList<int> source)
    {
        if (source.Count == 0)
            return Flat(PointCount);

        if (source.Count == 1)
            return Enumerable.Repeat(source[0], PointCount).ToList();

        var result = new int[PointCount];
        for (var i = 0; i < PointCount; i++)
        {
            var pos = i * (source.Count - 1) / (double)(PointCount - 1);
            var idx = (int)Math.Round(pos);
            result[i] = source[Math.Min(idx, source.Count - 1)];
        }

        return result;
    }

    private static IReadOnlyList<int> Flat(int count) => Enumerable.Repeat(0, count).ToList();
}