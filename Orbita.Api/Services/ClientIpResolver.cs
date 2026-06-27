using System.Net;

namespace Orbita.Api.Services;

internal static class ClientIpResolver
{
    public static string? Resolve(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (TryNormalize(first, out var normalizedForwarded))
            {
                return normalizedForwarded;
            }
        }

        var realIp = http.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (TryNormalize(realIp, out var normalizedRealIp))
        {
            return normalizedRealIp;
        }

        return TryNormalize(http.Connection.RemoteIpAddress?.ToString(), out var remoteIp)
            ? remoteIp
            : null;
    }

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            if (parsed.IsIPv4MappedToIPv6)
            {
                parsed = parsed.MapToIPv4();
            }

            normalized = parsed.ToString();
            return true;
        }

        normalized = trimmed;
        return true;
    }
}