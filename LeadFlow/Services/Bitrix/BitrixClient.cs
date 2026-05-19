using System.Collections.Generic;
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
    /// <summary>
    /// Bitrix REST ожидает ключи полей как в документации (<c>NAME</c>, <c>LAST_NAME</c>, <c>VALUE</c>).
    /// Перегрузка <c>PostAsJsonAsync</c> без <see cref="JsonSerializerOptions"/> использует <see cref="JsonSerializerOptions.Web"/>
    /// и портит регистр имён — CRM игнорирует поля и создаёт пустой контакт.
    /// </summary>
    internal static readonly JsonSerializerOptions RestJsonPreserveFieldNames =
        new(JsonSerializerDefaults.General);

    /// <summary>
    /// Временный стоп-кран: при <c>true</c> контакты и сделки в Bitrix24 не создаются (REST не вызывается).
    /// Поставьте <c>false</c>, чтобы снова включить отправку.
    /// </summary>
    public static bool DealCreationTemporarilyDisabled { get; set; } = false;

    private const string ImportedLeadSource = "Bitrix24";
    private const int ContactLookupMaxConcurrency = 6;
    private const int ContactLookupMaxAttempts = 3;
    private const int DealIdempotencyKeyMaxLength = 255;
    private const string MissingWebhookMessage = "Не задан URL вебхука Bitrix24.";
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
            if (!settings.DemoModeEnabled && settings.Bitrix.CheckDuplicatesInBitrix)
            {
                return new BitrixDuplicateLookupResult(
                    BitrixDuplicateLookupOutcome.Unavailable,
                    $"{MissingWebhookMessage} Проверка дублей в CRM недоступна.");
            }

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

            using var result = await client.PostAsJsonAsync(endpoint, request, RestJsonPreserveFieldNames, cancellationToken);
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
            if (!settings.DemoModeEnabled)
            {
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error = MissingWebhookMessage
                };
            }

            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = $"DEMO-{DateTime.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}"
            };
        }

        if (DealCreationTemporarilyDisabled)
        {
            const string msg =
                "Создание контактов и сделок в Bitrix24 отключено (BitrixClient.DealCreationTemporarilyDisabled).";
            _ = GlobalLogger.Instance.LogAsync(msg, DeskLinkAuditLogLevel.Warning);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                BitrixCreationSuppressed = true,
                Error = msg
            };
        }

        var preview = candidateParser.BuildPreview(response, settings.Bitrix);
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var webhookBase = settings.Bitrix.WebhookUrl.TrimEnd('/');
        var idempotencyUfCode = settings.Bitrix.DealIdempotencyUfCode.Trim();
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
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Bitrix idempotent hit: deal {existing.Value.DealId} for key {idempotencyKey}.",
                        DeskLinkAuditLogLevel.Info);
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
            if (!settings.DemoModeEnabled)
            {
                return new BitrixCreateLeadResponse
                {
                    IsSuccess = false,
                    Error = MissingWebhookMessage,
                    ContactId = contactId
                };
            }

            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = $"DEMO-{DateTime.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}",
                ContactId = contactId
            };
        }

        if (DealCreationTemporarilyDisabled)
        {
            const string msg =
                "Создание сделок в Bitrix24 отключено (BitrixClient.DealCreationTemporarilyDisabled).";
            _ = GlobalLogger.Instance.LogAsync(msg, DeskLinkAuditLogLevel.Warning);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                BitrixCreationSuppressed = true,
                Error = msg,
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
        var idempotencyUfCode = settings.Bitrix.DealIdempotencyUfCode.Trim();
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
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Bitrix idempotent hit (deal-only): deal {existing.Value.DealId} for key {idempotencyKey}.",
                        DeskLinkAuditLogLevel.Info);
                    return new BitrixCreateLeadResponse
                    {
                        IsSuccess = true,
                        EntityId = existing.Value.DealId,
                        ContactId = string.IsNullOrWhiteSpace(existing.Value.ContactId)
                            ? contactId
                            : existing.Value.ContactId
                    };
                }
            }

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

    private static string BuildDealIdempotencyKey(CandidateResponse response)
    {
        var source = string.IsNullOrWhiteSpace(response.Source) ? "Avito" : response.Source.Trim();
        var sid = string.IsNullOrWhiteSpace(response.SourceResponseId) ? string.Empty : response.SourceResponseId.Trim();
        var key = $"{source}|{response.AccountId:N}|{sid}";
        return key.Length <= DealIdempotencyKeyMaxLength ? key : key[..DealIdempotencyKeyMaxLength];
    }

    /// <summary>
    /// Поля имени контакта в Bitrix: если парсер не разбил ФИО на части, передаём целиком в фамилию, чтобы карточка не была пустой.
    /// </summary>
    private static (string Name, string LastName, string SecondName) BuildContactNameFields(CandidateResponse response)
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
        BitrixSettings bitrix)
    {
        var ageCode = bitrix.DealAgeUfCode.Trim();
        if (!string.IsNullOrEmpty(ageCode) && preview.Age is > 0 and <= 120)
        {
            // В портале на карточке отображается строковое UF «Возраст», не числовое UF_CRM_1777750747161.
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
        AppSettings settings,
        string contactId,
        string idempotencyUfCode,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["TITLE"] = preview.Title,
            ["COMMENTS"] = preview.Comments,
            ["SOURCE_DESCRIPTION"] = settings.Bitrix.LeadSource,
            ["ASSIGNED_BY_ID"] = settings.Bitrix.ResponsibleId,
            // Bitrix24 ожидает CONTACT_IDS (массив); CONTACT_ID в новых порталах часто не связывает клиента со сделкой.
            ["CONTACT_ID"] = contactId,
            ["CONTACT_IDS"] = BuildContactIdsForDeal(contactId)
        };

        if (!string.IsNullOrWhiteSpace(idempotencyUfCode) && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            fields[idempotencyUfCode] = idempotencyKey;
        }

        ApplyDealUserFields(fields, preview, settings.Bitrix);

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
                        using var result = await client.PostAsJsonAsync(
                            endpoint,
                            new { id = contactId },
                            RestJsonPreserveFieldNames,
                            cancellationToken);
                        if (ShouldRetry(result.StatusCode) && attempt < ContactLookupMaxAttempts)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
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
                        await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
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
