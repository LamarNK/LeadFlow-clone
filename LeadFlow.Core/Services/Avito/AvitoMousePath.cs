namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Геометрия «человеческой» траектории мыши: 2–5 промежуточных опорных точек с перпендикулярным
/// шумом (кривая вместо прямой Puppeteer-интерполяции). Чистая математика — тестируется без браузера.
/// </summary>
internal static class AvitoMousePath
{
    public readonly record struct PathPoint(decimal X, decimal Y);

    /// <summary>
    /// Путь от (x0,y0) до (x1,y1): промежуточные точки с поперечным отклонением до 30% длины
    /// и лёгким продольным разбросом. Последняя точка — точно цель.
    /// </summary>
    public static List<PathPoint> BuildPath(decimal x0, decimal y0, decimal x1, decimal y1, Random? random = null)
    {
        var rnd = random ?? Random.Shared;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var distance = (decimal)Math.Sqrt((double)(dx * dx + dy * dy));
        if (distance < 4)
        {
            return [new PathPoint(x1, y1)];
        }

        var waypointCount = distance < 200 ? rnd.Next(1, 3) : rnd.Next(2, 5);
        var result = new List<PathPoint>(waypointCount + 1);
        var unitX = dx / distance;
        var unitY = dy / distance;

        for (var i = 1; i <= waypointCount; i++)
        {
            // t растёт к концу: базовая равномерность + джиттер, без выхода за [0.05..0.85].
            var baseT = (decimal)i / (waypointCount + 1);
            var jitterT = (decimal)(rnd.NextDouble() * 0.12) - 0.06m;
            var t = Math.Clamp(baseT + jitterT, 0.05m, 0.85m);

            // Продольный шум ±12% длины; поперечный ±8..30% — траектория «изгибается».
            var alongNoise = (decimal)(rnd.NextDouble() * 0.24 - 0.12);
            var crossNoise = (decimal)(rnd.NextDouble() * 0.22 + 0.08) * (rnd.Next(2) == 0 ? -1 : 1);
            var px = x0 + dx * t + unitX * (distance * alongNoise) - unitY * (distance * crossNoise);
            var py = y0 + dy * t + unitY * (distance * alongNoise) + unitX * (distance * crossNoise);
            result.Add(new PathPoint(px, py));
        }

        result.Add(new PathPoint(x1, y1));
        return result;
    }

    /// <summary>
    /// Точка overshoot: цель, «пролетённая» на 5–20 px по ходу движения (коррекция — следующим жестом).
    /// </summary>
    public static PathPoint BuildOvershoot(decimal x0, decimal y0, decimal x1, decimal y1, Random? random = null)
    {
        var rnd = random ?? Random.Shared;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var distance = (decimal)Math.Sqrt((double)(dx * dx + dy * dy));
        if (distance < 1)
        {
            return new PathPoint(x1, y1);
        }

        var overshoot = (decimal)(rnd.NextDouble() * 15 + 5);
        return new PathPoint(x1 + dx / distance * overshoot, y1 + dy / distance * overshoot);
    }
}
