using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbita.Api.Models;
using Orbita.Contracts;
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

    public Task<BitrixDuplicateLookupResult> HasDuplicateAsync(
        CandidateMatchProfile profile,
        string? webhookUrl,
        CancellationToken cancellationToken) =>
        HasDuplicateByProfileAsync(profile, webhookUrl, cancellationToken);

    private async Task<BitrixDuplicateLookupResult> HasDuplicateByProfileAsync(
        CandidateMatchProfile profile,
        string? webhookUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new BitrixDuplicateLookupResult(
                BitrixDuplicateLookupOutcome.Unavailable,
                $"{MissingWebhookMessage} Проверка дублей в CRM недоступна.");
        }

        var (firstName, lastName, middleName) = candidateParser.ParseName(profile.FullName);
        if (string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(firstName))
        {
            return await HasDuplicateByPhoneAsync(profile.PhoneNormalized, webhookUrl, cancellationToken);
        }

        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var webhookBase = webhookUrl.TrimEnd('/');

        try
        {
            var contacts = await ListContactsByNameAsync(
                client,
                webhookBase,
                lastName,
                firstName,
                middleName,
                cancellationToken);

            foreach (var contact in contacts)
            {
                var (age, city) = await LoadDealProfileHintsAsync(client, webhookBase, contact.Id, cancellationToken);
                var existingProfile = new CandidateMatchProfile(
                    contact.FullName,
                    age,
                    city,
                    contact.PhoneNormalized);

                if (CandidateMatchScorer.IsMatch(existingProfile, profile))
                {
                    return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Duplicate);
                }
            }

            return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.NoDuplicate);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate profile check failed for {profile.FullName}.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixDuplicateLookupResult(
                BitrixDuplicateLookupOutcome.Unavailable,
                $"Проверка дублей в Bitrix24 недоступна: {ex.Message}");
        }
    }

    private async Task<BitrixDuplicateLookupResult> HasDuplicateByPhoneAsync(
        string phoneNormalized,
        string? webhookUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new BitrixDuplicateLookupResult(
                BitrixDuplicateLookupOutcome.Unavailable,
                $"{MissingWebhookMessage} Проверка дублей в CRM недоступна.");
        }

        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Skipped);
        }

        var endpoint = webhookUrl.TrimEnd('/') + "/crm.duplicate.findbycomm.json";
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var request = new
        {
            type = "PHONE",
            values = BuildDuplicateLookupValues(phoneNormalized)
        };

        using var result = await client.PostAsJsonAsync(endpoint, request, RestJsonPreserveFieldNames, cancellationToken);
        if (!result.IsSuccessStatusCode)
        {
            var body = await result.Content.ReadAsStringAsync(cancellationToken);
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate check HTTP {(int)result.StatusCode} for {phoneNormalized}. Body: {body}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixDuplicateLookupResult(
                BitrixDuplicateLookupOutcome.Unavailable,
                $"Bitrix24 вернул код {(int)result.StatusCode}. Проверка дублей недоступна.");
        }

        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (TryGetBitrixApiError(root, out var apiError))
        {
            return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Unavailable, apiError);
        }

        var hasDuplicate = HasDuplicateResult(root);
        return new BitrixDuplicateLookupResult(
            hasDuplicate ? BitrixDuplicateLookupOutcome.Duplicate : BitrixDuplicateLookupOutcome.NoDuplicate);
    }

    private async Task<IReadOnlyList<BitrixContactProfile>> ListContactsByNameAsync(
        HttpClient client,
        string webhookBase,
        string lastName,
        string firstName,
        string middleName,
        CancellationToken cancellationToken)
    {
        var filter = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LAST_NAME"] = lastName,
            ["NAME"] = firstName
        };
        if (!string.IsNullOrWhiteSpace(middleName))
        {
            filter["SECOND_NAME"] = middleName;
        }

        var request = new
        {
            filter,
            select = new[] { "ID", "NAME", "LAST_NAME", "SECOND_NAME", "PHONE" }
        };

        using var result = await client.PostAsJsonAsync(
            $"{webhookBase}/crm.contact.list.json",
            request,
            RestJsonPreserveFieldNames,
            cancellationToken);
        result.EnsureSuccessStatusCode();

        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (TryGetBitrixApiError(json.RootElement, out var apiError))
        {
            throw new InvalidOperationException($"Bitrix contact list failed: {apiError}");
        }

        if (!json.RootElement.TryGetProperty("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var contacts = new List<BitrixContactProfile>();
        foreach (var item in resultElement.EnumerateArray())
        {
            var id = GetString(item, "ID");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var contactLastName = GetString(item, "LAST_NAME");
            var contactFirstName = GetString(item, "NAME");
            var contactMiddleName = GetString(item, "SECOND_NAME");
            var fullName = string.Join(' ',
                new[] { contactLastName, contactFirstName, contactMiddleName }
                    .Where(static x => !string.IsNullOrWhiteSpace(x)));

            contacts.Add(new BitrixContactProfile(
                id,
                fullName,
                ExtractPrimaryPhone(item)));
        }

        return contacts;
    }

    private static async Task<(int? Age, string City)> LoadDealProfileHintsAsync(
        HttpClient client,
        string webhookBase,
        string contactId,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            filter = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CONTACT_ID"] = contactId
            },
            select = new[] { "ID", "COMMENTS" },
            order = new Dictionary<string, string>(StringComparer.Ordinal) { ["ID"] = "DESC" },
            start = 0
        };

        using var result = await client.PostAsJsonAsync(
            $"{webhookBase}/crm.deal.list.json",
            request,
            RestJsonPreserveFieldNames,
            cancellationToken);
        if (!result.IsSuccessStatusCode)
        {
            return (null, string.Empty);
        }

        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.Array)
        {
            return (null, string.Empty);
        }

        foreach (var item in resultElement.EnumerateArray())
        {
            var comments = GetString(item, "COMMENTS");
            if (string.IsNullOrWhiteSpace(comments))
            {
                continue;
            }

            return (ExtractAge(comments), ExtractFieldValue(comments, "Город"));
        }

        return (null, string.Empty);
    }

    private static string ExtractPrimaryPhone(JsonElement contactElement)
    {
        if (!contactElement.TryGetProperty("PHONE", out var phoneElement)
            || phoneElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var phone in phoneElement.EnumerateArray())
        {
            var value = GetString(phone, "VALUE");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return NormalizePhoneDigits(value);
            }
        }

        return string.Empty;
    }

    private static string NormalizePhoneDigits(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('8'))
        {
            digits = "7" + digits[1..];
        }

        return digits;
    }

    private static int? ExtractAge(string comments)
    {
        var value = ExtractFieldValue(comments, "Возраст");
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "-", StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age) ? age : null;
    }

    private static string ExtractFieldValue(string text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(
            text,
            $@"{Regex.Escape(fieldName)}\s*:\s*(.+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return string.Empty;
        }

        var line = match.Groups[1].Value;
        var newlineIndex = line.IndexOf('\n');
        if (newlineIndex >= 0)
        {
            line = line[..newlineIndex];
        }

        return line.Trim();
    }

    private sealed record BitrixContactProfile(string Id, string FullName, string PhoneNormalized);

    private static string[] BuildDuplicateLookupValues(string phoneNormalized)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { phoneNormalized };
        if (phoneNormalized.Length == 11 && phoneNormalized.StartsWith('7'))
        {
            values.Add($"+{phoneNormalized}");
        }

        return values.ToArray();
    }

    private static bool HasDuplicateResult(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in resultElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0)
            {
                return true;
            }
        }

        return false;
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