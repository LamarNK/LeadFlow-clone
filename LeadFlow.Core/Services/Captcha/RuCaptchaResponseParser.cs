using System.Text.Json;

namespace LeadFlow.Core.Services.Captcha;

public static class RuCaptchaResponseParser
{
    public static long ParseCreateTaskId(string json)
    {
        using var doc = ParseObject(json);
        var root = doc.RootElement;
        ThrowIfApiError(root);

        if (root.TryGetProperty("taskId", out var taskId) && TryGetInt64(taskId, out var id) && id > 0)
        {
            return id;
        }

        throw new RuCaptchaException("RuCaptcha createTask не вернул taskId.");
    }

    /// <summary>
    /// Разбирает getTaskResult. <paramref name="pending"/> = true, пока статус processing.
    /// </summary>
    public static GeeTestV4Solution? ParseTaskResult(string json, out bool pending)
    {
        pending = false;
        using var doc = ParseObject(json);
        var root = doc.RootElement;
        ThrowIfApiError(root);

        var status = root.TryGetProperty("status", out var statusProp)
            ? statusProp.GetString()
            : null;

        if (string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            pending = true;
            return null;
        }

        if (!string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuCaptchaException($"RuCaptcha getTaskResult: неожиданный статус «{status}».");
        }

        if (!root.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Object)
        {
            throw new RuCaptchaException("RuCaptcha getTaskResult: нет solution.");
        }

        var captchaId = ReadString(solution, "captcha_id");
        var lotNumber = ReadString(solution, "lot_number");
        var passToken = ReadString(solution, "pass_token");
        var genTime = ReadString(solution, "gen_time");
        var captchaOutput = ReadString(solution, "captcha_output");

        if (string.IsNullOrWhiteSpace(lotNumber)
            || string.IsNullOrWhiteSpace(passToken)
            || string.IsNullOrWhiteSpace(genTime)
            || string.IsNullOrWhiteSpace(captchaOutput))
        {
            throw new RuCaptchaException(
                "RuCaptcha solution неполный: нужны lot_number, pass_token, gen_time, captcha_output.");
        }

        return new GeeTestV4Solution(
            CaptchaId: captchaId ?? string.Empty,
            LotNumber: lotNumber,
            PassToken: passToken,
            GenTime: genTime,
            CaptchaOutput: captchaOutput);
    }

    public static bool IsVerifyAccepted(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                return false;
            }

            var httpOk = !root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.False;
            if (!httpOk)
            {
                return false;
            }

            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : raw;
            return IsAvitoVerifiedPayload(text);
        }
        catch (JsonException)
        {
            return IsAvitoVerifiedPayload(raw);
        }
    }

    public static bool IsAvitoVerifiedPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("\"verified\":false", StringComparison.OrdinalIgnoreCase)
            || text.Contains("\"verified\": false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Contains("\"verified\":true", StringComparison.OrdinalIgnoreCase)
               || text.Contains("\"verified\": true", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RuCaptchaException("Пустой ответ RuCaptcha.");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new RuCaptchaException("RuCaptcha вернул не JSON.", ex);
        }
    }

    private static void ThrowIfApiError(JsonElement root)
    {
        if (!root.TryGetProperty("errorId", out var errorId) || !TryGetInt64(errorId, out var id) || id == 0)
        {
            return;
        }

        var code = root.TryGetProperty("errorCode", out var codeProp) ? codeProp.GetString() : null;
        var description = root.TryGetProperty("errorDescription", out var descProp) ? descProp.GetString() : null;
        var message = string.IsNullOrWhiteSpace(description)
            ? $"RuCaptcha errorId={id}" + (string.IsNullOrWhiteSpace(code) ? string.Empty : $" ({code})")
            : description;
        throw new RuCaptchaException(message) { ErrorCode = code };
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
