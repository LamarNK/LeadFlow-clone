namespace Orbita.Web.Services;

internal enum SparklineTrend
{
    Up,
    Down,
    UpGentle
}

/// <summary>
/// Декоративные sparkline: 18–24 спокойных точек, плавный тренд.
/// </summary>
internal static class SparklineGenerator
{
    public const int PointCount = 20;

    public static IReadOnlyList<int> Create(int seed, SparklineTrend trend = SparklineTrend.Up)
    {
        var rng = new Random(seed);
        var count = PointCount;
        var raw = new double[count];
        raw[0] = 40 + rng.NextDouble() * 3;

        for (var i = 1; i < count; i++)
        {
            var drift = trend switch
            {
                SparklineTrend.Down => -(0.35 + rng.NextDouble() * 0.35),
                SparklineTrend.UpGentle => 0.25 + rng.NextDouble() * 0.35,
                _ => 0.5 + rng.NextDouble() * 0.45
            };
            var wobble = (rng.NextDouble() - 0.5) * 2.2;
            raw[i] = raw[i - 1] + drift + wobble;
        }

        var smooth = MovingAverage(raw, 3);
        var result = NormalizeCalm(smooth, 34, 56);

        if (trend == SparklineTrend.Down)
        {
            if (result[^1] >= result[0])
                result[^1] = Math.Max(30, result[0] - 3 - rng.Next(0, 3));
        }
        else if (result[^1] <= result[0])
        {
            result[^1] = result[0] + 3 + rng.Next(0, 4);
        }

        return result;
    }

    public static IReadOnlyList<int> FromHourlySeries(IReadOnlyList<int> source, SparklineTrend trend = SparklineTrend.Up)
    {
        if (source.Count == 0 || source.All(v => v == 0))
            return Flat(PointCount);

        if (source.Count < 4)
            return Create(42, trend);

        var count = PointCount;
        var resampled = new double[count];
        for (var i = 0; i < count; i++)
        {
            var pos = i * (source.Count - 1) / (double)(count - 1);
            var idx = (int)pos;
            var frac = pos - idx;
            var a = source[Math.Min(idx, source.Count - 1)];
            var b = source[Math.Min(idx + 1, source.Count - 1)];
            resampled[i] = a + (b - a) * frac;
        }

        var smooth = MovingAverage(resampled, 4);
        var result = NormalizeCalm(smooth, 32, 54);

        if (trend == SparklineTrend.Down)
        {
            if (result[^1] >= result[0])
                Array.Reverse(result);
        }
        else if (result[^1] <= result[0])
        {
            for (var i = 0; i < count; i++)
            {
                var lift = (double)i / (count - 1) * 5;
                result[i] = (int)Math.Round(result[i] + lift);
            }
        }

        return NormalizeCalm(result.Select(v => (double)v).ToArray(), 32, 54);
    }

    private static IReadOnlyList<int> Flat(int count) => Enumerable.Repeat(0, count).ToList();

    private static double[] MovingAverage(double[] values, int window)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var from = Math.Max(0, i - window + 1);
            var slice = values[from..(i + 1)];
            result[i] = slice.Average();
        }

        return result;
    }

    private static int[] NormalizeCalm(double[] values, int minOut, int maxOut)
    {
        var min = values.Min();
        var max = values.Max();
        var span = Math.Max(1, max - min);
        var result = new int[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var n = (values[i] - min) / span;
            result[i] = (int)Math.Round(minOut + n * (maxOut - minOut));
        }

        return result;
    }
}