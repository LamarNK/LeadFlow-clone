using System.Text;
using Orbita.Api.Models;

namespace Orbita.Api.Services;

public sealed class CandidateParser
{
    public (string FirstName, string LastName, string MiddleName) ParseName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (
            parts.ElementAtOrDefault(1) ?? string.Empty,
            parts.ElementAtOrDefault(0) ?? string.Empty,
            parts.ElementAtOrDefault(2) ?? string.Empty);
    }

    public BitrixLeadPreview BuildPreview(CandidateLead response, OrbitaBitrixSettings settings)
    {
        const int rawTextMaxLen = 500;
        var rawSnippet = string.IsNullOrWhiteSpace(response.RawText)
            ? string.Empty
            : response.RawText.Length <= rawTextMaxLen
                ? response.RawText
                : response.RawText[..rawTextMaxLen] + "…";

        return new BitrixLeadPreview
        {
            Title = response.FullName,
            Name = response.FirstName,
            LastName = response.LastName,
            SecondName = response.MiddleName,
            Phone = response.PhoneRaw,
            Age = response.Age,
            City = response.City,
            Vacancy = response.Vacancy,
            Source = settings.LeadSource,
            Comments = BuildComments(response, rawSnippet)
        };
    }

    private static string BuildComments(CandidateLead response, string rawSnippet)
    {
        var sb = new StringBuilder();
        sb.Append("ФИО: ").Append(response.FullName).AppendLine();
        sb.Append("Телефон: ").Append(response.PhoneRaw).AppendLine();
        AppendPhoneHistory(sb, response);
        sb.Append("Возраст: ").Append(response.Age?.ToString() ?? "-").AppendLine();
        sb.Append("Вакансия: ").Append(response.Vacancy).AppendLine();
        sb.Append("Город: ").Append(response.City).AppendLine();
        sb.Append("Источник: Авито").AppendLine();
        sb.Append("ID отклика (источник): ").Append(response.SourceResponseId).AppendLine();
        sb.Append("Ссылка на вакансию: ").Append(response.VacancyUrl).AppendLine();
        sb.Append("Аккаунт Авито: ").Append(response.AccountName).AppendLine();
        sb.Append("Дата отклика: ").Append(response.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
        if (!string.IsNullOrEmpty(rawSnippet))
        {
            sb.AppendLine();
            sb.Append("Текст отклика (фрагмент): ").Append(rawSnippet);
        }

        return sb.ToString();
    }

    private static void AppendPhoneHistory(StringBuilder sb, CandidateLead response)
    {
        var lines = BuildPhoneHistoryLines(response);
        if (lines.Count == 0)
        {
            return;
        }

        // Одну строку «Телефон:» уже вывели; историю пишем, если есть смена / несколько записей.
        if (lines.Count == 1
            && string.Equals(
                NormalizePhoneKey(lines[0].Phone),
                NormalizePhoneKey(response.PhoneRaw),
                StringComparison.Ordinal))
        {
            return;
        }

        sb.AppendLine("История телефонов:");
        foreach (var line in lines)
        {
            sb.Append("• ").Append(line.Phone);
            if (!string.IsNullOrWhiteSpace(line.WhenLocal))
            {
                sb.Append(" (").Append(line.WhenLocal).Append(')');
            }

            sb.AppendLine();
        }
    }

    private static List<(string Phone, string WhenLocal)> BuildPhoneHistoryLines(CandidateLead response)
    {
        var result = new List<(string Phone, string WhenLocal)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string raw, string normalized, DateTime? atUtc)
        {
            var display = string.IsNullOrWhiteSpace(raw) ? normalized : raw.Trim();
            if (string.IsNullOrWhiteSpace(display))
            {
                return;
            }

            var key = NormalizePhoneKey(string.IsNullOrWhiteSpace(normalized) ? display : normalized);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                return;
            }

            var when = atUtc is DateTime utc && utc != default
                ? utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : string.Empty;
            result.Add((display, when));
        }

        if (response.PhoneHistory is { Count: > 0 })
        {
            foreach (var item in response.PhoneHistory.OrderBy(x => x.RecordedAtUtc))
            {
                TryAdd(item.PhoneRaw, item.PhoneNormalized, item.RecordedAtUtc);
            }
        }

        // Текущий номер — если его ещё нет в истории.
        TryAdd(response.PhoneRaw, response.PhoneNormalized, null);

        return result;
    }

    private static string NormalizePhoneKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsDigit).ToArray());
    }
}
