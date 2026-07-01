namespace Orbita.Contracts;

public enum PanelChangeKind
{
    Dashboard,
    Responses,
    Workers,
    Events,
    Errors,
    Accounts,
    NavBadges
}

public sealed record PanelChangeNotification(
    IReadOnlyList<PanelChangeKind> Kinds,
    Guid? OfficeId,
    Guid? WorkerId,
    DateTime OccurredAtUtc);