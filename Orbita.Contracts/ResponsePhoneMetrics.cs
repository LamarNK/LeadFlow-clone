namespace Orbita.Contracts;

/// <summary>
/// Метрики смены/стабильности временного номера Avito.
/// Идентичность кандидата — субпрофиль + ФИО, не SourceResponseId.
/// </summary>
public static class ResponsePhoneMetricKinds
{
    /// <summary>Первый сбор / без особой метрики номера.</summary>
    public const string None = "";

    /// <summary>Номер на карточке сменился с прошлого прохода.</summary>
    public const string PhoneChanged = "PhoneChanged";

    /// <summary>Номер не менялся дольше порога из настроек воркера.</summary>
    public const string PhoneUnchanged = "PhoneUnchanged";

    public static bool IsKnown(string? kind) =>
        string.IsNullOrWhiteSpace(kind)
        || string.Equals(kind, PhoneChanged, StringComparison.Ordinal)
        || string.Equals(kind, PhoneUnchanged, StringComparison.Ordinal);

    public static string Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return None;
        }

        var trimmed = kind.Trim();
        if (string.Equals(trimmed, PhoneChanged, StringComparison.OrdinalIgnoreCase))
        {
            return PhoneChanged;
        }

        if (string.Equals(trimmed, PhoneUnchanged, StringComparison.OrdinalIgnoreCase))
        {
            return PhoneUnchanged;
        }

        return None;
    }

    public static string FormatLabel(string? kind, int? unchangedHours = null, string? previousPhone = null)
    {
        var normalized = Normalize(kind);
        return normalized switch
        {
            PhoneChanged => string.IsNullOrWhiteSpace(previousPhone)
                ? "номер изменился"
                : $"номер изменился (был {previousPhone.Trim()})",
            PhoneUnchanged => unchangedHours is > 0
                ? $"номер не менялся {unchangedHours.Value} ч"
                : "номер не менялся",
            _ => string.Empty
        };
    }
}

/// <summary>
/// Правила окна наблюдения за номером после первой отправки в Орбиту (настройка воркера).
/// Поле Worker.PhoneUnchangedHours хранит часы окна (историческое имя колонки).
/// </summary>
public static class ResponsePhoneWatchRules
{
    /// <summary>Default: 5 суток наблюдения после первой отправки.</summary>
    public const int DefaultUnchangedHours = 120;
    public const int MinUnchangedHours = 1;
    public const int MaxUnchangedHours = 720;

    /// <summary>
    /// null — настройка не задана (берём default при применении).
    /// 0 — не наблюдать после первой отправки (сразу закрыть окно).
    /// </summary>
    public static int? ClampUnchangedHours(int? hours)
    {
        if (hours is null)
        {
            return null;
        }

        if (hours.Value <= 0)
        {
            return 0;
        }

        return Math.Clamp(hours.Value, MinUnchangedHours, MaxUnchangedHours);
    }

    public static int ResolveUnchangedHours(int? hours)
    {
        var clamped = ClampUnchangedHours(hours);
        return clamped switch
        {
            null => DefaultUnchangedHours,
            0 => 0,
            int value => value
        };
    }
}
