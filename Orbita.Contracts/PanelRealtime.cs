namespace Orbita.Contracts;

public enum PanelChangeKind
{
    Dashboard,
    Responses,
    Workers,
    Events,
    Errors,
    Accounts,
    Statistics,
    NavBadges,
    Crm,
    /// <summary>Read models backing an individual worker page.</summary>
    WorkerDetails,
    /// <summary>Office-scoped reference/settings data.</summary>
    Reference,
    Listings
}

public sealed record PanelChangeNotification(
    IReadOnlyList<PanelChangeKind> Kinds,
    Guid? OfficeId,
    Guid? WorkerId,
    DateTime OccurredAtUtc,
    string? OperatorMessage = null,
    string? OperatorMessageVariant = null);
