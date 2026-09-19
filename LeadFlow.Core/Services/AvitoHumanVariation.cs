namespace LeadFlow.Core.Services;

/// <summary>
/// Случайные решения, которые меняют маршрут прохода, а не только длину паузы.
/// <paramref name="draw"/> в тестах — фиксированный бросок 0…999.
/// </summary>
public static class AvitoHumanVariation
{
    public static bool RollPermille(int permille, int? draw = null)
    {
        var p = Math.Clamp(permille, 0, 1000);
        if (p <= 0)
        {
            return false;
        }

        if (p >= 1000)
        {
            return true;
        }

        var sample = draw ?? Random.Shared.Next(1000);
        return sample < p;
    }

    public static int NextInclusive(int min, int max)
    {
        var lo = Math.Min(min, max);
        var hi = Math.Max(min, max);
        return lo == hi ? lo : Random.Shared.Next(lo, hi + 1);
    }

    public static void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public static int NextAutoReplyBudget() =>
        NextInclusive(
            MonitoringTiming.MinMessengerAutoRepliesPerSubProfilePerCycle,
            MonitoringTiming.MaxMessengerAutoRepliesPerSubProfilePerCycle);
}
