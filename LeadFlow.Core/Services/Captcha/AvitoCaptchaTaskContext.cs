namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// Async-local контекст текущего аккаунта: позволяет CDP-слою передать решателю
/// proxy-параметры профиля, не смешивая их между параллельными проходами воркера.
/// </summary>
public static class AvitoCaptchaTaskContext
{
    private static readonly AsyncLocal<GeeTestV4TaskOptions?> CurrentOptions = new();

    public static GeeTestV4TaskOptions? Options => CurrentOptions.Value;

    public static IDisposable Use(GeeTestV4TaskOptions? options)
    {
        var previous = CurrentOptions.Value;
        CurrentOptions.Value = options;
        return new Scope(() => CurrentOptions.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}
