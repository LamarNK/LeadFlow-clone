using System.Collections.Concurrent;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramUpdateDeduplicator
{
    private readonly ConcurrentDictionary<string, byte> _processed = new();
    private const int MaxEntries = 2000;

    public bool TryMarkProcessed(UpdateKey key)
    {
        if (!_processed.TryAdd(key.Value, 0))
        {
            return false;
        }

        if (_processed.Count > MaxEntries)
        {
            _processed.Clear();
        }

        return true;
    }

    public readonly record struct UpdateKey(string Value)
    {
        public static UpdateKey? FromUpdate(global::Telegram.Bot.Types.Update update)
        {
            if (update.Id != 0)
            {
                return new UpdateKey($"u:{update.Id}");
            }

            if (update.CallbackQuery is { } callback)
            {
                return new UpdateKey($"cq:{callback.Id}");
            }

            if (update.Message is { Chat.Id: var chatId, MessageId: var messageId })
            {
                return new UpdateKey($"m:{chatId}:{messageId}");
            }

            return null;
        }
    }
}