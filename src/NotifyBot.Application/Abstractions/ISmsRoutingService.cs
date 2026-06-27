namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Processes incoming SMS webhook payloads and routes notifications.
/// </summary>
public interface ISmsRoutingService
{
    /// <summary>
    /// Parses the SMS text, resolves the target office, and dispatches notifications.
    /// </summary>
    Task ProcessWebhookAsync(string messageText, CancellationToken cancellationToken = default);
}