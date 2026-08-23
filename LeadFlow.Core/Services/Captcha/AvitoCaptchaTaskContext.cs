namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// Счётчики капчи одного прохода аккаунта (thread-safe, AsyncLocal).
/// </summary>
public sealed class AvitoCaptchaPassCounters
{
    private int _seen;
    private int _solved;

    public int Seen => Volatile.Read(ref _seen);
    public int Solved => Volatile.Read(ref _solved);

    public void NoteSeen() => Interlocked.Increment(ref _seen);

    public void NoteSolved() => Interlocked.Increment(ref _solved);

    public (int Seen, int Solved) SnapshotAndReset()
    {
        var seen = Interlocked.Exchange(ref _seen, 0);
        var solved = Interlocked.Exchange(ref _solved, 0);
        return (seen, solved);
    }
}

/// <summary>
/// Async-local контекст текущего аккаунта: позволяет CDP-слою передать решателю
/// proxy-параметры профиля, не смешивая их между параллельными проходами воркера.
/// </summary>
public static class AvitoCaptchaTaskContext
{
    private static readonly AsyncLocal<GeeTestV4TaskOptions?> CurrentOptions = new();
    private static readonly AsyncLocal<AvitoCaptchaPassCounters?> CurrentCounters = new();

    public static GeeTestV4TaskOptions? Options => CurrentOptions.Value;

    public static AvitoCaptchaPassCounters? Counters => CurrentCounters.Value;

    public static IDisposable Use(
        GeeTestV4TaskOptions? options,
        AvitoCaptchaPassCounters? counters = null)
    {
        var previousOptions = CurrentOptions.Value;
        var previousCounters = CurrentCounters.Value;
        CurrentOptions.Value = options;
        if (counters is not null)
        {
            CurrentCounters.Value = counters;
        }

        return new Scope(() =>
        {
            CurrentOptions.Value = previousOptions;
            CurrentCounters.Value = previousCounters;
        });
    }

    public static void NoteSolved()
    {
        var counters = CurrentCounters.Value;
        if (counters is null)
        {
            return;
        }

        counters.NoteSeen();
        counters.NoteSolved();
    }

    public static void NoteUnsolved()
    {
        CurrentCounters.Value?.NoteSeen();
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
