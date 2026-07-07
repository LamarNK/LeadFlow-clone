using System.Text.Json;

namespace Orbita.Worker.Services;

internal static class WorkerSubProfileJsonOptions
{
    /// <summary>
    /// SubProfilesJson from Orbita API uses camelCase; LeadFlow models use PascalCase.
    /// </summary>
    internal static readonly JsonSerializerOptions Deserialize = new()
    {
        PropertyNameCaseInsensitive = true
    };
}