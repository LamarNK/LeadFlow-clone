using System.Text.Json;
using System.Text.RegularExpressions;
using LeadFlow.Core.Models;
using Orbita.Contracts;

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
            var avatarUrl = item.TryGetProperty("avatarUrl", out var avatarProp) ? avatarProp.GetString() ?? string.Empty : string.Empty;
            var rawText = item.TryGetProperty("rawText", out var rawTextProp) ? rawTextProp.GetString() ?? string.Empty : string.Empty;
            var ageText = item.TryGetProperty("age", out var ageTextProp) ? ageTextProp.GetString() : null;
            var cardGender = ParseGender(item.TryGetProperty("gender", out var genderProp) ? genderProp.GetString() : null);
            var gender = CandidateGenderResolver.ToStoredGender(
                CandidateGenderResolver.Resolve(fullName, cardGender, rawText));
            var chatMessages = AvitoChatMessagesJson.ParseFromCandidateJson(item);
            var chatMessagesJson = AvitoChatMessagesJson.Serialize(chatMessages);
            var age = ResolveAge(ageText, rawText, chatMessages);
            var explicitCitizenship = item.TryGetProperty("citizenship", out var citizenshipProp)
                ? citizenshipProp.GetString()
                : null;
            var citizenship = CandidateCitizenshipResolver.Resolve(
                explicitCitizenship,
                rawText,
                string.Join('\n', chatMessages.Select(message => message.Text)));
            var cardFingerprint = AvitoResponseCardFingerprint.Build(
                fullName,
                vacancy,
                city,
                vacancyUrl,
                messengerUrl,
                ageText);
            // Дата отклика из чата (platform/раннее сообщение); иначе момент сбора.
            var collectedAt = DateTime.UtcNow;
            var createdAt = AvitoChatMessagesJson.TryGetResponseAtUtc(chatMessages) ?? collectedAt;

            results.Add(new CandidateResponse
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Source = "Avito",
                SourceResponseId = sourceResponseId,
                CardFingerprint = cardFingerprint,
                FullName = fullName,
                PhoneRaw = phone,
                City = city,
                Vacancy = vacancy,
                Age = age,
                Gender = gender,
                Citizenship = citizenship,
                VacancyUrl = vacancyUrl,
                MessengerUrl = messengerUrl,
                AvatarUrl = avatarUrl,
                ChatMessagesJson = chatMessagesJson,
                RawText = rawText,
                CreatedAt = createdAt,
                CollectedAt = collectedAt
            });
        }

        return results;
    }

    public static string ParseGender(string? value)
    {
        var normalized = CandidateGenders.NormalizeFilterValue(value);
        if (normalized is CandidateGenders.Male or CandidateGenders.Female)
        {
            return normalized;
        }

        return CandidateGenders.ParseFromText(value) ?? string.Empty;
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
    /// Извлекает возраст из текста карточки/чата (форматы Avito: «Мужчина · N лет», «Возраст — N», «N лет»).
    /// Не принимает «опыт N лет» / «стаж N лет» — это стаж, не возраст.
    /// </summary>
    public static int? ParseAgeFromCardText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");

        var explicitAge = Regex.Match(
            normalized,
            @"возраст\s*[—\-:]\s*(\d{1,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitAge.Success && int.TryParse(explicitAge.Groups[1].Value, out var fromLabel))
        {
            return fromLabel;
        }

        // Демографическая строка: «Мужчина · 54 года» / «Женщина 37 лет».
        var demographic = Regex.Match(
            normalized,
            @"(?:мужчина|женщина)\s*[·•|,]?\s*(\d{1,2})\s*(?:лет|года|год)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (demographic.Success && int.TryParse(demographic.Groups[1].Value, out var fromDemo))
        {
            return fromDemo;
        }

        foreach (Match match in Regex.Matches(
                     normalized,
                     @"(\d{1,2})\s*(?:лет|года|год)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (IsExperienceYearsContext(normalized, match.Index))
            {
                continue;
            }

            if (int.TryParse(match.Groups[1].Value, out var age))
            {
                return age;
            }
        }

        return null;
    }

    /// <summary>
    /// Возраст: поле карточки, затем умный разбор rawText/чата.
    /// Если в age попал стаж («опыт 8 лет»), отбрасываем и ищем настоящий возраст.
    /// </summary>
    public static int? ResolveAge(
        string? ageText,
        string? rawText,
        IReadOnlyList<AvitoChatMessage>? chatMessages = null)
    {
        var fromRaw = ParseAgeFromCardText(rawText);
        var fromField = ParseAge(ageText);

        if (fromRaw is int rawAge)
        {
            return rawAge;
        }

        if (fromField is int fieldAge)
        {
            // age="8 лет" при rawText с «опыт 8 лет» без демографии — это стаж, не возраст.
            if (IsExperienceOnlyAgeField(rawText, fieldAge))
            {
                fromField = null;
            }
            else
            {
                return fieldAge;
            }
        }

        if (chatMessages is { Count: > 0 })
        {
            foreach (var message in chatMessages)
            {
                var fromChat = ParseAgeFromCardText(message.Text);
                if (fromChat is not null)
                {
                    return fromChat;
                }
            }
        }

        return fromField;
    }

    private static bool IsExperienceYearsContext(string normalizedText, int matchIndex)
    {
        var prefixStart = Math.Max(0, matchIndex - 40);
        var before = normalizedText[prefixStart..matchIndex];
        return Regex.IsMatch(
            before,
            @"(?:опыт(?:\s+работы)?|стаж)\s*[:\-]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsExperienceOnlyAgeField(string? rawText, int age)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        var normalized = Regex.Replace(rawText.Trim(), @"\s+", " ");
        var experienceHit = Regex.IsMatch(
            normalized,
            $@"(?:опыт(?:\s+работы)?|стаж)\s*[:\-]?\s*{age}\s*(?:лет|года|год)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!experienceHit)
        {
            return false;
        }

        // Если в тексте есть явный/демографический возраст — поле age не считаем «только стажем».
        return ParseAgeFromCardText(normalized) is null;
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
