namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Бюджет перезапусков сценария после восстановления страницы исчерпан: страница
/// перезагружалась (капча → решение → reload → снова препятствие) слишком много раз за проход.
/// Раньше это маскировалось под CDP-таймаут и ретраилось через 1 минуту, усугубляя петлю.
/// Если рестарты вызывались капчей/блоком IP — это сигнал плохого качества IP: нужен кулдаун,
/// а не немедленный повтор.
/// </summary>
public sealed class AvitoRestartBudgetExhaustedException : InvalidOperationException
{
    public AvitoRestartBudgetExhaustedException(
        int restarts,
        string? lastRecoveredObstacleKind,
        long recoveryEpisodes,
        Exception? inner = null)
        : base(BuildMessage(restarts, lastRecoveredObstacleKind, recoveryEpisodes), inner)
    {
        Restarts = restarts;
        LastRecoveredObstacleKind = lastRecoveredObstacleKind;
        RecoveryEpisodes = recoveryEpisodes;
        UserMessage = Message;
    }

    /// <summary>Сколько раз сценарий перезапускался (потолок на проход/сценарий).</summary>
    public int Restarts { get; }

    /// <summary>Последнее препятствие, устранённое оркестратором сессии (Captcha, IpBlocked, …).</summary>
    public string? LastRecoveredObstacleKind { get; }

    /// <summary>Всего подтверждённых восстановлений страницы за жизнь сессии.</summary>
    public long RecoveryEpisodes { get; }

    public string UserMessage { get; }

    /// <summary>
    /// Петля «капча → решение → reload → капча»: повтор в ближайшую минуту только усилит
    /// подозрения Avito к IP — нужен длинный кулдаун.
    /// </summary>
    public bool LooksLikeCaptchaLoop =>
        string.Equals(LastRecoveredObstacleKind, "Captcha", StringComparison.OrdinalIgnoreCase)
        || string.Equals(LastRecoveredObstacleKind, "IpBlocked", StringComparison.OrdinalIgnoreCase);

    private static string BuildMessage(int restarts, string? lastRecoveredObstacleKind, long recoveryEpisodes)
    {
        var obstacle = string.IsNullOrWhiteSpace(lastRecoveredObstacleKind)
            ? "неизвестно"
            : lastRecoveredObstacleKind;
        return
            $"страница Avito перезагружалась после восстановления слишком часто: бюджет перезапусков " +
            $"({restarts}) исчерпан, последнее устранённое препятствие — {obstacle}, эпизодов восстановления " +
            $"за сессию: {recoveryEpisodes}. Нужна пауза, а не немедленный повтор.";
    }
}
