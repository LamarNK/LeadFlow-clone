using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace LeadFlow.Services.Browser;

public sealed class ProfileCookiesService : IProfileCookiesService
{
    public async Task<string> ReadCurrentProfileCookiesAsJsonAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.BrowserProfilePath))
        {
            return "[]";
        }

        var cookiesDbPath = ResolveCookiesDbPath(account.BrowserProfilePath);
        if (cookiesDbPath is null)
        {
            return "[]";
        }

        var masterKey = TryGetChromiumMasterKey(account.BrowserProfilePath);

        var tempDbPath = Path.Combine(Path.GetTempPath(), $"leadflow-cookies-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(cookiesDbPath, tempDbPath, overwrite: true);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempDbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    name,
                    value,
                    encrypted_value,
                    host_key,
                    path,
                    expires_utc,
                    is_secure,
                    is_httponly,
                    samesite
                FROM cookies
                ORDER BY host_key, name;
                """;

            var rows = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var expiresUtcValue = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5);
                var plain = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                byte[]? encBlob = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2);
                var resolved = ResolveCookieValue(plain, encBlob, masterKey);

                rows.Add(new
                {
                    name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    value = resolved,
                    domain = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    path = reader.IsDBNull(4) ? "/" : reader.GetString(4),
                    expires = ToIsoStringFromChromiumMicroseconds(expiresUtcValue),
                    secure = !reader.IsDBNull(6) && reader.GetBoolean(6),
                    httpOnly = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    sameSite = reader.IsDBNull(8) ? null : reader.GetInt32(8).ToString(CultureInfo.InvariantCulture)
                });
            }

            return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "[]";
        }
        finally
        {
            try
            {
                if (File.Exists(tempDbPath))
                {
                    File.Delete(tempDbPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string ResolveCookieValue(string plainValue, byte[]? encryptedValue, byte[]? masterKey)
    {
        if (!string.IsNullOrEmpty(plainValue))
        {
            return plainValue;
        }

        if (encryptedValue is null || encryptedValue.Length == 0)
        {
            return string.Empty;
        }

        var decrypted = TryDecryptChromiumCookie(encryptedValue, masterKey);
        return decrypted ?? string.Empty;
    }

    /// <summary>
    /// Chromium/WebView2: в БД часто пустой <c>value</c>, а смысл в <c>encrypted_value</c> (v10/v11 + AES-GCM или старый DPAPI).
    /// </summary>
    private static string? TryDecryptChromiumCookie(byte[] encryptedValue, byte[]? masterKey)
    {
        if (encryptedValue.Length >= 3
            && encryptedValue[0] == (byte)'v'
            && encryptedValue[1] == (byte)'1'
            && (encryptedValue[2] == (byte)'0' || encryptedValue[2] == (byte)'1'))
        {
            if (masterKey is null || masterKey.Length != 32)
            {
                return null;
            }

            // v10 / v11: "v1x" + 12-byte nonce + ciphertext || 16-byte GCM tag
            const int prefixLen = 3;
            const int nonceLen = 12;
            const int tagLen = 16;
            if (encryptedValue.Length <= prefixLen + nonceLen + tagLen)
            {
                return null;
            }

            var nonce = encryptedValue.AsSpan(prefixLen, nonceLen);
            var ctWithTag = encryptedValue.AsSpan(prefixLen + nonceLen);
            var cipher = ctWithTag[..^tagLen];
            var tag = ctWithTag[^tagLen..];

            try
            {
                var plaintext = new byte[cipher.Length];
                using var aes = new AesGcm(masterKey, tagLen);
                aes.Decrypt(nonce, cipher, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch
            {
                return null;
            }
        }

        // Старый формат: всё тело — DPAPI
        try
        {
            var raw = ProtectedData.Unprotect(encryptedValue, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryGetChromiumMasterKey(string profilePath)
    {
        var localStatePath = ResolveLocalStatePath(profilePath);
        if (localStatePath is null)
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt))
            {
                return null;
            }

            if (!osCrypt.TryGetProperty("encrypted_key", out var keyEl))
            {
                return null;
            }

            var b64 = keyEl.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            var bytes = Convert.FromBase64String(b64);
            // Префикс "DPAPI" (5 байт) — ключ, зашифрованный DPAPI для текущего пользователя Windows
            ReadOnlySpan<byte> keyBlob = bytes;
            if (keyBlob.Length > 5 && keyBlob[..5].SequenceEqual("DPAPI"u8))
            {
                keyBlob = keyBlob[5..];
            }

            return ProtectedData.Unprotect(keyBlob.ToArray(), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocalStatePath(string profilePath)
    {
        var candidates = new[]
        {
            Path.Combine(profilePath, "EBWebView", "Local State"),
            Path.Combine(profilePath, "Local State")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveCookiesDbPath(string profilePath)
    {
        var candidates = new[]
        {
            Path.Combine(profilePath, "EBWebView", "Default", "Network", "Cookies"),
            Path.Combine(profilePath, "Default", "Network", "Cookies"),
            Path.Combine(profilePath, "Network", "Cookies"),
            Path.Combine(profilePath, "EBWebView", "Default", "Cookies"),
            Path.Combine(profilePath, "Default", "Cookies"),
            Path.Combine(profilePath, "Cookies")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ToIsoStringFromChromiumMicroseconds(long? microsecondsSince1601)
    {
        if (!microsecondsSince1601.HasValue || microsecondsSince1601.Value <= 0)
        {
            return null;
        }

        try
        {
            var utc = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(microsecondsSince1601.Value * 10);
            return utc.ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
