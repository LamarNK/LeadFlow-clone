namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Async-local scope: credentials for the current account processing pipeline
/// (AdsPower automation → auto-login recovery).
/// </summary>
public static class AvitoAutoLoginContext
{
    private static readonly AsyncLocal<AvitoLoginCredentials?> Current = new();

    public static AvitoLoginCredentials? Credentials => Current.Value;

    public static IDisposable Use(AvitoLoginCredentials? credentials)
    {
        var previous = Current.Value;
        Current.Value = credentials;
        return new Scope(() => Current.Value = previous);
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
