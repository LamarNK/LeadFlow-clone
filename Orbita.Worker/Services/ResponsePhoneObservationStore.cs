using LeadFlow.Core.Services.Worker;
using Microsoft.Data.Sqlite;
using Orbita.Worker;

namespace Orbita.Worker.Services;

/// <summary>
/// SQLite-хранилище наблюдений номера по (subprofile, FIO).
/// AccountId и SourceResponseId Avito намеренно не используются — динамические.
/// </summary>
public sealed class ResponsePhoneObservationStore : IResponsePhoneObservationStore, IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;

    public ResponsePhoneObservationStore()
    {
        var path = Path.Combine(WorkerAppSettingsStore.DataDirectory, "phone-watch.db");
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
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS PhoneObservations (
                    AvitoSubProfileId TEXT NOT NULL DEFAULT '',
                    FullNameKey TEXT NOT NULL,
                    PhoneRaw TEXT NOT NULL DEFAULT '',
                    PhoneNormalized TEXT NOT NULL DEFAULT '',
                    PhoneFirstSeenUtc TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL,
                    ClosedAfterStableSend INTEGER NOT NULL DEFAULT 0,
                    LastPublishedPhoneNormalized TEXT NOT NULL DEFAULT '',
                    LastPublishedMetricKind TEXT NOT NULL DEFAULT '',
                    PublishedSourceResponseId TEXT NOT NULL DEFAULT '',
                    WatchStartedUtc TEXT NULL,
                    PRIMARY KEY (AvitoSubProfileId, FullNameKey)
                );
                CREATE INDEX IF NOT EXISTS IX_PhoneObservations_LastSeenUtc ON PhoneObservations (LastSeenUtc);
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Миграция со старой схемы, где был AccountId в PK.
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(PhoneObservations)";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Contains("AccountId"))
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText =
                """
                CREATE TABLE IF NOT EXISTS PhoneObservations_v2 (
                    AvitoSubProfileId TEXT NOT NULL DEFAULT '',
                    FullNameKey TEXT NOT NULL,
                    PhoneRaw TEXT NOT NULL DEFAULT '',
                    PhoneNormalized TEXT NOT NULL DEFAULT '',
                    PhoneFirstSeenUtc TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL,
                    ClosedAfterStableSend INTEGER NOT NULL DEFAULT 0,
                    LastPublishedPhoneNormalized TEXT NOT NULL DEFAULT '',
                    LastPublishedMetricKind TEXT NOT NULL DEFAULT '',
                    PublishedSourceResponseId TEXT NOT NULL DEFAULT '',
                    WatchStartedUtc TEXT NULL,
                    PRIMARY KEY (AvitoSubProfileId, FullNameKey)
                );
                INSERT OR IGNORE INTO PhoneObservations_v2 (
                    AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                    PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                    LastPublishedPhoneNormalized, LastPublishedMetricKind,
                    PublishedSourceResponseId, WatchStartedUtc)
                SELECT
                    AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                    PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                    LastPublishedPhoneNormalized, LastPublishedMetricKind,
                    '', NULL
                FROM PhoneObservations;
                DROP TABLE PhoneObservations;
                ALTER TABLE PhoneObservations_v2 RENAME TO PhoneObservations;
                CREATE INDEX IF NOT EXISTS IX_PhoneObservations_LastSeenUtc ON PhoneObservations (LastSeenUtc);
                """;
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!columns.Contains("PublishedSourceResponseId"))
        {
            await using var addPub = connection.CreateCommand();
            addPub.CommandText =
                "ALTER TABLE PhoneObservations ADD COLUMN PublishedSourceResponseId TEXT NOT NULL DEFAULT '';";
            await addPub.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("WatchStartedUtc"))
        {
            await using var addWatch = connection.CreateCommand();
            addWatch.CommandText =
                "ALTER TABLE PhoneObservations ADD COLUMN WatchStartedUtc TEXT NULL;";
            await addWatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ResponsePhoneObservation?> GetAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullNameKey))
        {
            return null;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                   PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                   LastPublishedPhoneNormalized, LastPublishedMetricKind,
                   PublishedSourceResponseId, WatchStartedUtc
            FROM PhoneObservations
            WHERE AvitoSubProfileId = $sub
              AND FullNameKey = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sub", (avitoSubProfileId ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$name", fullNameKey.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTime? watchStarted = null;
        if (!reader.IsDBNull(10))
        {
            var rawWatch = reader.GetString(10);
            if (!string.IsNullOrWhiteSpace(rawWatch))
            {
                watchStarted = DateTime.Parse(
                    rawWatch,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            }
        }

        return new ResponsePhoneObservation
        {
            AvitoSubProfileId = reader.GetString(0),
            FullNameKey = reader.GetString(1),
            PhoneRaw = reader.GetString(2),
            PhoneNormalized = reader.GetString(3),
            PhoneFirstSeenUtc = DateTime.Parse(
                reader.GetString(4),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            LastSeenUtc = DateTime.Parse(
                reader.GetString(5),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            ClosedAfterStableSend = reader.GetInt64(6) != 0,
            LastPublishedPhoneNormalized = reader.GetString(7),
            LastPublishedMetricKind = reader.GetString(8),
            PublishedSourceResponseId = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            WatchStartedUtc = watchStarted
        };
    }

    public async Task<bool> IsOpenWatchAsync(
        string avitoSubProfileId,
        string fullNameKey,
        CancellationToken cancellationToken = default)
    {
        var obs = await GetAsync(avitoSubProfileId, fullNameKey, cancellationToken).ConfigureAwait(false);
        return ResponsePhoneObservationWatch.IsOpen(obs);
    }

    public async Task UpsertAsync(
        ResponsePhoneObservation observation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(observation.FullNameKey))
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PhoneObservations (
                AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                LastPublishedPhoneNormalized, LastPublishedMetricKind,
                PublishedSourceResponseId, WatchStartedUtc)
            VALUES (
                $sub, $name, $phoneRaw, $phoneNorm,
                $firstSeen, $lastSeen, $closed,
                $lastPubPhone, $lastPubKind,
                $pubSourceId, $watchStarted)
            ON CONFLICT(AvitoSubProfileId, FullNameKey)
            DO UPDATE SET
                PhoneRaw = excluded.PhoneRaw,
                PhoneNormalized = excluded.PhoneNormalized,
                PhoneFirstSeenUtc = excluded.PhoneFirstSeenUtc,
                LastSeenUtc = excluded.LastSeenUtc,
                ClosedAfterStableSend = excluded.ClosedAfterStableSend,
                LastPublishedPhoneNormalized = excluded.LastPublishedPhoneNormalized,
                LastPublishedMetricKind = excluded.LastPublishedMetricKind,
                PublishedSourceResponseId = excluded.PublishedSourceResponseId,
                WatchStartedUtc = excluded.WatchStartedUtc;
            """;
        command.Parameters.AddWithValue("$sub", observation.AvitoSubProfileId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$name", observation.FullNameKey.Trim());
        command.Parameters.AddWithValue("$phoneRaw", observation.PhoneRaw ?? string.Empty);
        command.Parameters.AddWithValue("$phoneNorm", observation.PhoneNormalized ?? string.Empty);
        command.Parameters.AddWithValue(
            "$firstSeen",
            observation.PhoneFirstSeenUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$lastSeen",
            observation.LastSeenUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$closed", observation.ClosedAfterStableSend ? 1 : 0);
        command.Parameters.AddWithValue(
            "$lastPubPhone",
            observation.LastPublishedPhoneNormalized ?? string.Empty);
        command.Parameters.AddWithValue(
            "$lastPubKind",
            observation.LastPublishedMetricKind ?? string.Empty);
        command.Parameters.AddWithValue(
            "$pubSourceId",
            observation.PublishedSourceResponseId ?? string.Empty);
        command.Parameters.AddWithValue(
            "$watchStarted",
            observation.WatchStartedUtc is DateTime ws
                ? ws.ToUniversalTime().ToString("O")
                : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
