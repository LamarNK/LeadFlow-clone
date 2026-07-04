using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbita.Contracts;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class WorkerCandidateOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private bool _initialized;

    public WorkerCandidateOutbox()
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
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS CandidateOutbox (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PayloadJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task EnqueueAsync(WorkerCandidateBatchRequest batch, CancellationToken cancellationToken = default)
    {
        if (batch.Candidates.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CandidateOutbox (PayloadJson, CreatedAtUtc, AttemptCount)
            VALUES ($payload, $createdAtUtc, 0);
            """;
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(batch, JsonOptions));
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int take, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, PayloadJson, AttemptCount
            FROM CandidateOutbox
            ORDER BY Id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);

        var entries = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payloadJson = reader.GetString(1);
            var batch = JsonSerializer.Deserialize<WorkerCandidateBatchRequest>(payloadJson, JsonOptions);
            if (batch is null || batch.Candidates.Count == 0)
            {
                continue;
            }

            entries.Add(new OutboxEntry(reader.GetInt64(0), batch, reader.GetInt32(2)));
        }

        return entries;
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CandidateOutbox WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task IncrementAttemptAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE CandidateOutbox SET AttemptCount = AttemptCount + 1 WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public sealed record OutboxEntry(long Id, WorkerCandidateBatchRequest Batch, int AttemptCount);
}