using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Auth;

public static class WorkerReleasePublishTokenValidator
{
    public static bool IsConfigured(string? expectedToken) => !string.IsNullOrWhiteSpace(expectedToken);

    public static bool IsValid(string? providedToken, string? expectedToken)
    {
        if (string.IsNullOrWhiteSpace(providedToken) || string.IsNullOrWhiteSpace(expectedToken))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(providedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return provided.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
