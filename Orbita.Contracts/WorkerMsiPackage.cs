namespace Orbita.Contracts;

public static class WorkerMsiPackage
{
    public static readonly byte[] OleCompoundSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool LooksLikeMsi(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < OleCompoundSignature.Length)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[OleCompoundSignature.Length];
            var read = stream.Read(header);
            return read == OleCompoundSignature.Length && header.SequenceEqual(OleCompoundSignature);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCorruptPackageExitCode(int exitCode) => exitCode is 1619 or 1620;
}
