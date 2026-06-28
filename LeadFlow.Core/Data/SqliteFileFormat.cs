using System.IO;

namespace LeadFlow.Core.Data;

internal static class SqliteFileFormat
{
    private static ReadOnlySpan<byte> SqlitePlainHeader => "SQLite format 3\0"u8;

    /// <summary>True if the file looks like a standard (non-SQLCipher) SQLite database.</summary>
    public static bool IsUnencryptedSqliteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length < SqlitePlainHeader.Length)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[SqlitePlainHeader.Length];
            using var stream = File.OpenRead(path);
            if (stream.Read(header) != SqlitePlainHeader.Length)
            {
                return false;
            }

            return header.SequenceEqual(SqlitePlainHeader);
        }
        catch
        {
            return false;
        }
    }
}
