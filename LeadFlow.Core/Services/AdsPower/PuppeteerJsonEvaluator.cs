using System.Text.Json;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Безопасное выполнение JS в CDP: результат всегда через JSON.stringify, без EvaluateExpressionAsync&lt;bool&gt;.
/// </summary>
public static class PuppeteerJsonEvaluator
{
    /// <param name="booleanExpression">Полное JS-выражение, возвращающее boolean (например <c>(() =&gt; true)()</c>).</param>
    public static async Task<bool> EvaluateBoolAsync(
        IPage page,
        string booleanExpression,
        CancellationToken cancellationToken = default)
    {
        var wrapped = $"JSON.stringify({booleanExpression})";
        try
        {
            var raw = await EvaluateStringWithRetryAsync(page, wrapped, cancellationToken).ConfigureAwait(false);
            return TryParseBool(raw);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<T?> EvaluateJsonAsync<T>(
        IPage page,
        string objectExpression,
        CancellationToken cancellationToken = default)
    {
        var wrapped = $"JSON.stringify(({objectExpression}))";
        var raw = await EvaluateStringWithRetryAsync(page, wrapped, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<string>(expression).ConfigureAwait(false) ?? string.Empty;
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            return await page.EvaluateExpressionAsync<string>(expression).ConfigureAwait(false) ?? string.Empty;
        }
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

    internal static string UnwrapJsonString(string raw)
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

    internal static bool IsRecoverableNavigationError(Exception ex) =>
        ex is PuppeteerException &&
        (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("frame got detached", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Response body is unavailable for redirect responses", StringComparison.OrdinalIgnoreCase));
}