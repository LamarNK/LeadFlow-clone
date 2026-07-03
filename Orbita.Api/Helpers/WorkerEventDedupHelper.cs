using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Helpers;

internal static class WorkerEventDedupHelper
{
    public static string BuildFingerprint(Guid workerId, Guid? accountId, string level, string message)
    {
        var normalizedLevel = level.Trim().ToLowerInvariant();
        var normalizedMessage = message.Trim();
        var accountKey = accountId?.ToString("N") ?? string.Empty;
        var payload = $"{workerId:N}|{accountKey}|{normalizedLevel}|{normalizedMessage}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}