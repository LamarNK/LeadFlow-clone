namespace Orbita.Api.Services;

/// <summary>
/// Panel access scope.
/// Admin — global; Manager and Operator — office-bound.
/// </summary>
public sealed record OfficeScope(Guid? OfficeId, bool IsGlobalAdmin)
{
    public static OfficeScope GlobalAdmin { get; } = new(null, true);

    public static OfficeScope ForOffice(Guid officeId) => new(officeId, false);

    public static OfficeScope NoAccess { get; } = new(null, false);

    public bool HasAccess => IsGlobalAdmin || OfficeId.HasValue;

    public Guid? ResolveFilter(Guid? requestedOfficeId)
    {
        if (IsGlobalAdmin)
        {
            return requestedOfficeId;
        }

        return OfficeId;
    }

    public bool CanAccessOffice(Guid officeId) =>
        IsGlobalAdmin || OfficeId == officeId;

    /// <summary>Worker access: admin or same office (manager/operator).</summary>
    public bool CanAccessWorker(Guid workerOfficeId) =>
        CanAccessOffice(workerOfficeId);

    /// <summary>
    /// Response access: admin, bound ownership office, worker's office
    /// (collection-pool may have null OfficeId), or shared via CRM delivery to this scope office.
    /// </summary>
    /// <param name="sharedWithScopeOffice">
    /// True when the response has a successful CRM delivery / card for <see cref="OfficeId"/>
    /// (same response, no clone/transfer).
    /// </param>
    public bool CanAccessResponse(
        Guid? responseOfficeId,
        Guid? workerOfficeId = null,
        bool sharedWithScopeOffice = false) =>
        IsGlobalAdmin
        || sharedWithScopeOffice
        || (OfficeId.HasValue && responseOfficeId.HasValue && OfficeId == responseOfficeId)
        || (OfficeId.HasValue && workerOfficeId.HasValue && OfficeId == workerOfficeId);
}
