using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using System.Net.Http;
using Microsoft.Extensions.Http;

namespace LeadFlow.Services.Bitrix;

public sealed class BitrixClient(
    IHttpClientFactory httpClientFactory,
    ICandidateParser candidateParser) : IBitrixClient
{
    private const string ImportedLeadSource = "Bitrix24";

    public async Task<bool> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl) || string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return false;
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
            result.EnsureSuccessStatusCode();

            await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var hasDuplicate = HasDuplicateResult(json.RootElement);

            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate check for {phoneNormalized} returned {(hasDuplicate ? "match" : "no match")}.",
                DeskLinkAuditLogLevel.Info);
            return hasDuplicate;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix duplicate check failed for {phoneNormalized}.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return false;
        }
    }

    public async Task<IReadOnlyList<CandidateResponse>> GetExistingLeadsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return [];
        }

        var endpoint = settings.Bitrix.WebhookUrl.TrimEnd('/') + "/crm.lead.list.json";
        var client = httpClientFactory.CreateClient(nameof(BitrixClient));
        var leads = new List<CandidateResponse>();
        var start = 0;

        while (true)
        {
            var request = new
            {
                order = new { ID = "ASC" },
                select = new[] { "ID", "TITLE", "NAME", "LAST_NAME", "SECOND_NAME", "PHONE", "ADDRESS_CITY", "COMMENTS", "DATE_CREATE" },
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
                var lead = ParseLead(item);
                if (lead is not null)
                {
                    leads.Add(lead);
                }
            }

            if (!json.RootElement.TryGetProperty("next", out var nextElement) || nextElement.ValueKind != JsonValueKind.Number)
            {
                break;
            }

            start = nextElement.GetInt32();
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Bitrix lead sync fetched {leads.Count} leads.",
            DeskLinkAuditLogLevel.Info);
        return leads;
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
        var request = new
        {
            fields = new
            {
                TITLE = preview.Title,
                NAME = preview.Name,
                LAST_NAME = preview.LastName,
                SECOND_NAME = preview.SecondName,
                PHONE = new[] { new { VALUE = response.PhoneNormalized, VALUE_TYPE = "WORK" } },
                ADDRESS_CITY = preview.City,
                COMMENTS = preview.Comments,
                SOURCE_DESCRIPTION = settings.Bitrix.LeadSource,
                ASSIGNED_BY_ID = settings.Bitrix.ResponsibleId
            }
        };

        try
        {
            var endpoint = settings.Bitrix.WebhookUrl.TrimEnd('/') + "/crm.lead.add.json";
            var client = httpClientFactory.CreateClient(nameof(BitrixClient));
            var result = await client.PostAsJsonAsync(endpoint, request, cancellationToken);
            result.EnsureSuccessStatusCode();
            return new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = $"BITRIX-{DateTime.UtcNow:HHmmss}"
            };
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix lead creation failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            return new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                Error = ex.Message
            };
        }
    }

    private static CandidateResponse? ParseLead(JsonElement item)
    {
        var bitrixId = GetString(item, "ID");
        var phone = GetFirstPhone(item);
        if (string.IsNullOrWhiteSpace(bitrixId) || string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var firstName = GetString(item, "NAME");
        var lastName = GetString(item, "LAST_NAME");
        var middleName = GetString(item, "SECOND_NAME");
        var fullName = string.Join(" ", new[] { lastName, firstName, middleName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var createdAt = ParseDate(GetString(item, "DATE_CREATE"));

        return new CandidateResponse
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.Empty,
            AccountName = ImportedLeadSource,
            Source = ImportedLeadSource,
            SourceResponseId = $"BITRIX-LEAD-{bitrixId}",
            FullName = fullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            PhoneRaw = phone,
            City = GetString(item, "ADDRESS_CITY"),
            Vacancy = GetString(item, "TITLE"),
            RawText = GetString(item, "COMMENTS"),
            Status = ResponseStatus.Sent,
            BitrixEntityType = "Lead",
            BitrixEntityId = bitrixId,
            CreatedAt = createdAt,
            ProcessedAt = createdAt
        };
    }

    private static string GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

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
