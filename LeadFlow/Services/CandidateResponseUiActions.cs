using System.Diagnostics;
using System.Text;
using System.Windows;




namespace LeadFlow.Services;

public static class CandidateResponseUiActions
{
    public static void TryCopyPhone(CandidateResponse? response)
    {
        if (string.IsNullOrWhiteSpace(response?.PhoneRaw))
        {
            return;
        }

        Clipboard.SetText(response.PhoneRaw);
    }

    public static void TryOpenVacancyUrl(CandidateResponse? response)
    {
        var url = response?.VacancyUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static async Task TryOpenBitrixAsync(
        CandidateResponse? response,
        ISettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.BitrixEntityId))
        {
            return;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var url = BitrixPortalLinks.TryBuildEntityDetailsUrl(
            settings.Bitrix.WebhookUrl,
            response.BitrixEntityType,
            response.BitrixEntityId);
        if (url is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static bool CanCopyPhone(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.PhoneRaw);

    public static bool CanCopyMessengerUrl(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.MessengerUrl);

    public static void TryCopyMessengerUrl(CandidateResponse? response)
    {
        if (!CanCopyMessengerUrl(response))
        {
            return;
        }

        Clipboard.SetText(response!.MessengerUrl.Trim());
    }

    public static bool CanCopyVacancyUrl(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.VacancyUrl);

    public static void TryCopyVacancyUrl(CandidateResponse? response)
    {
        if (!CanCopyVacancyUrl(response))
        {
            return;
        }

        Clipboard.SetText(response!.VacancyUrl.Trim());
    }

    public static bool CanCopyCardSummary(CandidateResponse? response) => response is not null;

    public static void TryCopyCardSummary(CandidateResponse? response)
    {
        if (response is null)
        {
            return;
        }

        var createdLocal = response.CreatedAt.ToLocalTimeFromStoredUtc();
        var processedText = response.ProcessedAt is DateTime processedAt
            ? processedAt.ToLocalTimeFromStoredUtc().ToString("dd.MM.yyyy HH:mm")
            : "Ещё не обработан";
        var ageText = response.Age is int age ? $"{age} лет" : "—";
        var sb = new StringBuilder();
        sb.AppendLine($"Имя: {response.FullName}");
        sb.AppendLine($"Телефон: {(string.IsNullOrWhiteSpace(response.PhoneRaw) ? "—" : response.PhoneRaw)}");
        sb.AppendLine($"Город: {(string.IsNullOrWhiteSpace(response.City) ? "—" : response.City)}");
        sb.AppendLine($"Возраст: {ageText}");
        sb.AppendLine($"Вакансия: {(string.IsNullOrWhiteSpace(response.Vacancy) ? "—" : response.Vacancy)}");
        sb.AppendLine($"Аккаунт: {(string.IsNullOrWhiteSpace(response.AccountName) ? "—" : response.AccountName)}");
        sb.AppendLine($"Статус: {ResponseStatusFormatting.DetailDescription(response.Status)}");
        sb.AppendLine($"Создан: {createdLocal:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Обработан: {processedText}");
        if (!string.IsNullOrWhiteSpace(response.MessengerUrl))
        {
            sb.AppendLine($"Чат: {response.MessengerUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(response.VacancyUrl))
        {
            sb.AppendLine($"Вакансия (URL): {response.VacancyUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(response.BitrixEntityId))
        {
            sb.AppendLine($"Bitrix: {response.BitrixEntityType} #{response.BitrixEntityId}");
        }

        if (!string.IsNullOrWhiteSpace(response.BitrixContactId))
        {
            sb.AppendLine($"Bitrix контакт: #{response.BitrixContactId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
        {
            sb.AppendLine($"Ошибка: {response.ErrorMessage.Trim()}");
        }

        Clipboard.SetText(sb.ToString().TrimEnd());
    }

    public static bool CanOpenVacancyUrl(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.VacancyUrl);

    public static bool CanOpenBitrix(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.BitrixEntityId);
}
