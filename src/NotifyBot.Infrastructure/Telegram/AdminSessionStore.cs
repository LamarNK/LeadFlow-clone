using System.Collections.Concurrent;

namespace NotifyBot.Infrastructure.Telegram;

public enum AdminInputKind
{
    AddCard,
    EditCardLabel
}

public sealed record AdminInputState(AdminInputKind Kind, string? CardLast4 = null);

public sealed class AdminSessionStore
{
    private readonly ConcurrentDictionary<long, AdminInputState> _states = new();

    public void Set(long userId, AdminInputState state) => _states[userId] = state;

    public bool TryGet(long userId, out AdminInputState state) => _states.TryGetValue(userId, out state!);

    public void Clear(long userId) => _states.TryRemove(userId, out _);
}