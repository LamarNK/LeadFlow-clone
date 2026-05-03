using System.Text;

namespace LeadFlow.Services;

internal static class SettingsEncryptionKeyHelper
{
    internal static string GetDefaultKey()
    {
        var segments = new byte[][]
        {
            new byte[] { 0x4C, 0x65, 0x61, 0x64, 0x46, 0x6C, 0x6F, 0x77 },
            new byte[] { 0x3A, 0x3A, 0x53, 0x65, 0x74, 0x74, 0x69, 0x6E, 0x67, 0x73 },
            new byte[] { 0x3A, 0x3A, 0x4D, 0x61, 0x73, 0x74, 0x65, 0x72, 0x4B, 0x65, 0x79 },
            new byte[] { 0x3A, 0x3A, 0x32, 0x30, 0x32, 0x36, 0x21 }
        };

        var totalSize = 0;
        foreach (var segment in segments)
        {
            totalSize += segment.Length;
        }

        var keyBytes = new byte[totalSize];
        var offset = 0;
        foreach (var segment in segments)
        {
            Buffer.BlockCopy(segment, 0, keyBytes, offset, segment.Length);
            offset += segment.Length;
        }

        return Encoding.UTF8.GetString(keyBytes);
    }
}
