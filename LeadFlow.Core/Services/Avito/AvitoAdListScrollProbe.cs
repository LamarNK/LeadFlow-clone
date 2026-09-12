using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

internal sealed record AvitoAdListScrollProbe(
    int Count,
    string FirstMarker,
    string LastMarker,
    bool Moved,
    bool AtEnd,
    bool Loader,
    bool LoadMoreClicked)
{
    public bool FirstWindowMoved(string previousFirstMarker) =>
        !string.IsNullOrEmpty(previousFirstMarker)
        && !string.IsNullOrEmpty(FirstMarker)
        && !string.Equals(previousFirstMarker, FirstMarker, StringComparison.Ordinal);

    /// <summary>Шаг прокрутки не сдвинул список и не нажал «ещё» — ждать подгрузку бессмысленно.</summary>
    public bool ScrollIdle => !Moved && !LoadMoreClicked && !Loader;
}

internal static class AvitoAdListScrollProbeParser
{
    public static AvitoAdListScrollProbe Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback();
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadInt(root, "count", out var count)
                || count < 0)
            {
                return Fallback();
            }

            return new AvitoAdListScrollProbe(
                count,
                ReadString(root, "first"),
                ReadString(root, "last"),
                ReadBool(root, "moved"),
                ReadBool(root, "atEnd"),
                ReadBool(root, "loader"),
                ReadBool(root, "loadMoreClicked"));
        }
        catch
        {
            return Fallback();
        }
    }

    private static AvitoAdListScrollProbe Fallback() =>
        new(0, string.Empty, string.Empty, Moved: false, AtEnd: false, Loader: false, LoadMoreClicked: false);

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string UnwrapJsonString(string raw)
    {
        var value = raw.Trim();
        return value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
            ? JsonSerializer.Deserialize<string>(value) ?? value
            : value;
    }
}
