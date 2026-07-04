using LeadFlow.Core.Models;
using Microsoft.Data.Sqlite;
using Orbita.Worker;

namespace Orbita.Worker.Services;

/// <summary>
/// Short-lived local dedup cache when Orbita API is unreachable (resilient offline mode).
/// </summary>
public sealed class EphemeralDedupCache : IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;

    public EphemeralDedupCache()
    {
        var path = Path.Combine(WorkerAppSettingsStore.DataDirectory, "cache.db");
        Directory.CreateDirectory(WorkerAppSettingsStore.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS DedupCache (
                    AccountId TEXT NOT NULL,
                    AvitoSubProfileId TEXT NOT NULL DEFAULT '',
                    SourceResponseId TEXT NOT NULL DEFAULT '',
                    PhoneNormalized TEXT NOT NULL DEFAULT '',
                    SeenAtUtc TEXT NOT NULL,
                    PRIMARY KEY (AccountId, AvitoSubProfileId, SourceResponseId, PhoneNormalized)
                );
                CREATE INDEX IF NOT EXISTS IX_DedupCache_SeenAtUtc ON DedupCache (SeenAtUtc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateLegacySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private static async Task MigrateLegacySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasSubProfileColumn = false;
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(DedupCache)";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "AvitoSubProfileId", StringComparison.Ordinal))
                {
                    hasSubProfileColumn = true;
                    break;
                }
            }
        }

        if (hasSubProfileColumn)
        {
            return;
        }

        await using var migrate = connection.CreateCommand();
        migrate.CommandText =
            """
            CREATE TABLE IF NOT EXISTS DedupCache_v2 (
                AccountId TEXT NOT NULL,
                AvitoSubProfileId TEXT NOT NULL DEFAULT '',
                SourceResponseId TEXT NOT NULL DEFAULT '',
                PhoneNormalized TEXT NOT NULL DEFAULT '',
                SeenAtUtc TEXT NOT NULL,
                PRIMARY KEY (AccountId, AvitoSubProfileId, SourceResponseId, PhoneNormalized)
            );
            INSERT OR IGNORE INTO DedupCache_v2 (AccountId, AvitoSubProfileId, SourceResponseId, PhoneNormalized, SeenAtUtc)
            SELECT AccountId, '', SourceResponseId, PhoneNormalized, SeenAtUtc FROM DedupCache;
            DROP TABLE DedupCache;
            ALTER TABLE DedupCache_v2 RENAME TO DedupCache;
            CREATE INDEX IF NOT EXISTS IX_DedupCache_SeenAtUtc ON DedupCache (SeenAtUtc);
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAsync(
        Guid accountId,
        string? sourceResponseId,
        string? phoneNormalized,
        DateTime seenAtUtc,
        string? avitoSubProfileId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceResponseId) && string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DedupCache (AccountId, AvitoSubProfileId, SourceResponseId, PhoneNormalized, SeenAtUtc)
            VALUES ($accountId, $avitoSubProfileId, $sourceResponseId, $phoneNormalized, $seenAtUtc)
            ON CONFLICT(AccountId, AvitoSubProfileId, SourceResponseId, PhoneNormalized)
            DO UPDATE SET SeenAtUtc = excluded.SeenAtUtc;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$avitoSubProfileId", avitoSubProfileId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$sourceResponseId", sourceResponseId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$phoneNormalized", phoneNormalized ?? string.Empty);
        command.Parameters.AddWithValue("$seenAtUtc", seenAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(HashSet<string> SourceIds, HashSet<string> Phones)> LookupAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        IEnumerable<string> phoneNormalized,
        DuplicateScope scope,
        CancellationToken cancellationToken = default,
        string? avitoSubProfileId = null)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var sourceIds = sourceResponseIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var phones = phoneNormalized
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sourceIds.Length == 0 && phones.Length == 0)
        {
            return ([], []);
        }

        var existingSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingPhones = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (sourceIds.Length > 0)
        {
            await using var command = connection.CreateCommand();
            var parameters = new List<string>();
            for (var i = 0; i < sourceIds.Length; i++)
            {
                var name = $"$s{i}";
                parameters.Add(name);
                command.Parameters.AddWithValue(name, sourceIds[i]);
            }

            command.CommandText =
                $"""
                 SELECT SourceResponseId FROM DedupCache
                 WHERE AccountId = $accountId AND SourceResponseId IN ({string.Join(", ", parameters)})
                 """;
            command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingSourceIds.Add(reader.GetString(0));
            }
        }

        if (phones.Length > 0)
        {
            await using var command = connection.CreateCommand();
            var parameters = new List<string>();
            for (var i = 0; i < phones.Length; i++)
            {
                var name = $"$p{i}";
                parameters.Add(name);
                command.Parameters.AddWithValue(name, phones[i]);
            }

            var subProfileId = avitoSubProfileId?.Trim();
            if (!string.IsNullOrWhiteSpace(subProfileId))
            {
                command.CommandText =
                    $"""
                     SELECT DISTINCT PhoneNormalized FROM DedupCache
                     WHERE AccountId = $accountId AND AvitoSubProfileId = $avitoSubProfileId
                       AND PhoneNormalized IN ({string.Join(", ", parameters)})
                     """;
                command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
                command.Parameters.AddWithValue("$avitoSubProfileId", subProfileId);
            }
            else if (scope == DuplicateScope.PerAvitoAccount)
            {
                command.CommandText =
                    $"""
                     SELECT DISTINCT PhoneNormalized FROM DedupCache
                     WHERE AccountId = $accountId AND PhoneNormalized IN ({string.Join(", ", parameters)})
                     """;
                command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            }
            else
            {
                command.CommandText =
                    $"""
                     SELECT DISTINCT PhoneNormalized FROM DedupCache
                     WHERE PhoneNormalized IN ({string.Join(", ", parameters)})
                     """;
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingPhones.Add(reader.GetString(0));
            }
        }

        return (existingSourceIds, existingPhones);
    }

    public async Task<int> PruneExpiredAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = utcNow.AddDays(-WorkerMaintenanceOptions.DedupCacheRetentionDays).ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DedupCache WHERE SeenAtUtc < $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}