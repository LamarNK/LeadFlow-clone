using System.Security.Cryptography;
using System.Text;

namespace Orbita.Contracts;

public static class CandidateWatchFingerprint
{
    public static string Chat(string? chatMessagesJson) => Hash(chatMessagesJson);

    public static string Profile(
        string? city,
        string? vacancy,
        int? age,
        string? gender,
        string? vacancyUrl,
        string? citizenship,
        string? messengerUrl) =>
        Hash(string.Join(
            '\u001f',
            city?.Trim() ?? string.Empty,
            vacancy?.Trim() ?? string.Empty,
            age?.ToString() ?? string.Empty,
            gender?.Trim() ?? string.Empty,
            vacancyUrl?.Trim() ?? string.Empty,
            citizenship?.Trim() ?? string.Empty,
            messengerUrl?.Trim() ?? string.Empty));

    private static string Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))
            .ToLowerInvariant();
    }
}
