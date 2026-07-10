using Microsoft.AspNetCore.Http;

namespace Orbita.Api.Auth;

/// <summary>
/// Hub URL prefixes where worker API keys may be supplied via the <c>access_token</c> query string.
/// Required for SignalR WebSocket/SSE transports: unlike negotiate HTTP requests, the WebSocket
/// upgrade cannot rely on the <c>Authorization</c> header alone.
/// </summary>
public static class WorkerHubPaths
{
    public static readonly string[] HubPrefixes =
    [
        "/hubs/captcha",
        "/hubs/browser-monitor",
        "/hubs/panel",
        "/hubs/worker"
    ];

    public static bool IsWorkerHubPath(PathString path) =>
        HubPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}