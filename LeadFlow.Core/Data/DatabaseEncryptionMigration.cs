using System.IO;
using LeadFlow.Core.Logging.Audit;
using Microsoft.Data.Sqlite;

namespace LeadFlow.Core.Data;

public static class DatabaseEncryptionMigration
{
    /// <summary>
    /// If <paramref name="databasePath"/> is a plaintext SQLite file, encrypts it in place using
    /// <paramref name="encryptionKey"/> (SQLCipher passphrase). Creates a backup <c>*.pre-encryption.bak</c> first.
    /// </summary>
    public static void MigratePlainDatabaseIfNeeded(string databasePath, string encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath) || !SqliteFileFormat.IsUnencryptedSqliteFile(databasePath))
        {
            return;
        }

        var backupPath = databasePath + ".pre-encryption.bak";
        var tempEncrypted = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            Path.GetFileName(databasePath) + ".enc-migration." + Guid.NewGuid().ToString("N") + ".tmp");

        File.Copy(databasePath, backupPath, overwrite: true);

        try
        {
            var plainBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            using (var conn = new SqliteConnection(plainBuilder.ConnectionString))
            {
                conn.Open();

                string quotedPath;
                string quotedKey;
                using (var quoteCmd = conn.CreateCommand())
                {
                    quoteCmd.CommandText = "SELECT quote($p);";
                    quoteCmd.Parameters.AddWithValue("$p", tempEncrypted);
                    quotedPath = (string)quoteCmd.ExecuteScalar()!;
                }

                using (var quoteCmd = conn.CreateCommand())
                {
                    quoteCmd.CommandText = "SELECT quote($k);";
                    quoteCmd.Parameters.AddWithValue("$k", encryptionKey);
                    quotedKey = (string)quoteCmd.ExecuteScalar()!;
                }

                using (var attachCmd = conn.CreateCommand())
                {
                    attachCmd.CommandText = $"ATTACH DATABASE {quotedPath} AS enc KEY {quotedKey};";
                    attachCmd.ExecuteNonQuery();
                }

                using (var exportCmd = conn.CreateCommand())
                {
                    exportCmd.CommandText = "SELECT sqlcipher_export('enc');";
                    exportCmd.ExecuteNonQuery();
                }

                using (var detachCmd = conn.CreateCommand())
                {
                    detachCmd.CommandText = "DETACH DATABASE enc;";
                    detachCmd.ExecuteNonQuery();
                }
            }

            File.Delete(databasePath);
            File.Move(tempEncrypted, databasePath);

            _ = GlobalLogger.Instance.LogAsync(
                "Plain SQLite database encrypted in place (SQLCipher). Backup: " + backupPath,
                DeskLinkAuditLogLevel.Info);
        }
        catch
        {
            try
            {
                if (File.Exists(tempEncrypted))
                {
                    File.Delete(tempEncrypted);
                }
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }
}
