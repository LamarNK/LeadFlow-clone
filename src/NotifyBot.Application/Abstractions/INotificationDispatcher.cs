namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Dispatches notifications to configured delivery channels.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Sends a notification to a specific Telegram chat.
    /// </summary>
    Task DispatchToDestinationAsync(long chatId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification to all admin users.
    /// </summary>
    Task DispatchToAdminsAsync(string message, CancellationToken cancellationToken = default);
}