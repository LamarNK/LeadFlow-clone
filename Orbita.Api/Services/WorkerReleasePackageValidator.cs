namespace Orbita.Api.Services;

public static class WorkerReleasePackageValidator
{
    private static readonly byte[] MsiSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool LooksLikeMsi(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < MsiSignature.Length)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[MsiSignature.Length];
        var read = stream.Read(header);
        return read == MsiSignature.Length && header.SequenceEqual(MsiSignature);
    }
}