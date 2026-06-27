using NotifyBot.Application.Abstractions;

namespace NotifyBot.Infrastructure.Notifications;

public sealed class NotificationDispatcher(ITelegramService telegramService) : INotificationDispatcher
{
    public Task DispatchToDestinationAsync(long chatId, string message, CancellationToken cancellationToken = default) =>
        telegramService.SendMessageAsync(chatId, message, cancellationToken);

    public Task DispatchToAdminsAsync(string message, CancellationToken cancellationToken = default) =>
        telegramService.SendAdminMessageAsync(message, cancellationToken);
}