namespace Orbita.Web.Formatting;

public static class OrbitaTime
{
    public static string ToIsoUtc(DateTime utc)
    {
        var normalized = utc.Kind switch
        {
            DateTimeKind.Utc => utc,
            DateTimeKind.Local => utc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utc, DateTimeKind.Utc)
        };

        return normalized.ToString("o");
    }

    public static string? ToIsoUtc(DateTime? utc) =>
        utc is null ? null : ToIsoUtc(utc.Value);
}