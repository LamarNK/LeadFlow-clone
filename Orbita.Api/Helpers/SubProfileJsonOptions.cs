using System.Text.Json;

namespace Orbita.Api.Helpers;

internal static class SubProfileJsonOptions
{
    /// <summary>
    /// SubProfilesJson in the DB may use PascalCase (LeadFlow AvitoAccount) or camelCase (telemetry).
    /// </summary>
    internal static readonly JsonSerializerOptions Deserialize = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static readonly JsonSerializerOptions Serialize = new(JsonSerializerDefaults.Web);
}