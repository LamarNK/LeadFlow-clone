using Microsoft.Extensions.Logging;
using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Entities;

namespace NotifyBot.Application.Services;

public sealed class SmsRoutingService(
    ISmsParser smsParser,
    ICardRepository cardRepository,
    INotificationDispatcher notificationDispatcher,
    ILogger<SmsRoutingService> logger) : ISmsRoutingService
{
    public async Task ProcessWebhookAsync(string messageText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            logger.LogWarning("Webhook received with empty SMS text");
            await SafeDispatchToAdminsAsync("Получено пустое SMS-сообщение от Plusofon.", cancellationToken);
            return;
        }

        logger.LogInformation("Processing SMS webhook, text length: {Length}", messageText.Length);

        var smsInfo = smsParser.Parse(messageText);
        if (smsInfo is null)
        {
            logger.LogWarning("Failed to parse SMS text: {Text}", messageText);
            await SafeDispatchToAdminsAsync(
                $"Не удалось распознать формат SMS:\n\n{messageText}",
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "SMS parsed successfully. Card: *{CardLast4}, Code: {Code}, Amount: {Amount}, Merchant: {Merchant}",
            smsInfo.CardLast4,
            smsInfo.Code,
            smsInfo.Amount,
            smsInfo.Merchant);

        Card? card;
        try
        {
            card = await cardRepository.GetByLast4Async(smsInfo.CardLast4, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to look up card *{CardLast4}", smsInfo.CardLast4);
            await SafeDispatchToAdminsAsync(
                $"Ошибка поиска карты *{smsInfo.CardLast4} в БД.\n\n{smsInfo.RawText}",
                cancellationToken);
            return;
        }

        if (card is null || !card.Enabled)
        {
            logger.LogWarning("Unknown or disabled card: *{CardLast4}", smsInfo.CardLast4);
            await SafeDispatchToAdminsAsync(
                $"Неизвестная или отключённая карта *{smsInfo.CardLast4}:\n\n{SmsMessageFormatter.Format3ds(smsInfo)}",
                cancellationToken);
            return;
        }

        if (card.DestinationChatId is null)
        {
            logger.LogWarning("Card *{CardLast4} has no destination chat configured", card.Last4);
            await SafeDispatchToAdminsAsync(
                $"Карта *{card.Last4} не привязана к чату. Привяжите через бота.\n\n{SmsMessageFormatter.Format3ds(smsInfo)}",
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Card *{CardLast4} routed to chat {ChatId}",
            card.Last4,
            card.DestinationChatId);

        await SafeDispatchToDestinationAsync(
            card.DestinationChatId.Value,
            SmsMessageFormatter.Format3ds(smsInfo),
            cancellationToken);
    }

    private async Task SafeDispatchToDestinationAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        try
        {
            await notificationDispatcher.DispatchToDestinationAsync(chatId, message, cancellationToken);
            logger.LogInformation("Notification sent to chat {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification to chat {ChatId}", chatId);
        }
    }

    private async Task SafeDispatchToAdminsAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await notificationDispatcher.DispatchToAdminsAsync(message, cancellationToken);
            logger.LogInformation("Admin notification sent");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send admin notification");
        }
    }
}