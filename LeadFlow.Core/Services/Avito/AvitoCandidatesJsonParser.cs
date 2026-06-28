using System.Text.Json;
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoCandidatesJsonParser
{
    public static IReadOnlyList<CandidateResponse> ParseCandidates(JsonElement root, AvitoAccount account)
    {
        if (!root.TryGetProperty("candidates", out var candidatesElement) || candidatesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<CandidateResponse>();
        foreach (var item in candidatesElement.EnumerateArray())
        {
            var fullName = item.TryGetProperty("fullName", out var fullNameProp) ? fullNameProp.GetString() ?? string.Empty : string.Empty;
            var phone = item.TryGetProperty("phone", out var phoneProp) ? phoneProp.GetString() ?? string.Empty : string.Empty;
            var sourceResponseId = item.TryGetProperty("sourceResponseId", out var sourceIdProp) ? sourceIdProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(sourceResponseId))
            {
                continue;
            }

            var vacancy = item.TryGetProperty("vacancy", out var vacancyProp) ? vacancyProp.GetString() ?? string.Empty : string.Empty;
            var city = item.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty;
            var vacancyUrl = item.TryGetProperty("vacancyUrl", out var vacancyUrlProp) ? vacancyUrlProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(vacancyUrl) && item.TryGetProperty("sourceUrl", out var legacySourceProp))
            {
                var legacy = legacySourceProp.GetString() ?? string.Empty;
                if (!string.Equals(legacy.Trim(), AvitoResponseSource.CandidatesPageUrl, StringComparison.OrdinalIgnoreCase))
                {
                    vacancyUrl = legacy.Trim();
                    if (vacancyUrl.StartsWith("//", StringComparison.Ordinal))
                    {
                        vacancyUrl = $"https:{vacancyUrl}";
                    }
                }
            }

            var messengerUrl = item.TryGetProperty("messengerUrl", out var messengerProp) ? messengerProp.GetString() ?? string.Empty : string.Empty;
            var rawText = item.TryGetProperty("rawText", out var rawTextProp) ? rawTextProp.GetString() ?? string.Empty : string.Empty;
            var age = ParseAge(item.TryGetProperty("age", out var ageProp) ? ageProp.GetString() : null);

            results.Add(new CandidateResponse
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Source = "Avito",
                SourceResponseId = sourceResponseId,
                FullName = fullName,
                PhoneRaw = phone,
                City = city,
                Vacancy = vacancy,
                Age = age,
                VacancyUrl = vacancyUrl,
                MessengerUrl = messengerUrl,
                RawText = rawText,
                CreatedAt = DateTime.UtcNow
            });
        }

        return results;
    }

    public static int? ParseAge(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var age) ? age : null;
    }
}
