using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orbita.Api.Services.Bitrix;

public sealed record BitrixImportStage(string Id, string Name);

public sealed record BitrixImportUser(long Id, string FullName);

public sealed record BitrixImportContact(
    long Id,
    string FullName,
    string Phone,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record BitrixImportComment(
    long Id,
    long? AuthorId,
    string Text,
    DateTime CreatedAtUtc);

public sealed record BitrixImportActivity(
    long Id,
    string Subject,
    string? Description,
    long? ResponsibleId,
    long? AuthorId,
    DateTime? DueAtUtc,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    bool IsCompleted);

public sealed record BitrixImportDeal(
    long Id,
    string Title,
    string StageId,
    string StageName,
    long? ResponsibleId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string Comments,
    IReadOnlyDictionary<string, string?> Fields,
    BitrixImportContact? Contact,
    IReadOnlyList<BitrixImportComment> TimelineComments,
    IReadOnlyList<BitrixImportActivity> Activities);

public sealed record BitrixImportSnapshot(
    IReadOnlyList<BitrixImportStage> Stages,
    IReadOnlyDictionary<string, string> DealFieldTitles,
    IReadOnlyDictionary<long, BitrixImportUser> Users,
    IReadOnlyList<BitrixImportDeal> Deals);

public interface IBitrixCrmImportClient
{
    Task<BitrixImportSnapshot> LoadAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> requestedStageNames,
        IReadOnlyCollection<long>? dealIds,
        CancellationToken ct);
}

public sealed class BitrixCrmImportClient(IHttpClientFactory httpClientFactory) : IBitrixCrmImportClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        BitrixClient.RestJsonPreserveFieldNames;

    public async Task<BitrixImportSnapshot> LoadAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> requestedStageNames,
        IReadOnlyCollection<long>? dealIds,
        CancellationToken ct)
    {
        var allStages = await LoadStagesAsync(webhookUrl, categoryId, ct);
        var stages = requestedStageNames
            .Select(name => allStages.FirstOrDefault(stage =>
                string.Equals(stage.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(stage => stage is not null)
            .Cast<BitrixImportStage>()
            .DistinctBy(stage => stage.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = requestedStageNames
            .Where(name => stages.All(stage =>
                !string.Equals(stage.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "В Bitrix24 не найдены стадии: " + string.Join(", ", missing));
        }

        var fieldTitles = await LoadDealFieldTitlesAsync(webhookUrl, ct);
        var deals = new List<BitrixImportDeal>();
        foreach (var stage in stages)
        {
            var rawDeals = await LoadDealsAsync(webhookUrl, categoryId, stage, dealIds, ct);
            foreach (var rawDeal in rawDeals)
            {
                var contact = await LoadPrimaryContactAsync(webhookUrl, rawDeal.Id, ct);
                var comments = await LoadCommentsAsync(webhookUrl, rawDeal.Id, ct);
                var activities = await LoadActivitiesAsync(webhookUrl, rawDeal.Id, ct);
                deals.Add(rawDeal with
                {
                    Contact = contact,
                    TimelineComments = comments,
                    Activities = activities
                });
            }
        }

        var userIds = deals
            .SelectMany(deal =>
                new long?[] { deal.ResponsibleId }
                    .Concat(deal.TimelineComments.Select(x => x.AuthorId))
                    .Concat(deal.Activities.SelectMany(x => new long?[] { x.ResponsibleId, x.AuthorId })))
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        var users = new Dictionary<long, BitrixImportUser>();
        foreach (var userId in userIds)
        {
            var user = await LoadUserAsync(webhookUrl, userId, ct);
            if (user is not null)
            {
                users[user.Id] = user;
            }
        }

        return new BitrixImportSnapshot(stages, fieldTitles, users, deals);
    }

    private async Task<IReadOnlyList<BitrixImportStage>> LoadStagesAsync(
        string webhookUrl,
        int categoryId,
        CancellationToken ct)
    {
        var entityId = categoryId == 0 ? "DEAL_STAGE" : $"DEAL_STAGE_{categoryId}";
        using var json = await CallAsync(
            webhookUrl,
            "crm.status.list",
            new { order = new { SORT = "ASC" }, filter = new { ENTITY_ID = entityId } },
            ct);
        var result = RequireArrayResult(json.RootElement, "crm.status.list");
        return result.EnumerateArray()
            .Select(item => new BitrixImportStage(
                GetString(item, "STATUS_ID"),
                FirstNonEmpty(GetString(item, "NAME"), GetString(item, "NAME_INIT"))))
            .Where(stage => stage.Id.Length > 0 && stage.Name.Length > 0)
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadDealFieldTitlesAsync(
        string webhookUrl,
        CancellationToken ct)
    {
        using var json = await CallAsync(webhookUrl, "crm.deal.fields", new { }, ct);
        if (!json.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Bitrix24 crm.deal.fields вернул неожиданный ответ.");
        }

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in result.EnumerateObject())
        {
            var title = FirstNonEmpty(
                GetString(field.Value, "formLabel"),
                GetString(field.Value, "listLabel"),
                GetString(field.Value, "title"));
            if (title.Length > 0)
            {
                titles[field.Name] = title;
            }
        }

        return titles;
    }

    private async Task<IReadOnlyList<BitrixImportDeal>> LoadDealsAsync(
        string webhookUrl,
        int categoryId,
        BitrixImportStage stage,
        IReadOnlyCollection<long>? dealIds,
        CancellationToken ct)
    {
        if (dealIds is { Count: 0 })
        {
            return [];
        }

        var result = new List<BitrixImportDeal>();
        var start = 0;
        do
        {
            var filter = new Dictionary<string, object?>
            {
                ["CATEGORY_ID"] = categoryId,
                ["STAGE_ID"] = stage.Id
            };
            if (dealIds is not null)
            {
                filter["@ID"] = dealIds.Where(x => x > 0).Distinct().ToArray();
            }

            using var json = await CallAsync(
                webhookUrl,
                "crm.deal.list",
                new
                {
                    order = new { ID = "ASC" },
                    filter,
                    select = new[] { "*", "UF_*" },
                    start
                },
                ct);
            var rows = RequireArrayResult(json.RootElement, "crm.deal.list");
            foreach (var item in rows.EnumerateArray())
            {
                var id = GetLong(item, "ID");
                if (id <= 0)
                {
                    continue;
                }

                var fields = ReadScalarFields(item);
                result.Add(new BitrixImportDeal(
                    id,
                    GetString(item, "TITLE"),
                    stage.Id,
                    stage.Name,
                    GetNullableLong(item, "ASSIGNED_BY_ID"),
                    GetDateTimeUtc(item, "DATE_CREATE") ?? DateTime.UtcNow,
                    GetDateTimeUtc(item, "DATE_MODIFY") ?? DateTime.UtcNow,
                    GetString(item, "COMMENTS"),
                    fields,
                    null,
                    [],
                    []));
            }

            start = GetNext(json.RootElement);
        }
        while (start >= 0);

        return result;
    }

    private async Task<BitrixImportContact?> LoadPrimaryContactAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct)
    {
        using var bindingsJson = await CallAsync(
            webhookUrl,
            "crm.deal.contact.items.get",
            new { id = dealId },
            ct);
        var bindings = RequireArrayResult(bindingsJson.RootElement, "crm.deal.contact.items.get");
        var contactId = bindings.EnumerateArray()
            .Select(x => GetNullableLong(x, "CONTACT_ID") ?? GetNullableLong(x, "ID"))
            .FirstOrDefault(x => x is > 0);
        if (contactId is null)
        {
            return null;
        }

        using var contactJson = await CallAsync(
            webhookUrl,
            "crm.contact.get",
            new { id = contactId.Value },
            ct);
        var contact = RequireObjectResult(contactJson.RootElement, "crm.contact.get");
        var name = string.Join(' ', new[]
        {
            GetString(contact, "LAST_NAME"),
            GetString(contact, "NAME"),
            GetString(contact, "SECOND_NAME")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var phone = string.Empty;
        if (contact.TryGetProperty("PHONE", out var phones) && phones.ValueKind == JsonValueKind.Array)
        {
            phone = phones.EnumerateArray()
                .Select(x => GetString(x, "VALUE"))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        return new BitrixImportContact(
            contactId.Value,
            name,
            phone,
            ReadScalarFields(contact));
    }

    private async Task<IReadOnlyList<BitrixImportComment>> LoadCommentsAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct)
    {
        var result = new List<BitrixImportComment>();
        var start = 0;
        do
        {
            using var json = await CallAsync(
                webhookUrl,
                "crm.timeline.comment.list",
                new
                {
                    filter = new { ENTITY_ID = dealId, ENTITY_TYPE = "deal" },
                    select = new[] { "ID", "CREATED", "AUTHOR_ID", "COMMENT" },
                    start
                },
                ct);
            var rows = RequireArrayResult(json.RootElement, "crm.timeline.comment.list");
            foreach (var item in rows.EnumerateArray())
            {
                var text = GetString(item, "COMMENT").Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                result.Add(new BitrixImportComment(
                    GetLong(item, "ID"),
                    GetNullableLong(item, "AUTHOR_ID"),
                    text,
                    GetDateTimeUtc(item, "CREATED") ?? DateTime.UtcNow));
            }

            start = GetNext(json.RootElement);
        }
        while (start >= 0);
        return result.OrderBy(x => x.CreatedAtUtc).ToList();
    }

    private async Task<IReadOnlyList<BitrixImportActivity>> LoadActivitiesAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct)
    {
        var result = new List<BitrixImportActivity>();
        var start = 0;
        do
        {
            using var json = await CallAsync(
                webhookUrl,
                "crm.activity.list",
                new
                {
                    order = new { ID = "ASC" },
                    filter = new { OWNER_TYPE_ID = 2, OWNER_ID = dealId },
                    select = new[]
                    {
                        "ID", "SUBJECT", "DESCRIPTION", "RESPONSIBLE_ID", "AUTHOR_ID",
                        "DEADLINE", "CREATED", "LAST_UPDATED", "END_TIME", "COMPLETED"
                    },
                    start
                },
                ct);
            var rows = RequireArrayResult(json.RootElement, "crm.activity.list");
            foreach (var item in rows.EnumerateArray())
            {
                var created = GetDateTimeUtc(item, "CREATED") ?? DateTime.UtcNow;
                var updated = GetDateTimeUtc(item, "LAST_UPDATED");
                var completed = string.Equals(
                    GetString(item, "COMPLETED"),
                    "Y",
                    StringComparison.OrdinalIgnoreCase);
                result.Add(new BitrixImportActivity(
                    GetLong(item, "ID"),
                    FirstNonEmpty(GetString(item, "SUBJECT"), "Дело из Bitrix24"),
                    NullIfEmpty(GetString(item, "DESCRIPTION")),
                    GetNullableLong(item, "RESPONSIBLE_ID"),
                    GetNullableLong(item, "AUTHOR_ID"),
                    GetDateTimeUtc(item, "DEADLINE"),
                    created,
                    updated,
                    completed ? GetDateTimeUtc(item, "END_TIME") ?? updated ?? created : null,
                    completed));
            }

            start = GetNext(json.RootElement);
        }
        while (start >= 0);
        return result;
    }

    private async Task<BitrixImportUser?> LoadUserAsync(
        string webhookUrl,
        long userId,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "user.get",
            new { filter = new { ID = userId } },
            ct);
        var rows = RequireArrayResult(json.RootElement, "user.get");
        if (rows.GetArrayLength() == 0)
        {
            return null;
        }

        var item = rows[0];
        var fullName = string.Join(' ', new[]
        {
            GetString(item, "LAST_NAME"),
            GetString(item, "NAME"),
            GetString(item, "SECOND_NAME")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new BitrixImportUser(userId, FirstNonEmpty(fullName, $"Bitrix ID {userId}"));
    }

    private async Task<JsonDocument> CallAsync(
        string webhookUrl,
        string method,
        object body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new InvalidOperationException("URL входящего вебхука Bitrix24 не настроен.");
        }

        var client = httpClientFactory.CreateClient(nameof(BitrixCrmImportClient));
        var endpoint = $"{webhookUrl.TrimEnd('/')}/{method}.json";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await client.PostAsJsonAsync(endpoint, body, JsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? json = null;
            try
            {
                json = JsonDocument.Parse(content);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                // The HTTP status below is more useful than an invalid JSON error.
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500
                            || (json is not null && IsRetryableError(json.RootElement));
            if (retryable && attempt < 3)
            {
                json?.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                json?.Dispose();
                throw new HttpRequestException(
                    $"Bitrix24 {method} вернул HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            json ??= JsonDocument.Parse(content);
            if (TryGetError(json.RootElement, out var error))
            {
                json.Dispose();
                throw new InvalidOperationException($"Bitrix24 {method}: {error}");
            }

            return json;
        }

        throw new HttpRequestException($"Bitrix24 {method} не ответил после повторных попыток.");
    }

    private static JsonElement RequireArrayResult(JsonElement root, string method)
    {
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            return result;
        }

        throw new InvalidOperationException($"Bitrix24 {method} вернул неожиданный ответ.");
    }

    private static JsonElement RequireObjectResult(JsonElement root, string method)
    {
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            return result;
        }

        throw new InvalidOperationException($"Bitrix24 {method} вернул неожиданный ответ.");
    }

    private static Dictionary<string, string?> ReadScalarFields(JsonElement item)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
        {
            fields[property.Name] = ScalarString(property.Value);
        }

        return fields;
    }

    private static int GetNext(JsonElement root) =>
        root.TryGetProperty("next", out var next)
            && int.TryParse(ScalarString(next), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? ScalarString(value) ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement element, string propertyName) =>
        GetNullableLong(element, propertyName) ?? 0;

    private static long? GetNullableLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return long.TryParse(
            ScalarString(value),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static DateTime? GetDateTimeUtc(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return null;
        }

        return parsed.UtcDateTime;
    }

    private static string? ScalarString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => value.GetArrayLength() == 0
                ? null
                : string.Join(", ", value.EnumerateArray().Select(ScalarString).Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => value.GetRawText()
        };

    private static bool IsRetryableError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.ValueKind == JsonValueKind.String
        && error.GetString()?.Trim().ToUpperInvariant() is "QUERY_LIMIT_EXCEEDED" or "OVERLOAD_LIMIT";

    private static bool TryGetError(JsonElement root, out string error)
    {
        error = string.Empty;
        if (!root.TryGetProperty("error", out var code) || code.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var description = root.TryGetProperty("error_description", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        error = string.IsNullOrWhiteSpace(description) ? code.GetString() ?? "error" : description;
        return true;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
