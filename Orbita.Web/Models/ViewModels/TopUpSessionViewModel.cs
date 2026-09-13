namespace Orbita.Web.Models.ViewModels;

/// <summary>
/// Модель для UI сессии ручного пополнения баланса аккаунта.
/// </summary>
public sealed class TopUpSessionViewModel
{
    public Guid SessionId { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal CurrentBalance { get; init; }
    public decimal TargetBalance { get; init; }
    public decimal RequestedAmount { get; init; }
    public int DailyResponseCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? QrReadyAtUtc { get; init; }
    public string? QrImageBase64 { get; init; }
    public string? QrImageUrl { get; init; }
    public string? FailureMessage { get; init; }
    public string OperatorDisplayName { get; init; } = string.Empty;
    public string SubProfileId { get; init; } = string.Empty;
    public string SubProfileName { get; init; } = string.Empty;
    public string? ProgressMessage { get; init; }
    public decimal? BalanceAfter { get; init; }
    public DateTime? BalanceConfirmedAtUtc { get; init; }
    public DateTime? AwaitingBalanceAtUtc { get; init; }

    public bool IsActive => Status is "requested" or "started" or "payment_claimed" or "qr_ready" or "awaiting_balance";
    public bool IsTerminal => !IsActive;
    public bool HasQr => !string.IsNullOrWhiteSpace(QrImageBase64) || !string.IsNullOrWhiteSpace(QrImageUrl);

    public string StatusLabel => Status switch
    {
        "requested" => "Задача передана воркеру",
        "started" => "Воркер выполняет пополнение",
        "payment_claimed" => "Формируем QR-код",
        "qr_ready" => "QR-код готов к оплате",
        "expired" => "Истекло",
        "failed" => "Ошибка",
        "cancelled" => "Отменено",
        "paid" => "Оплачено",
        "awaiting_balance" => "Проверяем оплату",
        "completed" => "Подтверждено",
        "verification_required" => "Проверяем оплату",
        _ => "Неизвестно"
    };

    public string TierLabel
    {
        get
        {
            var range = DailyResponseCount switch
            {
                <= 3 => "0–3 отклика",
                <= 5 => "4–5 откликов",
                <= 9 => "6–9 откликов",
                _ => "10+ откликов"
            };
            return $"{range} → +{RequestedAmount:N0} ₽";
        }
    }
}
