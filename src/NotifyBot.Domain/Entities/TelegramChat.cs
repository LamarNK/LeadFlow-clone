using NotifyBot.Domain.Enums;

namespace NotifyBot.Domain.Entities;

public sealed class TelegramChat
{
    public long ChatId { get; set; }

    public TelegramChatType ChatType { get; set; }

    public string? Title { get; set; }

    public string? Username { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAtUtc { get; set; }
}