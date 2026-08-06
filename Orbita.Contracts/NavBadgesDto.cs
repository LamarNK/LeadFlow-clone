namespace Orbita.Contracts;

public sealed record NavBadgesDto(
    int ErrorsToday,
    int SentToCrm,
    int ActionRequired,
    DateTime UpdatedAtUtc,
    int CrmTaskNotificationsUnread = 0,
    bool? CrmTaskNotificationsEnabled = null);
