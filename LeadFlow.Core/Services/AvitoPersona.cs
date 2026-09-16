namespace LeadFlow.Core.Services;

/// <summary>
/// Стабильная на аккаунт «персона»: множитель темпов 0.8–1.3 (сид от идентификатора аккаунта),
/// чтобы распределения таймингов разных аккаунтов не совпадали точка-в-точку (анти-кластеризация).
/// Распространяется через <see cref="AsyncLocal{T}"/> — <see cref="HumanDelay"/> применяет его без смены сигнатур.
/// </summary>
public static class AvitoPersona
{
    private static readonly AsyncLocal<PersonaScope?> CurrentScope = new();

    /// <summary>Множитель в [0.8..1.3] для внутристранничных пауз/жестов текущего аккаунта.</summary>
    public static double TimingFactor => CurrentScope.Value?.Factor ?? 1.0;

    /// <summary>Стабильный множитель из идентификатора (FNV-1a → [0.8..1.3]).</summary>
    public static double ResolveFactor(string accountId)
    {
        if (string.IsNullOrEmpty(accountId))
        {
            return 1.0;
        }

        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in accountId)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            // Равномерно в [0.0..1.0), затем в [0.8..1.3].
            var unit = hash / (double)uint.MaxValue;
            return 0.8 + unit * 0.5;
        }
    }

    /// <summary>Установить персону на время обработки аккаунта: <c>using var _ = AvitoPersona.Begin(accountId);</c></summary>
    public static IDisposable Begin(string accountId)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new PersonaScope(ResolveFactor(accountId));
        return new RestoreScope(previous, CurrentScope);
    }

    private sealed record PersonaScope(double Factor);

    private sealed class RestoreScope(PersonaScope? Previous, AsyncLocal<PersonaScope?> local) : IDisposable
    {
        public void Dispose() => local.Value = Previous;
    }
}
