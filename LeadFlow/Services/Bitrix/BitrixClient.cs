using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using System.Net.Http;
using Microsoft.Extensions.Http;
using System.Net;

namespace LeadFlow.Services.Bitrix;

public sealed class BitrixClient(
    IHttpClientFactory httpClientFactory,
    ICandidateParser candidateParser) : IBitrixClient
{
    private const string ImportedLeadSource = "Bitrix24";
    private const int ContactLookupMaxConcurrency = 6;
    private const int ContactLookupMaxAttempts = 3;
    private sealed record DealSnapshot(
        string BitrixId,
        string Title,
        string Comments,
        DateTime CreatedAt,
        string ContactId);

    private sealed record ContactSnapshot(
        string Id,
        string FirstName,
        string LastName,
        string MiddleName,
        string Phone);

    public async Task<BitrixDuplicateLookupResult> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Skipped);
        }

        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Skipped);
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Bitrix duplicate check requested for {phoneNormalized}.",
            DeskLinkAuditLogLevel.Info);

        var endpoint = settings.Bitrix.WebhookUrl.TrimEnd('/') + "/crm.duplicate.findbycomm.json";
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var request = new
        {
            type = "PHONE",
            values = BuildDuplicateLookupValues(phoneNormalized)
        };

        try
        {
            using var result = await client.PostAsJsonAsync(endpoint, request, cancellationToken);
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
                _ = GlobalLogger.Instance.LogAsync(
                    $"Bitrix duplicate check API error for {phoneNormalized}: {apiError}",
                    DeskLinkAuditLogLevel.Error);
                return new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.Unavailable, apiError);
            }

            var hasDuplicate = HasDuplicateResult(root);

            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate check for {phoneNormalized} returned {(hasDuplicate ? "match" : "no match")}.",
                DeskLinkAuditLogLevel.Info);
            return new BitrixDuplicateLookupResult(
                hasDuplicate ? BitrixDuplicateLookupOutcome.Duplicate : BitrixDuplicateLookupOutcome.NoDuplicate);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate check failed for {phoneNormalized}.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixDuplicateLookupResult(
                BitrixDuplicateLookupOutcome.Unavailable,
                $"Проверка дублей в Bitrix24 недоступна: {ex.Message}");
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

    public async Task<IReadOnlyList<CandidateResponse>> GetExistingLeadsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return [];
        }

        var endpoint = settings.Bitrix.WebhookUrl.TrimEnd('/') + "/crm.deal.list.json";
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var deals = new List<DealSnapshot>();
        var start = 0;

        while (true)
        {
            var request = new
            {
                order = new { ID = "ASC" },
                select = new[] { "ID", "TITLE", "COMMENTS", "DATE_CREATE", "STAGE_ID", "CONTACT_ID" },
                start
            };

            using var result = await client.PostAsJsonAsync(endpoint, request, cancellationToken);
            result.EnsureSuccessStatusCode();

            await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in resultElement.EnumerateArray())
            {
                var deal = ParseDealSnapshot(item);
                if (deal is not null)
                {
                    deals.Add(deal);
                }
            }

            if (!json.RootElement.TryGetProperty("next", out var nextElement) || nextElement.ValueKind != JsonValueKind.Number)
            {
                break;
            }

            start = nextElement.GetInt32();
        }

        var contacts = await LoadContactsAsync(
            client,
            settings.Bitrix.WebhookUrl,
            deals.Select(x => x.ContactId),
            cancellationToken);

        var responses = new List<CandidateResponse>(deals.Count);
        foreach (var deal in deals)
        {
            contacts.TryGetValue(deal.ContactId, out var contact);
            responses.Add(ParseDeal(deal, contact));
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Bitrix deal sync fetched {responses.Count} deals.",
            DeskLinkAuditLogLevel.Info);
        return responses;
    }

    public async Task<BitrixCreateLeadResponse> CreateLeadAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = $"DEMO-{DateTime.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}"
            };
        }

        var preview = candidateParser.BuildPreview(response, settings.Bitrix);
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var webhookBase = settings.Bitrix.WebhookUrl.TrimEnd('/');

        try
        {
            var contactRequest = new
            {
                fields = new
                {
                    NAME = response.FirstName,
                    LAST_NAME = response.LastName,
                    SECOND_NAME = response.MiddleName,
                    PHONE = new[] { new { VALUE = response.PhoneRaw, VALUE_TYPE = "WORK" } }
                }
            };

            var contactResult = await client.PostAsJsonAsync(
                $"{webhookBase}/crm.contact.add.json",
                contactRequest,
                cancellationToken);
            contactResult.EnsureSuccessStatusCode();

            await using var contactStream = await contactResult.Content.ReadAsStreamAsync(cancellationToken);
            using var contactJson = await JsonDocument.ParseAsync(contactStream, cancellationToken: cancellationToken);
            var contactId = GetCreateResultId(contactJson.RootElement);

            try
            {
                var dealId = await PostDealAsync(client, webhookBase, preview, settings, contactId, cancellationToken);
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

                var orphanMessage =
                    $"{dealEx.Message} Контакт в Bitrix24 остался (ID {contactId}); удаление контакта не удалось — создайте сделку вручную из мониторинга или удалите контакт в портале.";
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error = orphanMessage,
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

    public async Task<BitrixCreateLeadResponse> CreateDealForContactAsync(
        CandidateResponse response,
        string contactId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = $"DEMO-{DateTime.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}",
                ContactId = contactId
            };
        }

        if (string.IsNullOrWhiteSpace(contactId))
        {
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                Error = "Не указан ID контакта Bitrix24."
            };
        }

        var preview = candidateParser.BuildPreview(response, settings.Bitrix);
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var webhookBase = settings.Bitrix.WebhookUrl.TrimEnd('/');

        try
        {
            var dealId = await PostDealAsync(client, webhookBase, preview, settings, contactId, cancellationToken);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = dealId,
                ContactId = contactId
            };
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix deal-only creation failed for contact {contactId}.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                Error = ex.Message,
                ContactId = contactId
            };
        }
    }

    private static async Task<string> PostDealAsync(
        HttpClient client,
        string webhookBase,
        BitrixLeadPreview preview,
        AppSettings settings,
        string contactId,
        CancellationToken cancellationToken)
    {
        var dealRequest = new
        {
            fields = new
            {
                TITLE = preview.Title,
                COMMENTS = preview.Comments,
                SOURCE_DESCRIPTION = settings.Bitrix.LeadSource,
                ASSIGNED_BY_ID = settings.Bitrix.ResponsibleId,
                CONTACT_ID = contactId
            }
        };

        var dealResult = await client.PostAsJsonAsync(
            $"{webhookBase}/crm.deal.add.json",
            dealRequest,
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

    private static DealSnapshot? ParseDealSnapshot(JsonElement item)
    {
        var bitrixId = GetString(item, "ID");
        if (string.IsNullOrWhiteSpace(bitrixId))
        {
            return null;
        }

        return new DealSnapshot(
            bitrixId,
            GetString(item, "TITLE"),
            GetString(item, "COMMENTS"),
            ParseDate(GetString(item, "DATE_CREATE")),
            GetString(item, "CONTACT_ID"));
    }

    private static CandidateResponse ParseDeal(DealSnapshot deal, ContactSnapshot? contact)
    {
        var fullName = !string.IsNullOrWhiteSpace(contact?.LastName) || !string.IsNullOrWhiteSpace(contact?.FirstName)
            ? string.Join(" ", new[] { contact!.LastName, contact.FirstName, contact.MiddleName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim()
            : ExtractFullName(deal.Title, deal.Comments);

        var parsedName = ParseFullName(fullName);
        var phone = !string.IsNullOrWhiteSpace(contact?.Phone)
            ? contact.Phone
            : ExtractFieldValue(deal.Comments, "Телефон");
        var city = ExtractFieldValue(deal.Comments, "Город");
        var vacancy = ExtractFieldValue(deal.Comments, "Вакансия");

        return new CandidateResponse
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.Empty,
            AccountName = ImportedLeadSource,
            Source = ImportedLeadSource,
            SourceResponseId = $"BITRIX-DEAL-{deal.BitrixId}",
            FullName = fullName,
            FirstName = parsedName.firstName,
            LastName = parsedName.lastName,
            MiddleName = parsedName.middleName,
            PhoneRaw = phone,
            City = city,
            Vacancy = string.IsNullOrWhiteSpace(vacancy) ? deal.Title : vacancy,
            RawText = deal.Comments,
            Status = ResponseStatus.Sent,
            BitrixEntityType = "Deal",
            BitrixEntityId = deal.BitrixId,
            CreatedAt = deal.CreatedAt,
            ProcessedAt = deal.CreatedAt
        };
    }

    private static async Task<Dictionary<string, ContactSnapshot>> LoadContactsAsync(
        HttpClient client,
        string webhookUrl,
        IEnumerable<string> contactIds,
        CancellationToken cancellationToken)
    {
        var contacts = new ConcurrentDictionary<string, ContactSnapshot>(StringComparer.OrdinalIgnoreCase);
        var endpoint = webhookUrl.TrimEnd('/') + "/crm.contact.get.json";
        var distinctIds = contactIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<string, ContactSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        using var gate = new SemaphoreSlim(ContactLookupMaxConcurrency, ContactLookupMaxConcurrency);
        var tasks = distinctIds.Select(async contactId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                for (var attempt = 1; attempt <= ContactLookupMaxAttempts; attempt++)
                {
                    try
                    {
                        using var result = await client.PostAsJsonAsync(endpoint, new { id = contactId }, cancellationToken);
                        if (ShouldRetry(result.StatusCode) && attempt < ContactLookupMaxAttempts)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                            continue;
                        }

                        result.EnsureSuccessStatusCode();
                        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
                        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                        if (!json.RootElement.TryGetProperty("result", out var contactElement) || contactElement.ValueKind != JsonValueKind.Object)
                        {
                            return;
                        }

                        contacts[contactId] = new ContactSnapshot(
                            contactId,
                            GetString(contactElement, "NAME"),
                            GetString(contactElement, "LAST_NAME"),
                            GetString(contactElement, "SECOND_NAME"),
                            GetFirstPhone(contactElement));
                        return;
                    }
                    catch (Exception ex) when (IsTransient(ex) && attempt < ContactLookupMaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                    }
                    catch
                    {
                        // Ignore one-off contact lookup failures and keep importing available deals.
                        return;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return contacts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException || ex is TaskCanceledException;

    private static string GetFirstPhone(JsonElement item)
    {
        if (!item.TryGetProperty("PHONE", out var phones) || phones.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var phone in phones.EnumerateArray())
        {
            if (phone.TryGetProperty("VALUE", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var number = value.GetString();
                if (!string.IsNullOrWhiteSpace(number))
                {
                    return number;
                }
            }
        }

        return string.Empty;
    }

    private static string GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ExtractFieldValue(string text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line[(fieldName.Length + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractFullName(string title, string comments)
    {
        var fromComments = ExtractFieldValue(comments, "ФИО");
        if (!string.IsNullOrWhiteSpace(fromComments))
        {
            return fromComments;
        }

        const string separator = " — ";
        var separatorIndex = title.LastIndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex >= 0 && separatorIndex + separator.Length < title.Length)
        {
            return title[(separatorIndex + separator.Length)..].Trim();
        }

        return title;
    }

    private static (string firstName, string lastName, string middleName) ParseFullName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (
            parts.ElementAtOrDefault(1) ?? string.Empty,
            parts.ElementAtOrDefault(0) ?? string.Empty,
            parts.ElementAtOrDefault(2) ?? string.Empty);
    }

    private static string GetCreateResultId(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var resultElement))
        {
            return $"BITRIX-{DateTime.UtcNow:HHmmss}";
        }

        return resultElement.ValueKind switch
        {
            JsonValueKind.Number => resultElement.GetInt32().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => resultElement.GetString() ?? $"BITRIX-{DateTime.UtcNow:HHmmss}",
            _ => $"BITRIX-{DateTime.UtcNow:HHmmss}"
        };
    }

    private static string[] BuildDuplicateLookupValues(string phoneNormalized)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            phoneNormalized
        };

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
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (property.Value.GetArrayLength() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
}
