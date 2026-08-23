using System.Text.Json;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Безопасное выполнение JS в CDP: результат всегда через JSON.stringify, без EvaluateExpressionAsync<bool>.
/// </summary>
public static class PuppeteerJsonEvaluator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <param name="booleanExpression">Полное JS-выражение, возвращающее boolean (например <c>(() => true)()</c>).</param>
    public static async Task<bool> EvaluateBoolAsync(
        IPage page,
        string booleanExpression,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var wrapped = $"JSON.stringify({booleanExpression})";
        try
        {
            var raw = await EvaluateStringWithRetryAsync(page, wrapped, cancellationToken, timeout)
                .ConfigureAwait(false);
            return TryParseBool(raw);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<T?> EvaluateJsonAsync<T>(
        IPage page,
        string objectExpression,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var wrapped = $"JSON.stringify(({objectExpression}))";
        var raw = await EvaluateStringWithRetryAsync(page, wrapped, cancellationToken, timeout)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            return JsonSerializer.Deserialize<T>(text);
        }
        catch
        {
            return default;
        }
    }

    private static async Task<string> EvaluateStringWithRetryAsync(
        IPage page,
        string expression,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        try
        {
            return await EvaluateStringAsync(page, expression, cancellationToken, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            return await EvaluateStringAsync(page, expression, cancellationToken, timeout).ConfigureAwait(false);
        }
    }

    private static async Task<string> EvaluateStringAsync(
        IPage page,
        string expression,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var limit = timeout ?? DefaultTimeout;
        return await AdsPowerCdpGuard.WaitAsync(
                page.EvaluateExpressionAsync<string>(expression),
                limit,
                "JavaScript-проверка страницы",
                cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;
    }

    private static bool TryParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var text = UnwrapJsonString(raw.Trim());
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(root.GetString(), out var b) && b,
                JsonValueKind.Number => root.TryGetInt32(out var n) && n != 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static string UnwrapJsonString(string raw)
    {
        if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(raw) ?? raw;
            }
            catch
            {
                return raw;
            }
        }

        return raw;
    }

    private static bool IsRecoverableNavigationError(Exception ex) =>
        ex is PuppeteerException &&
        (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("frame got detached", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Response body is unavailable for redirect responses", StringComparison.OrdinalIgnoreCase));
}
