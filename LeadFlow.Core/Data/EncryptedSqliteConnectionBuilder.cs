using LeadFlow.Core.Models;
using Microsoft.Data.Sqlite;

namespace LeadFlow.Core.Data;

public static class EncryptedSqliteConnectionBuilder
{
    public static string BuildConnectionString(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey))
        {
            throw new InvalidOperationException("Database encryption key is not configured.");
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = settings.DatabaseEncryptionKey
        };

        return builder.ConnectionString;
    }
}
