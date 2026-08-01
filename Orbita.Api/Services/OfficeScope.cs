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
    /// Response access: admin, bound CRM office, or worker's office
    /// (collection-pool responses may have null OfficeId until delivery).
    /// </summary>
    public bool CanAccessResponse(Guid? responseOfficeId, Guid? workerOfficeId = null) =>
        IsGlobalAdmin
        || (OfficeId.HasValue && responseOfficeId.HasValue && OfficeId == responseOfficeId)
        || (OfficeId.HasValue && workerOfficeId.HasValue && OfficeId == workerOfficeId);
}
