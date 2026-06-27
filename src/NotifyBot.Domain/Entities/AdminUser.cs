namespace NotifyBot.Domain.Entities;

public sealed class AdminUser
{
    public long TelegramUserId { get; set; }

    public string? Username { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}