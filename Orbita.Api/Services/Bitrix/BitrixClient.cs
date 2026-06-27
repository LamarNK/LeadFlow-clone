using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Orbita.Api.Models;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services.Bitrix;

public sealed class BitrixClient(IHttpClientFactory httpClientFactory, CandidateParser candidateParser)
{
    internal static readonly JsonSerializerOptions RestJsonPreserveFieldNames =
        new(JsonSerializerDefaults.General);

    private const int DealIdempotencyKeyMaxLength = 255;
    private const string MissingWebhookMessage = "Не задан URL вебхука Bitrix24.";

    public async Task<BitrixCreateLeadResponse> CreateLeadAsync(
        CandidateLead response,
        string webhookUrl,
        OrbitaBitrixSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                Error = MissingWebhookMessage
            };
        }

        var preview = candidateParser.BuildPreview(response, settings);
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var webhookBase = webhookUrl.TrimEnd('/');
        var idempotencyUfCode = settings.DealIdempotencyUfCode.Trim();
        var idempotencyKey = BuildDealIdempotencyKey(response);

        try
        {
            if (!string.IsNullOrWhiteSpace(idempotencyUfCode))
            {
                var existing = await TryFindDealByIdempotencyKeyAsync(
                    client,
                    webhookBase,
                    idempotencyUfCode,
                    idempotencyKey,
                    cancellationToken);
                if (existing is not null)
                {
                    return new BitrixCreateLeadResponse
                    {
                        IsSuccess = true,
                        EntityId = existing.Value.DealId,
                        ContactId = existing.Value.ContactId
                    };
                }
            }

            var (contactName, contactLastName, contactSecondName) = BuildContactNameFields(response);
            var contactRequest = new
            {
                fields = new
                {
                    NAME = contactName,
                    LAST_NAME = contactLastName,
                    SECOND_NAME = contactSecondName,
                    PHONE = new[] { new { VALUE = response.PhoneRaw, VALUE_TYPE = "WORK" } }
                }
            };

            var contactResult = await client.PostAsJsonAsync(
                $"{webhookBase}/crm.contact.add.json",
                contactRequest,
                RestJsonPreserveFieldNames,
                cancellationToken);
            contactResult.EnsureSuccessStatusCode();

            await using var contactStream = await contactResult.Content.ReadAsStreamAsync(cancellationToken);
            using var contactJson = await JsonDocument.ParseAsync(contactStream, cancellationToken: cancellationToken);
            var contactRoot = contactJson.RootElement;
            if (TryGetBitrixApiError(contactRoot, out var contactApiError))
            {
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error = contactApiError
                };
            }

            var contactId = GetCreateResultId(contactRoot);
            if (string.IsNullOrWhiteSpace(contactId))
            {
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error = "Bitrix24 не вернул ID созданного контакта."
                };
            }

            try
            {
                var dealId = await PostDealAsync(
                    client,
                    webhookBase,
                    preview,
                    settings,
                    contactId,
                    idempotencyUfCode,
                    idempotencyKey,
                    cancellationToken);
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = true,
                    EntityId = dealId,
                    ContactId = contactId
                };
            }
            catch (Exception dealEx)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Bitrix deal creation failed after contact {contactId}.{Environment.NewLine}{dealEx}",
                    DeskLinkAuditLogLevel.Error);

                var deleted = await TryDeleteContactAsync(client, webhookBase, contactId, cancellationToken);
                if (deleted)
                {
                    return new BitrixCreateLeadResponse
                    {
                        IsSuccess = false,
                        Error = dealEx.Message,
                        ContactId = string.Empty
                    };
                }

                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error =
                        $"{dealEx.Message} Контакт в Bitrix24 остался (ID {contactId}); удаление контакта не удалось.",
                    ContactId = contactId
                };
            }
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix contact/deal creation failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                Error = ex.Message
            };
        }
    }

    private static string BuildDealIdempotencyKey(CandidateLead response)
    {
        var source = string.IsNullOrWhiteSpace(response.Source) ? "Avito" : response.Source.Trim();
        var sid = string.IsNullOrWhiteSpace(response.SourceResponseId) ? string.Empty : response.SourceResponseId.Trim();
        var key = $"{source}|{response.AccountId:N}|{sid}";
        return key.Length <= DealIdempotencyKeyMaxLength ? key : key[..DealIdempotencyKeyMaxLength];
    }

    private static (string Name, string LastName, string SecondName) BuildContactNameFields(CandidateLead response)
    {
        var hasParts = !string.IsNullOrWhiteSpace(response.LastName) || !string.IsNullOrWhiteSpace(response.FirstName);
        if (hasParts)
        {
            return (response.FirstName, response.LastName, response.MiddleName);
        }

        var full = response.FullName.Trim();
        return string.IsNullOrEmpty(full) ? (string.Empty, string.Empty, string.Empty) : (string.Empty, full, string.Empty);
    }

    private static object[] BuildContactIdsForDeal(string contactId)
    {
        if (int.TryParse(contactId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            return [numericId];
        }

        return [contactId];
    }

    private static async Task<(string DealId, string ContactId)?> TryFindDealByIdempotencyKeyAsync(
        HttpClient client,
        string webhookBase,
        string ufCode,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ufCode) || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var filter = new Dictionary<string, string>(StringComparer.Ordinal) { [ufCode] = idempotencyKey };
        var listRequest = new
        {
            filter,
            select = new[] { "ID", "CONTACT_ID" },
            start = 0
        };

        using var result = await client.PostAsJsonAsync(
            $"{webhookBase}/crm.deal.list.json",
            listRequest,
            RestJsonPreserveFieldNames,
            cancellationToken);
        if (!result.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (TryGetBitrixApiError(root, out _))
        {
            return null;
        }

        if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in resultElement.EnumerateArray())
        {
            var dealId = GetString(item, "ID");
            if (string.IsNullOrWhiteSpace(dealId))
            {
                continue;
            }

            var contactId = GetString(item, "CONTACT_ID");
            return (dealId, contactId);
        }

        return null;
    }

    private static void ApplyDealUserFields(
        Dictionary<string, object?> fields,
        BitrixLeadPreview preview,
        OrbitaBitrixSettings bitrix)
    {
        var ageCode = bitrix.DealAgeUfCode.Trim();
        if (!string.IsNullOrEmpty(ageCode) && preview.Age is > 0 and <= 120)
        {
            fields[ageCode] = preview.Age.Value.ToString(CultureInfo.InvariantCulture);
        }

        var professionCode = bitrix.DealProfessionUfCode.Trim();
        if (!string.IsNullOrEmpty(professionCode) && !string.IsNullOrWhiteSpace(preview.Vacancy))
        {
            fields[professionCode] = preview.Vacancy.Trim();
        }

        var cityCode = bitrix.DealCityUfCode.Trim();
        if (!string.IsNullOrEmpty(cityCode) && !string.IsNullOrWhiteSpace(preview.City))
        {
            fields[cityCode] = preview.City.Trim();
        }
    }

    private static async Task<string> PostDealAsync(
        HttpClient client,
        string webhookBase,
        BitrixLeadPreview preview,
        OrbitaBitrixSettings settings,
        string contactId,
        string idempotencyUfCode,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["TITLE"] = preview.Title,
            ["COMMENTS"] = preview.Comments,
            ["SOURCE_DESCRIPTION"] = settings.LeadSource,
            ["ASSIGNED_BY_ID"] = settings.ResponsibleId,
            ["CONTACT_ID"] = contactId,
            ["CONTACT_IDS"] = BuildContactIdsForDeal(contactId)
        };

        if (!string.IsNullOrWhiteSpace(idempotencyUfCode) && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            fields[idempotencyUfCode] = idempotencyKey;
        }

        ApplyDealUserFields(fields, preview, settings);

        var dealRequest = new { fields };

        var dealResult = await client.PostAsJsonAsync(
            $"{webhookBase}/crm.deal.add.json",
            dealRequest,
            RestJsonPreserveFieldNames,
            cancellationToken);
        dealResult.EnsureSuccessStatusCode();

        await using var dealStream = await dealResult.Content.ReadAsStreamAsync(cancellationToken);
        using var dealJson = await JsonDocument.ParseAsync(dealStream, cancellationToken: cancellationToken);
        var dealRoot = dealJson.RootElement;
        if (TryGetBitrixApiError(dealRoot, out var apiError))
        {
            throw new InvalidOperationException(apiError);
        }

        return GetCreateResultId(dealRoot);
    }

    private static async Task<bool> TryDeleteContactAsync(
        HttpClient client,
        string webhookBase,
        string contactId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contactId))
        {
            return false;
        }

        try
        {
            using var result = await client.PostAsJsonAsync(
                $"{webhookBase}/crm.contact.delete.json",
                new { id = contactId },
                RestJsonPreserveFieldNames,
                cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"crm.contact.delete failed for {contactId}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
            return false;
        }
    }

    private static bool TryGetBitrixApiError(JsonElement root, out string message)
    {
        message = string.Empty;
        if (!root.TryGetProperty("error", out var errorProp) || errorProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var err = errorProp.GetString() ?? "error";
        var desc = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        message = string.IsNullOrWhiteSpace(desc) ? err : $"{err}: {desc}";
        return true;
    }

    private static string GetCreateResultId(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
        {
            return string.Empty;
        }

        return result.ValueKind switch
        {
            JsonValueKind.Number => result.GetRawText(),
            JsonValueKind.String => result.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}