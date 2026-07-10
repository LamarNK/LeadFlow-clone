namespace Orbita.Api.Services;

internal static class OfficeIdResolver
{
    public static (Guid? OfficeId, string? Error) Resolve(OfficeScope scope, Guid? requestedOfficeId)
    {
        if (!scope.HasAccess)
        {
            return (null, "Нет доступа.");
        }

        if (scope.IsGlobalAdmin)
        {
            return requestedOfficeId is Guid officeId
                ? (officeId, null)
                : (null, "Выберите офис.");
        }

        return scope.OfficeId is Guid operatorOfficeId
            ? (operatorOfficeId, null)
            : (null, "Офис не назначен.");
    }
}