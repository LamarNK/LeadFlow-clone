using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Models;
using NotifyBot.Application.Options;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class SmsWatchCoordinator(IOptions<PlusofonOptions> options) : ISmsWatchCoordinator
{
    private readonly PlusofonOptions _options = options.Value;
    private readonly ConcurrentDictionary<long, WatchSession> _sessions = new();

    public SmsWatchRegistration TryRegister(long chatId)
    {
        var now = DateTimeOffset.UtcNow;
        var duration = TimeSpan.FromMinutes(Math.Max(1, _options.SmsWatchDurationMinutes));

        if (_sessions.TryGetValue(chatId, out var existing))
        {
            existing.ExpiresAtUtc = now + duration;
            return new SmsWatchRegistration(SmsWatchRegistrationKind.AlreadyActive, existing.ExpiresAtUtc);
        }

        var session = new WatchSession
        {
            ChatId = chatId,
            StartedAtUtc = now,
            ExpiresAtUtc = now + duration
        };
        _sessions[chatId] = session;
        return new SmsWatchRegistration(SmsWatchRegistrationKind.Started, session.ExpiresAtUtc);
    }

    public IReadOnlyList<SmsWatchSessionQuery> GetActiveQueries()
    {
        var now = DateTimeOffset.UtcNow;
        return _sessions.Values
            .Where(session => session.ExpiresAtUtc > now)
            .Select(session => new SmsWatchSessionQuery(
                session.ChatId,
                session.StartedAtUtc.AddSeconds(-30),
                session.DeliveredKeys))
            .ToList();
    }

    public void MarkDelivered(long chatId, string dedupeKey)
    {
        if (_sessions.TryGetValue(chatId, out var session))
        {
            lock (session.SyncRoot)
            {
                session.DeliveredKeys.Add(dedupeKey);
            }
        }
    }

    public IReadOnlyList<long> TakeExpiredChatIds()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new List<long>();

        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAtUtc <= now && _sessions.TryRemove(pair.Key, out _))
            {
                expired.Add(pair.Key);
            }
        }

        return expired;
    }

    private sealed class WatchSession
    {
        public required long ChatId { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; set; }
        public HashSet<string> DeliveredKeys { get; } = new(StringComparer.Ordinal);
        public object SyncRoot { get; } = new();
    }
}