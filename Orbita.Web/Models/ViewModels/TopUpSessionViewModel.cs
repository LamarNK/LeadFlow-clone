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

    public bool IsActive => Status is "requested" or "started" or "payment_claimed" or "qr_ready";
    public bool IsTerminal => !IsActive;
    public bool HasQr => !string.IsNullOrWhiteSpace(QrImageBase64) || !string.IsNullOrWhiteSpace(QrImageUrl);

    public string StatusLabel => Status switch
    {
        "requested" => "Запрошено",
        "started" => "Обработка",
        "payment_claimed" => "Оплата инициируется",
        "qr_ready" => "QR готов",
        "expired" => "Истекло",
        "failed" => "Ошибка",
        "cancelled" => "Отменено",
        _ => "Неизвестно"
    };

    public string TierLabel
    {
        get
        {
            if (DailyResponseCount <= 5) return "0–5 откликов → 300 ₽";
            if (DailyResponseCount <= 10) return "6–10 откликов → 900 ₽";
            return "11+ откликов → 2000 ₽";
        }
    }
}
