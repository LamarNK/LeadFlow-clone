using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Services;

public static class ApiKeyService
{
    public static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }

    public static bool VerifyApiKey(string apiKey, string hash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashApiKey(apiKey)),
            Encoding.UTF8.GetBytes(hash));
}