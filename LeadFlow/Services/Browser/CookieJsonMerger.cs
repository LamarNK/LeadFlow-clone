using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeadFlow.Services.Browser;

/// <summary>
/// Объединяет два JSON-массива cookie; при совпадении ключа (domain, name, path) побеждает второй массив (overlay).
/// </summary>
public static class CookieJsonMerger
{
    public static string Merge(string baseJson, string overlayJson)
    {
        var map = new Dictionary<CookieKey, JsonNode>(CookieKeyComparer.Instance);

        foreach (var el in ParseElements(baseJson))
        {
            if (TryGetKey(el, out var key))
            {
                map[key] = JsonNode.Parse(el.GetRawText())!;
            }
        }

        foreach (var el in ParseElements(overlayJson))
        {
            if (TryGetKey(el, out var key))
            {
                map[key] = JsonNode.Parse(el.GetRawText())!;
            }
        }

        var arr = new JsonArray(map.Values.OrderBy(static n => n["domain"]?.ToString()).ThenBy(static n => n["name"]?.ToString()).ToArray());
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<JsonElement> ParseElements(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                yield return el.Clone();
            }
        }
    }

    private static bool TryGetKey(JsonElement el, out CookieKey key)
    {
        key = default;
        if (el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        var domain = el.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        var path = "/";
        if (el.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        {
            var ps = p.GetString();
            if (!string.IsNullOrEmpty(ps))
            {
                path = ps;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        key = new CookieKey(domain, name, path);
        return true;
    }

    private readonly record struct CookieKey(string Domain, string Name, string Path);

    private sealed class CookieKeyComparer : IEqualityComparer<CookieKey>
    {
        public static readonly CookieKeyComparer Instance = new();

        public bool Equals(CookieKey x, CookieKey y) =>
            string.Equals(x.Domain, y.Domain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal)
            && string.Equals(x.Path, y.Path, StringComparison.Ordinal);

        public int GetHashCode(CookieKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Domain),
                StringComparer.Ordinal.GetHashCode(obj.Name),
                StringComparer.Ordinal.GetHashCode(obj.Path));
    }
}
