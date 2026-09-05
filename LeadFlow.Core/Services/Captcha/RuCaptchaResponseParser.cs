using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Captcha;

public static class RuCaptchaResponseParser
{
    /// <summary>
    /// Разбирает значение <c>Captcha.Code</c> из SDK 2captcha-csharp для GeeTest v4.
    /// Поддерживает и обычный JSON решения, и формат <c>ExtendedResponse = 1</c>,
    /// в котором оно находится в поле <c>request</c>.
    /// </summary>
    public static GeeTestV4Solution ParseGeeTestV4V1Solution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RuCaptchaException("RuCaptcha API v1 не вернул решение GeeTest v4.");
        }

        using var doc = ParseObject(value);
        var root = doc.RootElement;
        if (root.TryGetProperty("request", out var request))
        {
            if (request.ValueKind == JsonValueKind.String)
            {
                return ParseGeeTestV4V1Solution(request.GetString());
            }

            if (request.ValueKind == JsonValueKind.Object)
            {
                return ParseGeeTestV4Solution(request);
            }
        }

        return ParseGeeTestV4Solution(root);
    }

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

        return ParseGeeTestV4Solution(solution);
    }

    /// <summary>Разбирает token / gRecaptchaResponse, возвращаемый HCaptchaTask.</summary>
    public static HCaptchaSolution? ParseHCaptchaTaskResult(string json, out bool pending)
    {
        pending = false;
        using var doc = ParseObject(json);
        var root = doc.RootElement;
        ThrowIfApiError(root);

        var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
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
            throw new RuCaptchaException("RuCaptcha getTaskResult: нет solution hCaptcha.");
        }

        var token = ReadString(solution, "gRecaptchaResponse") ?? ReadString(solution, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new RuCaptchaException("RuCaptcha solution hCaptcha не содержит token.");
        }

        return new HCaptchaSolution(token);
    }

    /// <summary>Разбирает <c>solution.text</c>, возвращаемый ImageToTextTask.</summary>
    public static ImageCaptchaSolution? ParseImageToTextTaskResult(string json, out bool pending)
    {
        pending = false;
        using var doc = ParseObject(json);
        var root = doc.RootElement;
        ThrowIfApiError(root);

        var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
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
            throw new RuCaptchaException("RuCaptcha getTaskResult: нет solution картинки.");
        }

        var text = ReadString(solution, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RuCaptchaException("RuCaptcha solution картинки не содержит text.");
        }

        return new ImageCaptchaSolution(text.Trim());
    }

    /// <summary>Разбирает ответ API v1 Coordinates: <c>x=12,y=34;x=56,y=78</c>.</summary>
    public static IReadOnlyList<ClickCaptchaPoint> ParseClickCaptchaCoordinates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RuCaptchaException("RuCaptcha API v1 не вернул координаты ClickCaptcha.");
        }

        var matches = Regex.Matches(
            value.Trim(),
            @"(?:^|;)\s*(?:x\s*=\s*)?(?<x>\d+(?:\.\d+)?)\s*,\s*(?:y\s*=\s*)?(?<y>\d+(?:\.\d+)?)\s*(?=;|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            throw new RuCaptchaException("RuCaptcha ClickCaptcha вернула координаты в неизвестном формате.");
        }

        var points = new List<ClickCaptchaPoint>(matches.Count);
        var lastEnd = 0;
        foreach (Match match in matches)
        {
            if (!string.IsNullOrWhiteSpace(value[lastEnd..match.Index]))
            {
                throw new RuCaptchaException("RuCaptcha ClickCaptcha вернула координаты в неизвестном формате.");
            }

            if (!decimal.TryParse(
                    match.Groups["x"].Value,
                    System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var x)
                || !decimal.TryParse(
                    match.Groups["y"].Value,
                    System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var y)
                || x < 0 || y < 0)
            {
                throw new RuCaptchaException("RuCaptcha ClickCaptcha вернула некорректные координаты.");
            }

            points.Add(new ClickCaptchaPoint(x, y));
            lastEnd = match.Index + match.Length;
        }

        if (!string.IsNullOrWhiteSpace(value[lastEnd..]))
        {
            throw new RuCaptchaException("RuCaptcha ClickCaptcha вернула координаты в неизвестном формате.");
        }

        return points;
    }

    /// <summary>Проверяет ответ API v2 на reportCorrect/reportIncorrect.</summary>
    public static void EnsureReportAccepted(string json)
    {
        using var doc = ParseObject(json);
        var root = doc.RootElement;
        ThrowIfApiError(root);

        var status = ReadString(root, "status");
        if (!string.IsNullOrWhiteSpace(status)
            && !string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "reported", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuCaptchaException($"RuCaptcha report: неожиданный статус «{status}».");
        }
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

            if (root.TryGetProperty("verified", out var verifiedProp) && verifiedProp.ValueKind == JsonValueKind.True)
            {
                return true;
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

    private static GeeTestV4Solution ParseGeeTestV4Solution(JsonElement solution)
    {
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
