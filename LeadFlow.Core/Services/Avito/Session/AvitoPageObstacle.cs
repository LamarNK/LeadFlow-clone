namespace LeadFlow.Core.Services.Avito.Session;

/// <summary>
/// Тип препятствия на странице Avito, замеченного во время автоматизации.
/// Отделяет «что видно на странице» от стадии обработки в оркестраторе.
/// </summary>
public enum AvitoPageObstacleKind
{
    None,
    Captcha,
    IpBlocked,
    LoginRequired,
    TransientError,
    ManualActionRequired,
    Unknown
}

/// <summary>
/// Препятствие, обнаруженное единым детектором страницы: что именно мешает автоматизации,
/// где и по каким сигналам. Один источник правды для оркестратора сессии.
/// </summary>
public sealed record AvitoPageObstacle(
    AvitoPageObstacleKind Kind,
    string? CaptchaKind = null,
    string? Url = null,
    string? Title = null,
    IReadOnlyList<string>? Signals = null)
{
    public static AvitoPageObstacle None { get; } = new(AvitoPageObstacleKind.None);

    public static AvitoPageObstacle Unknown { get; } = new(AvitoPageObstacleKind.Unknown);
}

/// <summary>Результат работы обработчика препятствия. Успех обработчика — ещё не успех
/// восстановления: оркестратор обязан подтвердить исчезновение препятствия повторной проверкой.</summary>
public sealed record AvitoObstacleRecoveryResult(bool Recovered, string? Message = null)
{
    public static AvitoObstacleRecoveryResult Success(string? message = null) => new(true, message);

    public static AvitoObstacleRecoveryResult Failure(string? message = null) => new(false, message);
}
