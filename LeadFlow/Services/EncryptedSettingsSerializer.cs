using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LeadFlow.Services;

internal static class EncryptedSettingsSerializer
{
    private const int FileFormatVersion = 1;
    private const int MagicLength = 4;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultPbkdf2Iterations = 100_000;
    private static ReadOnlySpan<byte> Magic => "LFST"u8;
    private static ReadOnlySpan<byte> NoPasswordMarker => "LeadFlow.Settings.NoPassword.v1"u8;

    public static void EncryptToStream(Stream output, byte[] plain, string applicationMasterKey, string? userPassword)
    {
        Span<byte> dek = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(dek);

        Span<byte> payloadNonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(payloadNonce);
        var payloadCipher = new byte[plain.Length];
        var payloadTag = new byte[TagSize];

        using (var aes = new AesGcm(dek, TagSize))
        {
            aes.Encrypt(payloadNonce, plain, payloadCipher, payloadTag);
        }

        Span<byte> wrapSalt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(wrapSalt);
        Span<byte> wrapNonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(wrapNonce);
        var wrappingKey = DeriveWrappingKey(applicationMasterKey, userPassword, wrapSalt, DefaultPbkdf2Iterations);

        var wrappedDek = new byte[KeySize];
        var wrapTag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(wrappingKey, TagSize);
            aes.Encrypt(wrapNonce, dek, wrappedDek, wrapTag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(wrappingKey);
        }

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FileFormatVersion);
        writer.Write(!string.IsNullOrEmpty(userPassword));
        writer.Write((ushort)wrapSalt.Length);
        writer.Write(wrapSalt);
        writer.Write(DefaultPbkdf2Iterations);
        writer.Write(payloadNonce);
        writer.Write(payloadCipher.Length);
        writer.Write(payloadCipher);
        writer.Write(payloadTag);
        writer.Write(wrapNonce);
        writer.Write(wrappedDek);
        writer.Write(wrapTag);
    }

    public static byte[] DecryptFromStream(Stream input, string applicationMasterKey, string? userPassword)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(MagicLength);
        if (magic.Length != MagicLength || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid LeadFlow settings file signature.");
        }

        var version = reader.ReadInt32();
        if (version != FileFormatVersion)
        {
            throw new InvalidDataException("Unsupported LeadFlow settings file version.");
        }

        var hasPassword = reader.ReadBoolean();
        if (hasPassword && string.IsNullOrEmpty(userPassword))
        {
            throw new InvalidDataException("Password-protected settings are not supported.");
        }

        var saltLen = reader.ReadUInt16();
        var wrapSalt = reader.ReadBytes(saltLen);
        var iterations = reader.ReadInt32();
        var payloadNonce = reader.ReadBytes(NonceSize);
        var cipherLen = reader.ReadInt32();
        var cipher = reader.ReadBytes(cipherLen);
        var payloadTag = reader.ReadBytes(TagSize);
        var wrapNonce = reader.ReadBytes(NonceSize);
        var wrappedDek = reader.ReadBytes(KeySize);
        var wrapTag = reader.ReadBytes(TagSize);

        var wrappingKey = DeriveWrappingKey(applicationMasterKey, userPassword, wrapSalt, iterations);
        Span<byte> dek = stackalloc byte[KeySize];
        try
        {
            using (var aes = new AesGcm(wrappingKey, TagSize))
            {
                aes.Decrypt(wrapNonce, wrappedDek, wrapTag, dek);
            }

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(dek, TagSize))
            {
                aes.Decrypt(payloadNonce, cipher, payloadTag, plain);
            }

            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public static bool LooksEncrypted(Stream input)
    {
        var originalPosition = input.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[MagicLength];
            var read = input.Read(buffer);
            return read == MagicLength && buffer.SequenceEqual(Magic);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    private static byte[] DeriveWrappingKey(string applicationMasterKey, string? userPassword, ReadOnlySpan<byte> salt, int iterations)
    {
        var appComponent = new byte[KeySize];
        var passwordComponent = new byte[KeySize];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(applicationMasterKey, salt, appComponent, iterations, HashAlgorithmName.SHA256);
            if (string.IsNullOrEmpty(userPassword))
            {
                SHA256.HashData(NoPasswordMarker, passwordComponent);
            }
            else
            {
                Rfc2898DeriveBytes.Pbkdf2(userPassword, salt, passwordComponent, iterations, HashAlgorithmName.SHA256);
            }

            return HMACSHA256.HashData(appComponent, passwordComponent);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(appComponent);
            CryptographicOperations.ZeroMemory(passwordComponent);
        }
    }
}
