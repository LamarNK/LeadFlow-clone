namespace LeadFlow.Core.Services.Avito;

public static class AvitoAdExpiryCalculator
{
    public static DateTime ComputeExpiresAtUtc(DateTime publishedAtUtc)
    {
        var local = AvitoAdBusinessTime.ToLocal(publishedAtUtc);
        var expiresLocal = local.AddMonths(1);
        return AvitoAdBusinessTime.ToUtc(DateTime.SpecifyKind(expiresLocal, DateTimeKind.Unspecified));
    }

    public static int CalendarAgeDays(DateTime publishedAtUtc, DateTime utcNow)
    {
        var publishedLocal = AvitoAdBusinessTime.ToLocal(publishedAtUtc).Date;
        var nowLocal = AvitoAdBusinessTime.ToLocal(utcNow).Date;
        return Math.Max(0, (int)(nowLocal - publishedLocal).TotalDays);
    }

    public static int RemainingControlDays(DateTime expiresAtUtc, DateTime utcNow)
    {
        var expiresLocal = AvitoAdBusinessTime.ToLocal(expiresAtUtc).Date;
        var nowLocal = AvitoAdBusinessTime.ToLocal(utcNow).Date;
        return (int)(expiresLocal - nowLocal).TotalDays;
    }
}
