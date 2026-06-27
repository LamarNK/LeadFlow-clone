namespace Orbita.Api.Services;

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
}