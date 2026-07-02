using System.Net;
using System.Net.Sockets;

namespace Orbita.Contracts;

public static class WorkerIpAddressRules
{
    public static bool IsUsablePublic(string? value)
    {
        if (!TryNormalize(value, out var normalized) || normalized is null)
        {
            return false;
        }

        if (!IPAddress.TryParse(normalized, out var ip))
        {
            return false;
        }

        return !IsNonPublic(ip);
    }

    public static string? ResolveForHeartbeat(string? connectionIp, string? reportedPublicIp)
    {
        if (IsUsablePublic(connectionIp))
        {
            return Normalize(connectionIp);
        }

        if (IsUsablePublic(reportedPublicIp))
        {
            return Normalize(reportedPublicIp);
        }

        return null;
    }

    public static string? Normalize(string? value) =>
        TryNormalize(value, out var normalized) ? normalized : null;

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!IPAddress.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        normalized = parsed.ToString();
        return true;
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            {
                return true;
            }
        }

        return false;
    }
}