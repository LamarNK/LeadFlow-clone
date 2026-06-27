using System.Net.Http.Json;
using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixWebhookValidator(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private const string HintCreateWebhook =
        "В Bitrix24 откройте: Приложения → Ресурсы разработчика → вкладка «Готовые сценарии» → Другое → Входящий вебхук. " +
        "Создайте вебхук, включите право CRM, скопируйте всю ссылку целиком и вставьте сюда.";

    private const string HintEnableCrm =
        "Откройте этот вебхук в Bitrix24 → «Настройка прав» → включите CRM (контакты и сделки) → сохраните. " +
        "Если ссылка изменилась — скопируйте новую и вставьте сюда снова.";

    private const string HintRecreateWebhook =
        "Создайте новый входящий вебхук в Bitrix24 (старый мог быть удалён или код в ссылке устарел). " +
        "Скопируйте новую ссылку целиком, без пробелов в начале и в конце.";

    public async Task<BitrixWebhookValidationDto> ValidateAsync(string? webhookUrl, CancellationToken ct = default)
    {
        var steps = new List<BitrixValidationStepDto>();
        var normalized = NormalizeWebhookUrl(webhookUrl);
        if (normalized is null)
        {
            var formatIssue = DescribeFormatIssue(webhookUrl)!;
            steps.Add(Step(
                "format",
                "Ссылка на вебхук",
                BitrixValidationStepStatuses.Error,
                formatIssue.Value.Message,
                formatIssue.Value.Hint));
            return Finalize(steps);
        }

        steps.Add(Step(
            "format",
            "Ссылка на вебхук",
            BitrixValidationStepStatuses.Ok,
            "Ссылка выглядит правильно — начинается с https и содержит /rest/."));

        var client = httpClientFactory.CreateClient(nameof(BitrixWebhookValidator));
        var baseUrl = normalized;

        var scopeStep = await CallAsync(client, baseUrl, "scope", null, ct);
        if (scopeStep.Status == BitrixValidationStepStatuses.Error)
        {
            steps.Add(Step(
                "connectivity",
                "Связь с Bitrix24",
                BitrixValidationStepStatuses.Error,
                scopeStep.Message,
                scopeStep.Hint ?? HintRecreateWebhook));
            return Finalize(steps);
        }

        steps.Add(Step(
            "connectivity",
            "Связь с Bitrix24",
            BitrixValidationStepStatuses.Ok,
            "Портал отвечает — вебхук принят, ссылка рабочая."));

        var hasCrm = scopeStep.Message.Contains("crm", StringComparison.OrdinalIgnoreCase);
        if (!hasCrm)
        {
            var granted = string.IsNullOrWhiteSpace(scopeStep.Message) || scopeStep.Message == "OK"
                ? "права не определены"
                : $"сейчас выдано только: {scopeStep.Message}";
            steps.Add(Step(
                "scope",
                "Право CRM",
                BitrixValidationStepStatuses.Warning,
                $"Для отправки контактов нужно право CRM, но {granted}.",
                HintEnableCrm));
        }
        else
        {
            steps.Add(Step(
                "scope",
                "Право CRM",
                BitrixValidationStepStatuses.Ok,
                "Право CRM включено — можно работать с контактами и сделками."));
        }

        var fieldsStep = await CallAsync(client, baseUrl, "crm.contact.fields", null, ct);
        if (fieldsStep.Status == BitrixValidationStepStatuses.Error)
        {
            steps.Add(Step(
                "crm_read",
                "Доступ к контактам",
                BitrixValidationStepStatuses.Warning,
                fieldsStep.Message,
                fieldsStep.Hint ?? HintEnableCrm));
        }
        else
        {
            steps.Add(Step(
                "crm_read",
                "Доступ к контактам",
                BitrixValidationStepStatuses.Ok,
                "Контакты в CRM читаются — отправка кандидатов возможна."));
        }

        var duplicateStep = await CallAsync(
            client,
            baseUrl,
            "crm.duplicate.findbycomm",
            new { type = "PHONE", values = new[] { "+70000000000" } },
            ct);
        if (duplicateStep.Status == BitrixValidationStepStatuses.Error)
        {
            steps.Add(Step(
                "duplicate_check",
                "Проверка дублей",
                BitrixValidationStepStatuses.Warning,
                duplicateStep.Message,
                duplicateStep.Hint ?? HintEnableCrm));
        }
        else
        {
            steps.Add(Step(
                "duplicate_check",
                "Проверка дублей",
                BitrixValidationStepStatuses.Ok,
                "Поиск дублей по телефону в CRM работает."));
        }

        return Finalize(steps);
    }

    public static (string Message, string Hint)? GetFormatIssue(string? webhookUrl) =>
        DescribeFormatIssue(webhookUrl);

    public static string? NormalizeWebhookUrl(string? webhookUrl)
    {
        if (DescribeFormatIssue(webhookUrl) is not null)
        {
            return null;
        }

        return webhookUrl!.Trim().TrimEnd('/');
    }

    private static (string Message, string Hint)? DescribeFormatIssue(string? webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return ("Вставьте ссылку из Bitrix24 — поле пустое.", HintCreateWebhook);
        }

        var trimmed = webhookUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (
                "Это не похоже на ссылку. Скопируйте адрес из Bitrix24 целиком, начиная с https://.",
                HintCreateWebhook);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (
                "Ссылка должна начинаться с https:// (как в Bitrix24). http:// не подойдёт.",
                HintCreateWebhook);
        }

        if (!uri.AbsolutePath.Contains("/rest/", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "В ссылке нет части /rest/ — возможно, скопирован не вебхук, а адрес сайта или карточки.",
                HintCreateWebhook);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3
            || !string.Equals(segments[0], "rest", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[^1]))
        {
            return (
                "Ссылка обрезана. В конце должен быть секретный код вебхука — скопируйте URL из Bitrix24 полностью.",
                HintCreateWebhook);
        }

        return null;
    }

    public static string? TryGetPortalHost(string? webhookUrl)
    {
        var normalized = NormalizeWebhookUrl(webhookUrl);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Host;
    }

    public static string? MaskWebhookUrl(string? webhookUrl)
    {
        var normalized = NormalizeWebhookUrl(webhookUrl);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !string.Equals(segments[0], "rest", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host}/rest/***/";
        }

        return $"{uri.Scheme}://{uri.Host}/rest/{segments[1]}/***/";
    }

    private async Task<(string Status, string Message, string? Hint)> CallAsync(
        HttpClient client,
        string baseUrl,
        string method,
        object? body,
        CancellationToken ct)
    {
        var endpoint = $"{baseUrl}/{method}.json";
        try
        {
            using var response = body is null
                ? await client.GetAsync(endpoint, ct)
                : await client.PostAsJsonAsync(endpoint, body, JsonOptions, ct);

            var content = await response.Content.ReadAsStringAsync(ct);
            if (!TryParseJson(content, out var json))
            {
                return !response.IsSuccessStatusCode
                    ? HumanizeHttpError((int)response.StatusCode, method)
                    : (BitrixValidationStepStatuses.Error, "Bitrix24 вернул непонятный ответ — попробуйте ещё раз позже.", null);
            }

            using (json!)
            {
                if (TryGetBitrixApiError(json.RootElement, out var errorCode, out var errorDescription))
                {
                    return HumanizeBitrixError(errorCode, errorDescription, method, (int)response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return HumanizeHttpError((int)response.StatusCode, method);
                }

                if (string.Equals(method, "scope", StringComparison.OrdinalIgnoreCase)
                    && json.RootElement.TryGetProperty("result", out var scopeResult)
                    && scopeResult.ValueKind == JsonValueKind.Array)
                {
                    var scopes = scopeResult.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList();
                    return (BitrixValidationStepStatuses.Ok, string.Join(", ", scopes), null);
                }

                return (BitrixValidationStepStatuses.Ok, "OK", null);
            }
        }
        catch (TaskCanceledException)
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Bitrix24 не ответил вовремя — проверьте интернет или попробуйте через минуту.",
                "Убедитесь, что портал Bitrix24 открывается в браузере. Если сайт недоступен — проблема на стороне портала или сети.");
        }
        catch (HttpRequestException)
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Не удалось связаться с Bitrix24 — нет соединения с порталом.",
                "Откройте ваш портал Bitrix24 в браузере. Если не открывается — проверьте интернет или обратитесь к администратору CRM.");
        }
        catch (Exception)
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Произошла непредвиденная ошибка при проверке вебхука.",
                HintRecreateWebhook);
        }
    }

    private static (string Status, string Message, string? Hint) HumanizeHttpError(int statusCode, string method)
    {
        var context = MethodContext(method);
        return statusCode switch
        {
            401 or 403 => (
                BitrixValidationStepStatuses.Error,
                $"Bitrix24 отклонил запрос ({context}): неверная ссылка или недостаточно прав.",
                HintRecreateWebhook),
            404 => (
                BitrixValidationStepStatuses.Error,
                "Bitrix24 не нашёл этот вебхук — ссылка устарела или скопирована не полностью.",
                HintRecreateWebhook),
            429 => (
                BitrixValidationStepStatuses.Error,
                "Bitrix24 временно ограничил запросы — подождите 1–2 минуты и нажмите «Проверить» снова.",
                null),
            >= 500 => (
                BitrixValidationStepStatuses.Error,
                "Сервер Bitrix24 временно недоступен — попробуйте проверку позже.",
                null),
            _ => (
                BitrixValidationStepStatuses.Error,
                $"Bitrix24 вернул ошибку {statusCode} при проверке ({context}).",
                HintRecreateWebhook)
        };
    }

    private static (string Status, string Message, string? Hint) HumanizeBitrixError(
        string errorCode,
        string? errorDescription,
        string method,
        int httpStatus)
    {
        var code = errorCode.Trim();
        var lower = code.ToLowerInvariant();
        var desc = errorDescription?.Trim() ?? string.Empty;

        if (lower is "invalid_credentials" or "invalid_credential" or "no_auth_found")
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Bitrix24 не узнал этот вебхук — секретный код в ссылке неверный или вебхук уже удалён.",
                HintRecreateWebhook);
        }

        if (lower is "insufficient_scope")
        {
            return method switch
            {
                "scope" => (
                    BitrixValidationStepStatuses.Error,
                    "Вебхук не может даже сообщить свои права — ссылка, скорее всего, недействительна.",
                    HintRecreateWebhook),
                "crm.contact.fields" or "crm.duplicate.findbycomm" => (
                    BitrixValidationStepStatuses.Error,
                    "У вебхука нет доступа к CRM — без права CRM контакты не отправить.",
                    HintEnableCrm),
                _ => (
                    BitrixValidationStepStatuses.Error,
                    "У вебхука не хватает прав для этой операции.",
                    HintEnableCrm)
            };
        }

        if (lower is "query_limit_exceeded")
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Bitrix24 ограничил число запросов — подождите немного и повторите проверку.",
                null);
        }

        if (lower is "method_not_found" or "error_method_not_found")
        {
            return (
                BitrixValidationStepStatuses.Error,
                "Ссылка не похожа на входящий вебхук Bitrix24 — проверьте, что копируете URL из раздела «Входящий вебхук».",
                HintCreateWebhook);
        }

        if (httpStatus is 401 or 403)
        {
            return (
                BitrixValidationStepStatuses.Error,
                string.IsNullOrWhiteSpace(desc)
                    ? "Bitrix24 отклонил вебхук — ссылка недействительна или прав недостаточно."
                    : $"Bitrix24 отклонил вебхук: {desc}",
                HintRecreateWebhook);
        }

        return (
            BitrixValidationStepStatuses.Error,
            string.IsNullOrWhiteSpace(desc)
                ? $"Bitrix24 сообщил об ошибке: {code}."
                : $"Bitrix24 сообщил об ошибке: {desc}",
            HintRecreateWebhook);
    }

    private static string MethodContext(string method) =>
        method switch
        {
            "scope" => "проверка связи",
            "crm.contact.fields" => "доступ к контактам",
            "crm.duplicate.findbycomm" => "проверка дублей",
            _ => "запрос к CRM"
        };

    private static bool TryParseJson(string content, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetBitrixApiError(JsonElement root, out string errorCode, out string? errorDescription)
    {
        errorCode = string.Empty;
        errorDescription = null;
        if (!root.TryGetProperty("error", out var errorProp) || errorProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        errorCode = errorProp.GetString() ?? "error";
        errorDescription = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        return true;
    }

    private static BitrixValidationStepDto Step(
        string id,
        string title,
        string status,
        string message,
        string? hint = null) =>
        new(id, title, status, message, hint);

    private static BitrixWebhookValidationDto Finalize(IReadOnlyList<BitrixValidationStepDto> steps)
    {
        var failed = steps.LastOrDefault(x => x.Status == BitrixValidationStepStatuses.Error);
        if (failed is not null)
        {
            var summary = failed.Hint is not null
                ? $"{failed.Message} Что сделать: {failed.Hint}"
                : failed.Message;
            return new(BitrixValidationStatuses.Error, summary, steps);
        }

        var warned = steps.LastOrDefault(x => x.Status == BitrixValidationStepStatuses.Warning);
        if (warned is not null)
        {
            var summary = warned.Hint is not null
                ? $"{warned.Message} Что сделать: {warned.Hint}"
                : warned.Message;
            return new(BitrixValidationStatuses.Warning, summary, steps);
        }

        return new(
            BitrixValidationStatuses.Ok,
            "Всё в порядке: вебхук рабочий, CRM доступна, контакты и проверка дублей будут работать.",
            steps);
    }
}