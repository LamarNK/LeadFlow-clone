namespace Orbita.Contracts;

/// <summary>
/// Validates the compact avatar payload sent from a worker with a candidate response.
/// The image itself, not its Avito URL, is persisted by the panel.
/// </summary>
public static class CandidateResponseAvatar
{
    public const int MaxImageBytes = 1_048_576;

    public static bool TryDecode(
        string? declaredContentType,
        string? imageBase64,
        out byte[] imageBytes,
        out string contentType)
    {
        imageBytes = [];
        contentType = string.Empty;

        if (string.IsNullOrWhiteSpace(imageBase64)
            || imageBase64.Length > GetMaxBase64Length(MaxImageBytes))
        {
            return false;
        }

        try
        {
            imageBytes = Convert.FromBase64String(imageBase64);
        }
        catch (FormatException)
        {
            imageBytes = [];
            return false;
        }

        if (imageBytes.Length == 0 || imageBytes.Length > MaxImageBytes)
        {
            imageBytes = [];
            return false;
        }

        contentType = DetectContentType(imageBytes);
        if (string.IsNullOrEmpty(contentType))
        {
            imageBytes = [];
            return false;
        }

        return string.IsNullOrWhiteSpace(declaredContentType)
            || string.Equals(NormalizeContentType(declaredContentType), contentType, StringComparison.Ordinal);
    }

    public static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47
            && bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a)
        {
            return "image/png";
        }

        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        return string.Empty;
    }

    private static int GetMaxBase64Length(int byteLength) => checked(((byteLength + 2) / 3) * 4);

    private static string NormalizeContentType(string value) =>
        value.Split(';', 2)[0].Trim().ToLowerInvariant();
}
