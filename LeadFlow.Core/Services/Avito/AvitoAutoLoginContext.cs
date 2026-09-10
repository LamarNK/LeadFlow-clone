using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Async-local scope: credentials for the current account processing pipeline
/// (AdsPower automation → auto-login recovery).
/// </summary>
public static class AvitoAutoLoginContext
{
    private static readonly AsyncLocal<AvitoLoginCredentials?> Current = new();
    private static readonly AsyncLocal<Func<IPage, CancellationToken, Task<bool>>?> CurrentSolver = new();
    private static readonly AsyncLocal<AvitoAutoLoginAttempt?> CurrentAttempt = new();

    public static AvitoLoginCredentials? Credentials => Current.Value;

    public static Func<IPage, CancellationToken, Task<bool>>? CaptchaSolver => CurrentSolver.Value;

    public static void RecordAttemptResult(bool succeeded) => CurrentAttempt.Value?.Record(succeeded);

    public static IDisposable UseAttempt(AvitoAutoLoginAttempt attempt)
    {
        var previous = CurrentAttempt.Value;
        CurrentAttempt.Value = attempt;
        return new Scope(() => CurrentAttempt.Value = previous);
    }

    public static IDisposable Use(AvitoLoginCredentials? credentials)
    {
        var previous = Current.Value;
        Current.Value = credentials;
        return new Scope(() => Current.Value = previous);
    }

    public static IDisposable UseSolver(Func<IPage, CancellationToken, Task<bool>>? captchaSolver)
    {
        var previous = CurrentSolver.Value;
        CurrentSolver.Value = captchaSolver;
        return new Scope(() => CurrentSolver.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            restore();
        }
    }
}

/// <summary>Результат фактически запущенного автовхода для текущего прохода.</summary>
public sealed class AvitoAutoLoginAttempt
{
    public bool Attempted { get; private set; }
    public bool Succeeded { get; private set; }

    internal void Record(bool succeeded)
    {
        Attempted = true;
        Succeeded = succeeded;
    }

    public void Reset()
    {
        Attempted = false;
        Succeeded = false;
    }
}
