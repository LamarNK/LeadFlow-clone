using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

internal sealed record AvitoScrollStepItem(
    int Index,
    string FullName,
    string CardFingerprint,
    string City,
    string Age,
    string Gender,
    string PhoneDigits);

internal sealed record AvitoScrollStepProbe(
    int ItemCount,
    bool Moved,
    bool AtEnd,
    IReadOnlyList<AvitoScrollStepItem> NewItems,
    bool IsFullRescan,
    bool RequiresFallbackRescan,
    double ScrollTop = -1,
    int ClientHeight = 0)
{
    public bool AllowEarlyStop => !IsFullRescan && !RequiresFallbackRescan;
}

internal sealed record ScrollGeometryProbe(double ScrollTop, int ClientHeight, double ScrollHeight);

internal static class AvitoScrollStepProbeParser
{
    public static AvitoScrollStepProbe Parse(string? raw, int previousItemCount)
    {
        var fallback = Fallback(previousItemCount);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadInt(root, "itemCount", out var itemCount)
                || itemCount < 0
                || !TryReadBool(root, "moved", out var moved)
                || !TryReadBool(root, "atEnd", out var atEnd)
                || !TryReadBool(root, "fullRescan", out var fullRescan)
                || !TryReadBool(root, "structureValid", out var structureValid)
                || !root.TryGetProperty("newItems", out var itemsElement)
                || itemsElement.ValueKind != JsonValueKind.Array)
            {
                return fallback;
            }

            var scrollTop = TryReadDouble(root, "scrollTop", out var scrollTopValue) ? scrollTopValue : -1;
            var clientHeight = TryReadInt(root, "clientHeight", out var clientHeightValue) ? clientHeightValue : 0;
            var requiresFallbackRescan = !structureValid || (itemCount < previousItemCount && !fullRescan);
            var expectedFirstIndex = fullRescan ? 0 : previousItemCount;
            var expectedCount = itemCount - expectedFirstIndex;
            if (expectedCount < 0 || itemsElement.GetArrayLength() != expectedCount)
            {
                return new AvitoScrollStepProbe(
                    itemCount,
                    moved,
                    atEnd,
                    [],
                    IsFullRescan: false,
                    RequiresFallbackRescan: true,
                    scrollTop,
                    clientHeight);
            }

            var items = new List<AvitoScrollStepItem>(expectedCount);
            var expectedIndex = expectedFirstIndex;
            foreach (var element in itemsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryReadInt(element, "index", out var index)
                    || index != expectedIndex
                    || !TryReadString(element, "fullName", out var fullName)
                    || !TryReadString(element, "cardFingerprint", out var fingerprint)
                    || string.IsNullOrWhiteSpace(fullName)
                    || string.IsNullOrWhiteSpace(fingerprint))
                {
                    return new AvitoScrollStepProbe(
                        itemCount,
                        moved,
                        atEnd,
                        [],
                        IsFullRescan: false,
                        RequiresFallbackRescan: true,
                        scrollTop,
                        clientHeight);
                }

                items.Add(new AvitoScrollStepItem(
                    index,
                    fullName,
                    fingerprint,
                    ReadString(element, "city"),
                    ReadString(element, "age"),
                    ReadString(element, "gender"),
                    ReadString(element, "phoneDigits")));
                expectedIndex++;
            }

            return new AvitoScrollStepProbe(
                itemCount,
                moved,
                atEnd,
                items,
                fullRescan,
                requiresFallbackRescan,
                scrollTop,
                clientHeight);
        }
        catch
        {
            return fallback;
        }
    }

    public static ScrollGeometryProbe? TryParseGeometry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadDouble(root, "scrollTop", out var scrollTop)
                || !TryReadInt(root, "clientHeight", out var clientHeight)
                || !TryReadDouble(root, "scrollHeight", out var scrollHeight))
            {
                return null;
            }

            return new ScrollGeometryProbe(scrollTop, clientHeight, scrollHeight);
        }
        catch
        {
            return null;
        }
    }

    private static AvitoScrollStepProbe Fallback(int itemCount, bool moved = false, bool atEnd = false) =>
        new(Math.Max(0, itemCount), moved, atEnd, [], IsFullRescan: false, RequiresFallbackRescan: true);

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetDouble(out value)
               && double.IsFinite(value);
    }

    private static bool TryReadBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = ReadString(root, name);
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String;
    }

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
