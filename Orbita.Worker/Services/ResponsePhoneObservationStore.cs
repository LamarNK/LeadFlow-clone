using LeadFlow.Core.Services.Worker;
using Microsoft.Data.Sqlite;
using Orbita.Worker;

namespace Orbita.Worker.Services;

/// <summary>
/// SQLite-хранилище наблюдений номера по (subprofile, FIO).
/// AccountId и SourceResponseId намеренно не используются — динамические.
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
        // Новая схема: PK (AvitoSubProfileId, FullNameKey) — без AccountId.
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
                    PRIMARY KEY (AvitoSubProfileId, FullNameKey)
                );
                CREATE INDEX IF NOT EXISTS IX_PhoneObservations_LastSeenUtc ON PhoneObservations (LastSeenUtc);
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Миграция со старой схемы, где был AccountId в PK.
        var hasAccountIdColumn = false;
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(PhoneObservations)";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "AccountId", StringComparison.Ordinal))
                {
                    hasAccountIdColumn = true;
                    break;
                }
            }
        }

        if (!hasAccountIdColumn)
        {
            return;
        }

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
                PRIMARY KEY (AvitoSubProfileId, FullNameKey)
            );
            INSERT OR IGNORE INTO PhoneObservations_v2 (
                AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                LastPublishedPhoneNormalized, LastPublishedMetricKind)
            SELECT
                AvitoSubProfileId, FullNameKey, PhoneRaw, PhoneNormalized,
                PhoneFirstSeenUtc, LastSeenUtc, ClosedAfterStableSend,
                LastPublishedPhoneNormalized, LastPublishedMetricKind
            FROM PhoneObservations;
            DROP TABLE PhoneObservations;
            ALTER TABLE PhoneObservations_v2 RENAME TO PhoneObservations;
            CREATE INDEX IF NOT EXISTS IX_PhoneObservations_LastSeenUtc ON PhoneObservations (LastSeenUtc);
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                   LastPublishedPhoneNormalized, LastPublishedMetricKind
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
            LastPublishedMetricKind = reader.GetString(8)
        };
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
                LastPublishedPhoneNormalized, LastPublishedMetricKind)
            VALUES (
                $sub, $name, $phoneRaw, $phoneNorm,
                $firstSeen, $lastSeen, $closed,
                $lastPubPhone, $lastPubKind)
            ON CONFLICT(AvitoSubProfileId, FullNameKey)
            DO UPDATE SET
                PhoneRaw = excluded.PhoneRaw,
                PhoneNormalized = excluded.PhoneNormalized,
                PhoneFirstSeenUtc = excluded.PhoneFirstSeenUtc,
                LastSeenUtc = excluded.LastSeenUtc,
                ClosedAfterStableSend = excluded.ClosedAfterStableSend,
                LastPublishedPhoneNormalized = excluded.LastPublishedPhoneNormalized,
                LastPublishedMetricKind = excluded.LastPublishedMetricKind;
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
