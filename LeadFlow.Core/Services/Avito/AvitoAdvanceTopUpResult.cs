namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Результат пополнения аванса Avito на воркере. Воркер никогда не выполняет оплату
/// и не сообщает об оплате — только генерирует QR для ручной оплаты оператором.
/// </summary>
public sealed record AvitoAdvanceTopUpResult(
    bool Success,
    string? QrImageBase64 = null,
    string? QrImageUrl = null,
    string? FailureMessage = null)
{
    public static AvitoAdvanceTopUpResult QrReady(string base64, string? url = null) =>
        new(true, base64, url);

    public static AvitoAdvanceTopUpResult Failed(string message) =>
        new(false, FailureMessage: AvitoAdvanceTopUpScripts.SanitizeDiagnostic(message));
}
