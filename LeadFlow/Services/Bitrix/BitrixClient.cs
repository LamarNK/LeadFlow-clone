using System.Net.Http.Json;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using System.Net.Http;
using Microsoft.Extensions.Http;

namespace LeadFlow.Services.Bitrix;

public sealed class BitrixClient(
    IHttpClientFactory httpClientFactory,
    ICandidateParser candidateParser) : IBitrixClient
{
    public Task<bool> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl))
        {
            return Task.FromResult(false);
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Bitrix duplicate check requested for {phoneNormalized}.",
            DeskLinkAuditLogLevel.Info);
        return Task.FromResult(false);
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
}
