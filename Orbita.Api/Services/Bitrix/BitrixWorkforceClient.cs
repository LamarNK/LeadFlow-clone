using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orbita.Api.Services.Bitrix;

public sealed record BitrixWorkforceDeal(
    long Id,
    int CategoryId,
    string StageId,
    long? AssignedById,
    long? CreatedById,
    long? ModifiedById,
    string Comments,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record BitrixWorkforceManagerStatus(
    long BitrixUserId,
    bool IsActive,
    string? TimemanStatus);

public sealed record BitrixWorkforceDealRevision(
    long DealId,
    string StageId,
    string Revision);

public interface IBitrixWorkforceClient
{
    Task<BitrixWorkforceDeal> GetDealAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct);

    Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
        string webhookUrl,
        IReadOnlyList<long> managerIds,
        CancellationToken ct);

    Task<IReadOnlyList<long>> GetDealContactIdsAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct);

    Task UpdateDealAsync(
        string webhookUrl,
        long dealId,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken ct);

    Task UpdateContactOwnerAsync(
        string webhookUrl,
        long contactId,
        long responsibleId,
        CancellationToken ct);

    Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> stageIds,
        CancellationToken ct);

    Task ValidateCrmAccessAsync(
        string webhookUrl,
        CancellationToken ct);

    Task ValidateDealFieldsAsync(
        string webhookUrl,
        IReadOnlyCollection<string> requiredFieldCodes,
        CancellationToken ct);

    Task ValidateDealPipelineAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> stageIds,
        CancellationToken ct);
}

public sealed class BitrixWorkforceClient(IHttpClientFactory httpClientFactory) : IBitrixWorkforceClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        BitrixClient.RestJsonPreserveFieldNames;

    public async Task<BitrixWorkforceDeal> GetDealAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "crm.deal.get",
            new { id = dealId },
            ct);
        var result = RequireObjectResult(json.RootElement, "crm.deal.get");
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in result.EnumerateObject())
        {
            fields[property.Name] = ScalarString(property.Value);
        }

        return new BitrixWorkforceDeal(
            dealId,
            GetInt(result, "CATEGORY_ID"),
            GetString(result, "STAGE_ID"),
            GetNullableLong(result, "ASSIGNED_BY_ID"),
            GetNullableLong(result, "CREATED_BY_ID"),
            GetNullableLong(result, "MODIFY_BY_ID"),
            GetString(result, "COMMENTS"),
            fields);
    }

    public async Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
        string webhookUrl,
        IReadOnlyList<long> managerIds,
        CancellationToken ct)
    {
        var result = new List<BitrixWorkforceManagerStatus>(managerIds.Count);
        foreach (var managerId in managerIds.Where(x => x > 0).Distinct())
        {
            var isActive = await IsUserActiveAsync(webhookUrl, managerId, ct);
            if (!isActive)
            {
                result.Add(new BitrixWorkforceManagerStatus(managerId, false, null));
                continue;
            }

            using var statusJson = await CallAsync(
                webhookUrl,
                "timeman.status",
                new { USER_ID = managerId },
                ct);
            var status = RequireObjectResult(statusJson.RootElement, "timeman.status");
            result.Add(new BitrixWorkforceManagerStatus(
                managerId,
                true,
                GetString(status, "STATUS")));
        }

        return result;
    }

    public async Task<IReadOnlyList<long>> GetDealContactIdsAsync(
        string webhookUrl,
        long dealId,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "crm.deal.contact.items.get",
            new { id = dealId },
            ct);
        if (!json.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Bitrix24 crm.deal.contact.items.get returned an unexpected response.");
        }

        var contactIds = new List<long>();
        foreach (var item in result.EnumerateArray())
        {
            var id = GetNullableLong(item, "CONTACT_ID")
                     ?? GetNullableLong(item, "ID");
            if (id is > 0)
            {
                contactIds.Add(id.Value);
            }
        }

        return contactIds.Distinct().ToList();
    }

    public async Task UpdateDealAsync(
        string webhookUrl,
        long dealId,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "crm.deal.update",
            new
            {
                id = dealId,
                fields,
                @params = new
                {
                    REGISTER_SONET_EVENT = "N",
                    REGISTER_HISTORY_EVENT = "N"
                }
            },
            ct);
        RequireTrueResult(json.RootElement, "crm.deal.update");
    }

    public async Task UpdateContactOwnerAsync(
        string webhookUrl,
        long contactId,
        long responsibleId,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "crm.contact.update",
            new
            {
                id = contactId,
                fields = new { ASSIGNED_BY_ID = responsibleId },
                @params = new { REGISTER_SONET_EVENT = "N" }
            },
            ct);
        RequireTrueResult(json.RootElement, "crm.contact.update");
    }

    public async Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> stageIds,
        CancellationToken ct)
    {
        if (stageIds.Count == 0)
        {
            return [];
        }

        var deals = new List<BitrixWorkforceDealRevision>();
        foreach (var stageId in stageIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var start = 0;
            do
            {
                using var json = await CallAsync(
                    webhookUrl,
                    "crm.deal.list",
                    new
                    {
                        order = new { ID = "ASC" },
                        filter = new Dictionary<string, object?>
                        {
                            ["CATEGORY_ID"] = categoryId,
                            ["STAGE_ID"] = stageId
                        },
                        select = new[] { "ID", "STAGE_ID", "DATE_MODIFY" },
                        start
                    },
                    ct);
                if (!json.RootElement.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        "Bitrix24 crm.deal.list returned an unexpected response.");
                }

                foreach (var item in result.EnumerateArray())
                {
                    var id = GetNullableLong(item, "ID");
                    if (id is > 0)
                    {
                        deals.Add(new BitrixWorkforceDealRevision(
                            id.Value,
                            string.IsNullOrWhiteSpace(GetString(item, "STAGE_ID"))
                                ? stageId
                                : GetString(item, "STAGE_ID"),
                            GetString(item, "DATE_MODIFY")));
                    }
                }

                start = json.RootElement.TryGetProperty("next", out var next)
                    ? ParseInt(next)
                    : -1;
            }
            while (start >= 0);
        }

        return deals
            .GroupBy(x => x.DealId)
            .Select(x => x.First())
            .ToList();
    }

    public Task ValidateCrmAccessAsync(
        string webhookUrl,
        CancellationToken ct) =>
        ValidateDealFieldsAsync(webhookUrl, [], ct);

    public async Task ValidateDealFieldsAsync(
        string webhookUrl,
        IReadOnlyCollection<string> requiredFieldCodes,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "crm.deal.fields",
            new { },
            ct);
        var fields = RequireObjectResult(json.RootElement, "crm.deal.fields");
        var availableFieldCodes = fields
            .EnumerateObject()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFieldCodes = requiredFieldCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !availableFieldCodes.Contains(x))
            .ToList();
        if (missingFieldCodes.Count > 0)
        {
            throw new InvalidOperationException(
                "Bitrix24 deal fields were not found or are not accessible: "
                + string.Join(", ", missingFieldCodes));
        }
    }

    public async Task ValidateDealPipelineAsync(
        string webhookUrl,
        int categoryId,
        IReadOnlyCollection<string> stageIds,
        CancellationToken ct)
    {
        var categoryFound = false;
        var start = 0;
        do
        {
            using var categoryJson = await CallAsync(
                webhookUrl,
                "crm.category.list",
                new { entityTypeId = 2, start },
                ct);
            var categoryResult = RequireObjectResult(
                categoryJson.RootElement,
                "crm.category.list");
            if (!categoryResult.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Bitrix24 crm.category.list returned an unexpected response.");
            }

            categoryFound = categories
                .EnumerateArray()
                .Any(x => TryGetInt(x, "id", out var id) && id == categoryId);
            start = categoryFound
                ? -1
                : categoryJson.RootElement.TryGetProperty("next", out var next)
                    ? ParseInt(next)
                    : -1;
        }
        while (start >= 0);

        if (!categoryFound)
        {
            throw new InvalidOperationException(
                $"Bitrix24 deal category {categoryId} was not found or is not accessible.");
        }

        var entityId = categoryId == 0
            ? "DEAL_STAGE"
            : $"DEAL_STAGE_{categoryId}";
        using var stagesJson = await CallAsync(
            webhookUrl,
            "crm.status.list",
            new
            {
                order = new { SORT = "ASC" },
                filter = new { ENTITY_ID = entityId }
            },
            ct);
        if (!stagesJson.RootElement.TryGetProperty("result", out var stages)
            || stages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Bitrix24 crm.status.list returned an unexpected response.");
        }

        var availableStageIds = stages
            .EnumerateArray()
            .Select(x => GetString(x, "STATUS_ID"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingStageIds = stageIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !availableStageIds.Contains(x))
            .ToList();
        if (missingStageIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Bitrix24 deal stages were not found in category {categoryId}: "
                + string.Join(", ", missingStageIds));
        }
    }

    private async Task<bool> IsUserActiveAsync(
        string webhookUrl,
        long managerId,
        CancellationToken ct)
    {
        using var json = await CallAsync(
            webhookUrl,
            "user.get",
            new { filter = new { ID = managerId } },
            ct);
        if (!json.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
        {
            return false;
        }

        var active = GetString(result[0], "ACTIVE");
        return string.Equals(active, "Y", StringComparison.OrdinalIgnoreCase)
               || string.Equals(active, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonDocument> CallAsync(
        string webhookUrl,
        string method,
        object body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new InvalidOperationException("Bitrix24 webhook URL is missing.");
        }

        var client = httpClientFactory.CreateClient(nameof(BitrixWorkforceClient));
        var endpoint = $"{webhookUrl.TrimEnd('/')}/{method}.json";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
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
                    // The HTTP error below is more useful than an invalid JSON error.
                }

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                                || (int)response.StatusCode >= 500
                                || (json is not null && HasRetryableBitrixError(json.RootElement));
                if (retryable && attempt < 3)
                {
                    json?.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    json?.Dispose();
                    throw new HttpRequestException(
                        $"Bitrix24 {method} returned HTTP {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }

                json ??= JsonDocument.Parse(content);
                if (TryGetBitrixError(json.RootElement, out var error))
                {
                    json.Dispose();
                    throw new InvalidOperationException($"Bitrix24 {method}: {error}");
                }

                return json;
            }
            catch (HttpRequestException ex) when (
                attempt < 3
                && (ex.StatusCode is null
                    || ex.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)ex.StatusCode >= 500))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }

        throw new HttpRequestException($"Bitrix24 {method} failed after retries.");
    }

    private static JsonElement RequireObjectResult(JsonElement root, string method)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object)
        {
            return result;
        }

        throw new InvalidOperationException($"Bitrix24 {method} returned an unexpected response.");
    }

    private static void RequireTrueResult(JsonElement root, string method)
    {
        if (root.TryGetProperty("result", out var result)
            && (result.ValueKind == JsonValueKind.True
                || (result.ValueKind == JsonValueKind.Number
                    && result.TryGetInt32(out var number)
                    && number == 1)
                || (result.ValueKind == JsonValueKind.String
                    && result.GetString()?.Trim().ToLowerInvariant() is "true" or "1")))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Bitrix24 {method} did not confirm the update.");
    }

    private static bool HasRetryableBitrixError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return error.GetString()?.Trim().ToUpperInvariant() is
            "QUERY_LIMIT_EXCEEDED"
            or "OVERLOAD_LIMIT";
    }

    private static bool TryGetBitrixError(JsonElement root, out string error)
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
        error = string.IsNullOrWhiteSpace(description)
            ? code.GetString() ?? "error"
            : description;
        return true;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? ScalarString(value) ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? ParseInt(value) : 0;

    private static bool TryGetInt(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = -1;
        return element.TryGetProperty(propertyName, out var property)
               && (value = ParseInt(property)) >= 0;
    }

    private static int ParseInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(
            ScalarString(value),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : -1;
    }

    private static long? GetNullableLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(
            ScalarString(value),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static string? ScalarString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
}
