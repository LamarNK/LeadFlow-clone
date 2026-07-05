using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Models;

namespace NotifyBot.Application.Models;

public sealed record SmsWatchMatch(SmsInfo Info, PlusofonSmsMessage Message, string DedupeKey);

public sealed record SmsWatchSessionQuery(
    long ChatId,
    DateTimeOffset Since,
    IReadOnlySet<string> ExcludeKeys);

public enum SmsWatchRegistrationKind
{
    Started,
    AlreadyActive
}

public sealed record SmsWatchRegistration(SmsWatchRegistrationKind Kind, DateTimeOffset ExpiresAtUtc);