using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Application.Services;
using Telegram.Bot;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class SmsWatchHostedService(
    ISmsWatchCoordinator watchCoordinator,
    IServiceScopeFactory scopeFactory,
    IOptions<PlusofonOptions> options,
    ILogger<SmsWatchHostedService> logger) : BackgroundService
{
    private readonly PlusofonOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(3, _options.SmsWatchPollIntervalSeconds));
        var idleInterval = TimeSpan.FromSeconds(Math.Max(pollInterval.TotalSeconds, _options.SmsWatchIdleIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queries = watchCoordinator.GetActiveQueries();
                if (queries.Count == 0)
                {
                    await Task.Delay(idleInterval, stoppingToken);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var smsCheckService = scope.ServiceProvider.GetRequiredService<ISmsCheckService>();
                var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

                var matchesByChat = await smsCheckService.FindWatchMatchesForChatsAsync(queries, stoppingToken);
                foreach (var (chatId, matches) in matchesByChat)
                {
                    foreach (var match in matches)
                    {
                        var text = SmsMessageFormatter.Format3ds(match.Info, match.Message.ReceivedAtUtc);
                        await botClient.SendMessage(chatId, text, cancellationToken: stoppingToken);
                        watchCoordinator.MarkDelivered(chatId, match.DedupeKey);
                        logger.LogInformation(
                            "SMS watch delivered code for card *{CardLast4} to chat {ChatId}",
                            match.Info.CardLast4,
                            chatId);
                    }
                }

                foreach (var chatId in watchCoordinator.TakeExpiredChatIds())
                {
                    await botClient.SendMessage(
                        chatId,
                        $"Не дождался SMS за {_options.SmsWatchDurationMinutes} мин. Нажмите /sms снова.",
                        cancellationToken: stoppingToken);
                    logger.LogInformation("SMS watch expired for chat {ChatId}", chatId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SMS watch polling loop failed");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }
}