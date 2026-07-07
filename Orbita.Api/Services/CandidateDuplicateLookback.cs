namespace Orbita.Api.Services;

/// <summary>
/// Окно для проверки дублей: учитываются только отклики не старше полугода.
/// </summary>
public static class CandidateDuplicateLookback
{
    public const int Months = 6;

    public static DateTime GetCutoffUtc(DateTime utcNow) => utcNow.AddMonths(-Months);
}