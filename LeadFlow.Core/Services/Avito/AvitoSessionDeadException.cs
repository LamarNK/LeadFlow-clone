namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// CDP-сессия браузера не отвечает: несколько подряд шагов/проб упали CDP-таймаутом.
/// Продолжать проход по оставшимся субпрофилям бессмысленно — каждый шаг будет терять
/// по 30 с. Проход прерывается сразу; браузер закрывается, аккаунт повторяется позже
/// (транзиентный RetryAfter + backoff).
/// </summary>
public sealed class AvitoSessionDeadException : TimeoutException
{
    public int ConsecutiveFailures { get; }

    public AvitoSessionDeadException(int consecutiveFailures)
        : base(
            $"сессия браузера не отвечает: {consecutiveFailures} CDP-шагов/проб подряд упали таймаутом — " +
            "проход прерван досрочно, браузер будет закрыт и аккаунт повторится позже.")
    {
        ConsecutiveFailures = consecutiveFailures;
    }
}
