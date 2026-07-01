namespace Orbita.Api.Helpers;

public static class DateTimeUtcHelper
{
    public static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? EnsureUtc(DateTime? value) =>
        value is null ? null : EnsureUtc(value.Value);
}