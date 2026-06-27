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
            Comments =
                $"ФИО: {response.FullName}{Environment.NewLine}" +
                $"Телефон: {response.PhoneRaw}{Environment.NewLine}" +
                $"Возраст: {response.Age?.ToString() ?? "-"}{Environment.NewLine}" +
                $"Вакансия: {response.Vacancy}{Environment.NewLine}" +
                $"Город: {response.City}{Environment.NewLine}" +
                $"Источник: Авито{Environment.NewLine}" +
                $"ID отклика (источник): {response.SourceResponseId}{Environment.NewLine}" +
                $"Ссылка на вакансию: {response.VacancyUrl}{Environment.NewLine}" +
                $"Ссылка на мессенджер: {response.MessengerUrl}{Environment.NewLine}" +
                $"Аккаунт Авито: {response.AccountName}{Environment.NewLine}" +
                $"Дата отклика: {response.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}" +
                (string.IsNullOrEmpty(rawSnippet)
                    ? string.Empty
                    : $"{Environment.NewLine}Текст отклика (фрагмент): {rawSnippet}")
        };
    }
}