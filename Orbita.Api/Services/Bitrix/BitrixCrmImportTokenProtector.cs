using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Orbita.Api.Services.Bitrix;

public sealed record BitrixCrmFileImportTokenPayload(
    Guid BitrixInstanceId,
    Guid OfficeId,
    string PortalHost,
    int CategoryId,
    DateTime ExpiresAtUtc,
    BitrixImportSnapshot Snapshot);

public sealed class BitrixCrmImportTokenProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public BitrixCrmImportTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Orbita.BitrixCrmFileImport.v1");
    }

    public string Protect(BitrixCrmFileImportTokenPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return Convert.ToBase64String(_protector.Protect(buffer.ToArray()));
    }

    public bool TryUnprotect(string? token, out BitrixCrmFileImportTokenPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var compressed = _protector.Unprotect(Convert.FromBase64String(token));
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            payload = JsonSerializer.Deserialize<BitrixCrmFileImportTokenPayload>(gzip, JsonOptions);
            return payload is not null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or InvalidDataException or JsonException)
        {
            return false;
        }
    }
}
