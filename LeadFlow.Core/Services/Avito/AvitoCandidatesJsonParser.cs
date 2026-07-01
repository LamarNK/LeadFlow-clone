using System.Text.Json;
using System.Text.RegularExpressions;
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
            var chatMessages = AvitoChatMessagesJson.ParseFromCandidateJson(item);
            var chatMessagesJson = AvitoChatMessagesJson.Serialize(chatMessages);

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
                ChatMessagesJson = chatMessagesJson,
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

    /// <summary>
    /// Извлекает возраст из текста карточки отклика (оба формата Avito: старый и «Мужчина · N лет · …»).
    /// </summary>
    public static int? ParseAgeFromCardText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        var match = Regex.Match(normalized, @"(\d{1,2})\s*(?:лет|года|год)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var age) ? age : null;
    }

    /// <summary>Парсит название и город вакансии из textContent строки «… на вакансию …» в панели отклика.</summary>
    public static (string Vacancy, string City) ParseVacancyLineFromDetailText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, string.Empty);
        }

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        var match = Regex.Match(
            normalized,
            @"на вакансию\s+(?<title>[^·]+?)(?:\s*[·]\s*(?<city>[^·]+))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return (string.Empty, string.Empty);
        }

        var vacancy = match.Groups["title"].Value.Trim();
        var city = match.Groups["city"].Success ? match.Groups["city"].Value.Trim() : string.Empty;
        return (vacancy, city);
    }

    /// <summary>Стабильный ID отклика: при наличии ссылки на объявление — <c>avito:itemId:phone</c>, иначе хэш ФИО+телефон+вакансия+город.</summary>
    public static string BuildSourceResponseId(
        string fullName,
        string phone,
        string vacancy,
        string city,
        string? vacancyUrl)
    {
        var phoneKey = NormalizePhoneKey(phone);
        if (!string.IsNullOrWhiteSpace(vacancyUrl))
        {
            var itemMatch = Regex.Match(vacancyUrl.Trim(), @"/(\d{5,})(?:\?|$|/)", RegexOptions.CultureInvariant);
            if (itemMatch.Success && phoneKey.Length >= 10)
            {
                return $"avito:{itemMatch.Groups[1].Value}:{phoneKey}";
            }
        }

        var stablePayload = string.Join(
            "\u001f",
            new[] { fullName, phone, vacancy, city }.Select(static x => Regex.Replace((x ?? string.Empty).Trim(), @"\s+", " ")));
        return $"avito:{Fnv1a32Hex(stablePayload)}";
    }

    private static string NormalizePhoneKey(string phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('8'))
        {
            digits = $"7{digits[1..]}";
        }
        else if (digits.Length == 10)
        {
            digits = $"7{digits}";
        }

        return digits;
    }

    private static string Fnv1a32Hex(string text)
    {
        uint hash = 2166136261;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return hash.ToString("x");
    }
}
