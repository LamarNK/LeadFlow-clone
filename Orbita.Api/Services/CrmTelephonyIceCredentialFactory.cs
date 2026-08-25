using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Services;

public static class CrmTelephonyIceCredentialFactory
{
    public static (string Username, string Credential) Create(
        string authSecret,
        string subject,
        DateTimeOffset now,
        int ttlSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var effectiveTtlSeconds = Math.Clamp(ttlSeconds, 300, 86400);
        var username = $"{now.ToUnixTimeSeconds() + effectiveTtlSeconds}:{subject.Trim()}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authSecret));
        var credential = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
        return (username, credential);
    }
}
