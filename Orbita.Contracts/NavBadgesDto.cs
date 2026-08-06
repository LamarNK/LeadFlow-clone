namespace Orbita.Contracts;

public sealed record NavBadgesDto(
    int ErrorsToday,
    int UniqueResponsesToday,
    int ActionRequired,
    DateTime UpdatedAtUtc,
    int CrmTaskNotificationsUnread = 0,
    bool? CrmTaskNotificationsEnabled = null);
