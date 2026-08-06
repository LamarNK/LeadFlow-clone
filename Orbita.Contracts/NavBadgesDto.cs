namespace Orbita.Contracts;

public sealed record NavBadgesDto(
    int ErrorsToday,
    int UniqueResponsesToday,
    int ActionRequired,
    DateTime UpdatedAtUtc);
