namespace Orbita.Api.Services;

/// <summary>
/// Окна для проверки дублей.
/// Полное ФИО / телефоны / source id: полугода.
/// Неполное имя (только имя или Ф+И): узкое окно по дате отклика (~неделя).
/// </summary>
public static class CandidateDuplicateLookback
{
    public const int Months = 6;

    /// <summary>Окно для неполного имени: ±N дней вокруг даты отклика (из чата).</summary>
    public const int IncompleteNameDays = 7;

    public static DateTime GetCutoffUtc(DateTime utcNow) => utcNow.AddMonths(-Months);

    public static DateTime ResolveAnchorUtc(DateTime? responseAtUtc, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (responseAtUtc is null || responseAtUtc.Value == default)
        {
            return now;
        }

        var value = responseAtUtc.Value;
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Окно [anchor − IncompleteNameDays, anchor + IncompleteNameDays] для слабого матча по имени.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) GetIncompleteNameWindow(
        DateTime? responseAtUtc,
        DateTime? utcNow = null)
    {
        var anchor = ResolveAnchorUtc(responseAtUtc, utcNow);
        return (
            anchor.AddDays(-IncompleteNameDays),
            anchor.AddDays(IncompleteNameDays));
    }
}
