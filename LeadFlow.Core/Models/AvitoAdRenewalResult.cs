namespace LeadFlow.Core.Models;

/// <summary>Результат явной публикации объявления из вкладки «Неопубликованные».</summary>
public sealed record AvitoAdRenewalResult(
    bool Success,
    string Code,
    string Message,
    DateTime? SubmittedAtUtc = null)
{
    public static AvitoAdRenewalResult Submitted(string message) =>
        new(true, "publication_submitted", message, DateTime.UtcNow);

    public static AvitoAdRenewalResult AlreadyPublished(string message) =>
        new(true, "already_published", message, DateTime.UtcNow);

    public static AvitoAdRenewalResult Failed(string code, string message) =>
        new(false, code, message);
}
