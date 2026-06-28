namespace Orbita.Contracts;

public sealed record NavBadgesDto(
    int ErrorsToday,
    int ResponsesToday,
    int ActionRequired,
    DateTime UpdatedAtUtc);