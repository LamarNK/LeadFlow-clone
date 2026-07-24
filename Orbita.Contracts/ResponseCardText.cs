using System.Globalization;
using System.Text;

namespace Orbita.Contracts;

public static class ResponseCardText
{
    public static string Format(
        string fullName,
        string phoneDisplay,
        string? city,
        int? age,
        string vacancy,
        string accountDisplay,
        string statusLabel,
        DateTime collectedAtUtc,
        DateTime createdAtUtc,
        DateTime? processedAtUtc,
        string? vacancyUrl = null,
        string? messengerUrl = null,
        IReadOnlyList<ResponseBitrixDeliveryDto>? bitrixDeliveries = null,
        string? bitrixEntityType = null,
        string? bitrixEntityId = null,
        string? errorMessage = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Имя: {ValueOrDash(fullName)}");
        sb.AppendLine($"Телефон: {ValueOrDash(phoneDisplay)}");
        sb.AppendLine($"Город: {ValueOrDash(city)}");
        sb.AppendLine($"Возраст: {FormatAge(age)}");
        sb.AppendLine($"Вакансия: {ValueOrDash(vacancy)}");
        sb.AppendLine($"Аккаунт: {ValueOrDash(accountDisplay)}");
        sb.AppendLine($"Статус: {ValueOrDash(statusLabel)}");
        sb.AppendLine($"Сбор: {collectedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Отклик: {createdAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Обработан: {FormatProcessedAt(processedAtUtc)}");

        if (!string.IsNullOrWhiteSpace(messengerUrl))
        {
            sb.AppendLine($"Чат: {messengerUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(vacancyUrl))
        {
            sb.AppendLine($"Вакансия (URL): {vacancyUrl.Trim()}");
        }

        if (bitrixDeliveries is { Count: > 0 })
        {
            foreach (var delivery in bitrixDeliveries)
            {
                var line = $"{delivery.BitrixLabel} — {FormatDeliveryOutcome(delivery.Outcome)}";
                if (!string.IsNullOrWhiteSpace(delivery.ErrorMessage))
                {
                    line += $" ({delivery.ErrorMessage.Trim()})";
                }

                sb.AppendLine($"Битрикс: {line}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(bitrixEntityId))
        {
            var type = string.IsNullOrWhiteSpace(bitrixEntityType) ? "entity" : bitrixEntityType.Trim();
            sb.AppendLine($"Bitrix: {type} #{bitrixEntityId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            sb.AppendLine($"Ошибка: {errorMessage.Trim()}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string FormatAge(int? age) =>
        age is > 0 ? $"{age.Value.ToString(CultureInfo.InvariantCulture)} лет" : "—";

    private static string FormatProcessedAt(DateTime? processedAtUtc) =>
        processedAtUtc is DateTime value
            ? value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : "Ещё не обработан";

    private static string FormatDeliveryOutcome(string outcome) => outcome switch
    {
        ResponseBitrixDeliveryOutcomes.Sent => "отправлен",
        ResponseBitrixDeliveryOutcomes.Duplicate => "дубль",
        ResponseBitrixDeliveryOutcomes.Error => "ошибка",
        ResponseBitrixDeliveryOutcomes.Unavailable => "недоступен",
        _ => outcome
    };
}