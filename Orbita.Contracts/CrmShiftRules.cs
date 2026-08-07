namespace Orbita.Contracts;

/// <summary>
/// Причины окончания CRM-смены (история <c>CrmManagerShifts.EndReason</c>).
/// </summary>
public static class CrmShiftEndReasons
{
    /// <summary>Менеджер нажал «Стоп смены».</summary>
    public const string Manual = "manual";

    /// <summary>Автозакрытие: смена дольше <see cref="CrmShiftRules.MaxDuration"/>.</summary>
    public const string AutoMaxDuration = "auto_max_duration";

    /// <summary>Автозакрытие legacy-флага без времени старта.</summary>
    public const string LegacyCleanup = "legacy_cleanup";

    /// <summary>Новый старт, пока предыдущая открытая запись ещё не закрыта.</summary>
    public const string Superseded = "superseded";
}

/// <summary>
/// Правила CRM-смены менеджера: старт/стоп и автозакрытие «забытых» смен.
/// </summary>
/// <remarks>
/// Без верхней границы смена могла висеть днями/неделями, если менеджер
/// не нажал «Стоп», и лиды продолжали назначаться.
/// История смен (начало/конец/причина) пишется отдельно для аналитики.
/// </remarks>
public static class CrmShiftRules
{
    /// <summary>
    /// Максимальная длительность одной смены. Длиннее 14 ч — почти наверняка
    /// забытый «Стоп», а не реальная рабочая смена (в т.ч. с ночным перекрытием).
    /// </summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(14);

    /// <summary>
    /// Причина автозакрытия: нет старта → legacy, иначе превышена длительность.
    /// </summary>
    public static string ResolveAutoEndReason(DateTime? startedAtUtc) =>
        startedAtUtc is null ? CrmShiftEndReasons.LegacyCleanup : CrmShiftEndReasons.AutoMaxDuration;

    /// <summary>
    /// Смена считается активной для выдачи лидов и UI.
    /// Активный флаг без времени старта (legacy) — не на смене: такие записи
    /// нужно автозакрыть.
    /// </summary>
    public static bool IsEffectivelyOnShift(
        bool crmShiftActive,
        DateTime? startedAtUtc,
        DateTime utcNow)
    {
        if (!crmShiftActive || startedAtUtc is null)
        {
            return false;
        }

        var started = NormalizeUtc(startedAtUtc.Value);
        var now = NormalizeUtc(utcNow);
        if (started > now)
        {
            // Часы вперёд / рассинхрон — не считаем на смене.
            return false;
        }

        return now - started < MaxDuration;
    }

    /// <summary>
    /// Смену нужно снять автоматически: флаг включён, но срок вышел или нет старта.
    /// </summary>
    public static bool ShouldAutoStop(
        bool crmShiftActive,
        DateTime? startedAtUtc,
        DateTime utcNow)
    {
        if (!crmShiftActive)
        {
            return false;
        }

        return !IsEffectivelyOnShift(crmShiftActive, startedAtUtc, utcNow);
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
