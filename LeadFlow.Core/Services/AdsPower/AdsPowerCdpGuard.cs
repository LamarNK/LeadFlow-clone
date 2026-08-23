namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Клиентский таймаут вокруг CDP-вызовов PuppeteerSharp.
/// Встроенный Timeout команды часто живёт внутри Runtime.evaluate / Target.getTargets
/// и не срабатывает, если Chrome ещё рисует вкладку, а сессия уже не отвечает.
/// </summary>
internal static class AdsPowerCdpGuard
{
    public const string TimeoutPrefix = "AdsPower CDP:";

    public static bool IsCdpTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException timeout
                && timeout.Message.StartsWith(TimeoutPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static TimeoutException? FindCdpTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException timeout
                && timeout.Message.StartsWith(TimeoutPrefix, StringComparison.Ordinal))
            {
                return timeout;
            }
        }

        return null;
    }

    public static TimeoutException Timeout(string operation, TimeSpan timeout, Exception? inner = null) =>
        new($"{TimeoutPrefix} {operation} не ответила за {FormatSeconds(timeout)} с.", inner);

    public static async Task WaitAsync(
        Task task,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw Timeout(operation, timeout, ex);
        }
    }

    public static async Task<T> WaitAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw Timeout(operation, timeout, ex);
        }
    }

    private static string FormatSeconds(TimeSpan timeout) =>
        timeout.TotalSeconds >= 1 && Math.Abs(timeout.TotalSeconds - Math.Round(timeout.TotalSeconds)) < 0.05
            ? timeout.TotalSeconds.ToString("0")
            : timeout.TotalSeconds.ToString("0.#");
}
