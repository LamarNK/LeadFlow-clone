using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Models;
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

    public async Task<SmsCheckResult> CheckAsync(long requestingChatId, CancellationToken cancellationToken = default)
    {
        if (!_options.IsApiConfigured)
        {
            return new SmsCheckResult(
                "Plusofon API не настроен. Укажите PLUSOFON_API_TOKEN (ключ доступа из ЛК).",
                ShouldStartWatch: false);
        }

        IReadOnlyList<PlusofonSmsMessage> messages;
        try
        {
            messages = await plusofonSmsClient.GetRecentIncomingAsync(_options.SmsFetchLimit, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SMS from Plusofon");
            return new SmsCheckResult(
                ex.Message.StartsWith("Plusofon", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Client", StringComparison.OrdinalIgnoreCase)
                    ? $"Plusofon: {ex.Message}"
                    : "Не удалось получить SMS из Plusofon. Попробуйте через несколько секунд.",
                ShouldStartWatch: false);
        }

        if (messages.Count == 0)
        {
            return new SmsCheckResult("Входящих SMS пока нет.", ShouldStartWatch: true);
        }

        var parsed = ParseMessages(messages);
        if (parsed.Count == 0)
        {
            return new SmsCheckResult("Свежих SMS с 3DS-кодом не найдено.", ShouldStartWatch: true);
        }

        var boundCards = await GetBoundCardsForChatAsync(requestingChatId, cancellationToken);
        var relevant = boundCards.Count > 0
            ? parsed.Where(x => boundCards.Contains(x.Info.CardLast4)).ToList()
            : parsed;

        if (relevant.Count == 0)
        {
            var cardList = string.Join(", ", boundCards.Select(x => $"*{x}"));
            return new SmsCheckResult(
                $"Нет SMS для карт этого чата ({cardList}).",
                ShouldStartWatch: true);
        }

        var fresh = FilterByMaxAge(relevant, _options.SmsMaxAgeMinutes);
        if (fresh.Count == 0)
        {
            return new SmsCheckResult(
                $"Свежих 3DS-кодов нет (старше {_options.SmsMaxAgeMinutes} мин не показываем). Подождите SMS и нажмите /sms снова.",
                ShouldStartWatch: true);
        }

        var latest = fresh
            .OrderByDescending(x => x.Message.ReceivedAtUtc ?? DateTimeOffset.MinValue)
            .First();

        return new SmsCheckResult(
            SmsMessageFormatter.Format3ds(latest.Info, latest.Message.ReceivedAtUtc),
            ShouldStartWatch: false);
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<SmsWatchMatch>>> FindWatchMatchesForChatsAsync(
        IReadOnlyList<SmsWatchSessionQuery> queries,
        CancellationToken cancellationToken = default)
    {
        if (queries.Count == 0 || !_options.IsApiConfigured)
        {
            return new Dictionary<long, IReadOnlyList<SmsWatchMatch>>();
        }

        IReadOnlyList<PlusofonSmsMessage> messages;
        try
        {
            messages = await plusofonSmsClient.GetRecentIncomingAsync(_options.SmsFetchLimit, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch SMS from Plusofon for watch polling");
            return new Dictionary<long, IReadOnlyList<SmsWatchMatch>>();
        }

        var parsed = ParseMessages(messages);
        if (parsed.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<SmsWatchMatch>>();
        }

        var cards = await cardRepository.GetAllAsync(cancellationToken);
        var boundCardsByChat = cards
            .Where(card => card.Enabled && card.DestinationChatId is not null)
            .GroupBy(card => card.DestinationChatId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(card => card.Last4).ToHashSet(StringComparer.Ordinal));

        var result = new Dictionary<long, IReadOnlyList<SmsWatchMatch>>();
        foreach (var query in queries)
        {
            boundCardsByChat.TryGetValue(query.ChatId, out var boundCards);
            boundCards ??= [];

            var relevant = boundCards.Count > 0
                ? parsed.Where(item => boundCards.Contains(item.Info.CardLast4)).ToList()
                : parsed;

            var matches = relevant
                .Where(item => IsNewWatchMatch(item, query))
                .OrderBy(item => item.Message.ReceivedAtUtc ?? DateTimeOffset.MinValue)
                .Select(item => new SmsWatchMatch(
                    item.Info,
                    item.Message,
                    BuildDedupeKey(item.Message)))
                .ToList();

            if (matches.Count > 0)
            {
                result[query.ChatId] = matches;
            }
        }

        return result;
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
            var block = SmsMessageFormatter.FormatSmsBlock(message);

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

    private static bool IsNewWatchMatch(
        (PlusofonSmsMessage Message, SmsInfo Info) item,
        SmsWatchSessionQuery query)
    {
        var dedupeKey = BuildDedupeKey(item.Message);
        if (query.ExcludeKeys.Contains(dedupeKey))
        {
            return false;
        }

        return item.Message.ReceivedAtUtc is null || item.Message.ReceivedAtUtc >= query.Since;
    }

    private static string BuildDedupeKey(PlusofonSmsMessage message) => message.Text.Trim();

    private List<(PlusofonSmsMessage Message, SmsInfo Info)> ParseMessages(IReadOnlyList<PlusofonSmsMessage> messages) =>
        DeduplicateMessages(messages)
            .Select(message => (Message: message, Info: smsParser.Parse(message.Text)))
            .Where(item => item.Info is not null)
            .Select(item => (item.Message, Info: item.Info!))
            .ToList();

    private static List<(PlusofonSmsMessage Message, SmsInfo Info)> FilterByMaxAge(
        IEnumerable<(PlusofonSmsMessage Message, SmsInfo Info)> items,
        int maxAgeMinutes)
    {
        if (maxAgeMinutes <= 0)
        {
            return items.ToList();
        }

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-maxAgeMinutes);
        return items
            .Where(item => item.Message.ReceivedAtUtc is null || item.Message.ReceivedAtUtc >= cutoff)
            .ToList();
    }

    private static IReadOnlyList<PlusofonSmsMessage> DeduplicateMessages(IReadOnlyList<PlusofonSmsMessage> messages) =>
        messages
            .GroupBy(message => message.Text.Trim(), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(message => message.ReceivedAtUtc ?? DateTimeOffset.MinValue).First())
            .ToList();

    private async Task<HashSet<string>> GetBoundCardsForChatAsync(
        long requestingChatId,
        CancellationToken cancellationToken)
    {
        var cards = await cardRepository.GetAllAsync(cancellationToken);
        return cards
            .Where(card => card.Enabled && card.DestinationChatId == requestingChatId)
            .Select(card => card.Last4)
            .ToHashSet(StringComparer.Ordinal);
    }
}