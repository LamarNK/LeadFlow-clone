using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Domain.Models;

namespace NotifyBot.Application.Services;

public sealed class SmsCheckService(
    IPlusofonSmsClient plusofonSmsClient,
    ISmsParser smsParser,
    ICardRepository cardRepository,
    IOptions<PlusofonOptions> options,
    ILogger<SmsCheckService> logger) : ISmsCheckService
{
    private readonly PlusofonOptions _options = options.Value;

    public async Task<string> CheckAsync(long requestingChatId, CancellationToken cancellationToken = default)
    {
        if (!_options.IsApiConfigured)
        {
            return "Plusofon API не настроен. Укажите PLUSOFON_API_TOKEN (ключ доступа из ЛК).";
        }

        IReadOnlyList<PlusofonSmsMessage> messages;
        try
        {
            messages = await plusofonSmsClient.GetRecentIncomingAsync(_options.SmsFetchLimit, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SMS from Plusofon");
            return ex.Message.StartsWith("Plusofon", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Client", StringComparison.OrdinalIgnoreCase)
                ? $"Plusofon: {ex.Message}"
                : "Не удалось получить SMS из Plusofon. Попробуйте через несколько секунд.";
        }

        if (messages.Count == 0)
        {
            return "Входящих SMS пока нет.";
        }

        var parsed = messages
            .Select(m => (Message: m, Info: smsParser.Parse(m.Text)))
            .Where(x => x.Info is not null)
            .Select(x => (x.Message, Info: x.Info!))
            .ToList();

        if (parsed.Count == 0)
        {
            return "Свежих SMS с 3DS-кодом не найдено.";
        }

        var boundCards = await GetBoundCardsForChatAsync(requestingChatId, cancellationToken);
        var relevant = boundCards.Count > 0
            ? parsed.Where(x => boundCards.Contains(x.Info.CardLast4)).ToList()
            : parsed;

        if (relevant.Count == 0)
        {
            var cardList = string.Join(", ", boundCards.Select(x => $"*{x}"));
            return $"Нет SMS для карт этого чата ({cardList}).";
        }

        var latestByCard = relevant
            .GroupBy(x => x.Info.CardLast4)
            .Select(g => g.OrderByDescending(x => x.Message.ReceivedAtUtc ?? DateTimeOffset.MinValue).First())
            .OrderByDescending(x => x.Message.ReceivedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();

        return string.Join(
            "\n\n—\n\n",
            latestByCard.Select(x => SmsMessageFormatter.Format3ds(x.Info)));
    }

    public async Task<string> ListAllForDebugAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsApiConfigured)
        {
            return "Plusofon API не настроен. Укажите PLUSOFON_API_TOKEN (ключ доступа из ЛК).";
        }

        IReadOnlyList<PlusofonSmsMessage> messages;
        try
        {
            messages = await plusofonSmsClient.GetRecentAsync(_options.SmsFetchLimit, incomingOnly: null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SMS list from Plusofon for debug");
            return ex.Message.StartsWith("Plusofon", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Client", StringComparison.OrdinalIgnoreCase)
                ? $"Plusofon: {ex.Message}"
                : "Не удалось получить SMS из Plusofon.";
        }

        if (messages.Count == 0)
        {
            return "SMS в Plusofon не найдены.";
        }

        const int maxLength = 3900;
        var header = $"📋 Последние SMS ({messages.Count}):";
        var blocks = new List<string>();
        var totalLength = header.Length;

        foreach (var message in messages)
        {
            var when = message.ReceivedAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "?";
            var direction = message.Incoming ? "вх" : "исх";
            var block =
                $"[{when}] {direction}\n" +
                $"от: {message.Sender ?? "?"}\n" +
                $"кому: {message.Receiver ?? "?"}\n" +
                $"{message.Text}";

            var separatorLength = blocks.Count == 0 ? 2 : 7;
            if (totalLength + separatorLength + block.Length > maxLength)
            {
                blocks.Add($"... ещё {messages.Count - blocks.Count} SMS (обрезано, лимит Telegram)");
                break;
            }

            blocks.Add(block);
            totalLength += separatorLength + block.Length;
        }

        return blocks.Count == 0 ? header : $"{header}\n\n—\n\n{string.Join("\n\n—\n\n", blocks)}";
    }

    private async Task<HashSet<string>> GetBoundCardsForChatAsync(
        long requestingChatId,
        CancellationToken cancellationToken)
    {
        var cards = await cardRepository.GetAllAsync(cancellationToken);
        return cards
            .Where(c => c.Enabled && c.DestinationChatId == requestingChatId)
            .Select(c => c.Last4)
            .ToHashSet(StringComparer.Ordinal);
    }
}