namespace LeadFlow.Core.Services.Avito;

/// <summary>Страница Avito не соответствует ожидаемому шагу автоматизации.</summary>
public sealed class AvitoPageMismatchException : Exception
{
    public AvitoPageMismatchException(
        string expectedStep,
        AvitoPageKind expectedKind,
        AvitoPageState? actualState,
        IReadOnlyList<string>? recoveryAttempts = null,
        string? userMessage = null)
        : base(userMessage ?? BuildDefaultMessage(expectedStep, expectedKind, actualState, recoveryAttempts))
    {
        ExpectedStep = expectedStep;
        ExpectedKind = expectedKind;
        ActualState = actualState;
        RecoveryAttempts = recoveryAttempts ?? [];
        UserMessage = Message;
    }

    public string ExpectedStep { get; }

    public AvitoPageKind ExpectedKind { get; }

    public AvitoPageState? ActualState { get; }

    public IReadOnlyList<string> RecoveryAttempts { get; }

    public string UserMessage { get; }

    private static string BuildDefaultMessage(
        string expectedStep,
        AvitoPageKind expectedKind,
        AvitoPageState? actualState,
        IReadOnlyList<string>? recoveryAttempts)
    {
        var expectedLabel = expectedKind switch
        {
            AvitoPageKind.Candidates => "страница откликов",
            AvitoPageKind.ProfileSwitchModal => "модалка выбора субпрофиля",
            _ => expectedStep
        };

        var actualLabel = actualState?.DescribeForDiagnostics() ?? "состояние не определено";
        var attempts = recoveryAttempts is { Count: > 0 }
            ? $" Попытки: {string.Join("; ", recoveryAttempts)}."
            : string.Empty;

        return $"Ожидали: {expectedLabel}. Факт: {actualLabel}.{attempts}";
    }
}