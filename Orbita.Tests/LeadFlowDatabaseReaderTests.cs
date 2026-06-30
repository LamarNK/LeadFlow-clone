using Microsoft.Data.Sqlite;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class LeadFlowDatabaseReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LeadFlowDatabaseReader _reader = new();

    public LeadFlowDatabaseReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "leadflow-test-" + Guid.NewGuid().ToString("N") + ".db");
        CreateSampleDatabase(_dbPath);
    }

    [Fact]
    public void Open_ReadsCandidateResponses_FromPlainDatabase()
    {
        var result = _reader.Open(_dbPath, encryptionKey: null);

        Assert.True(result.Success);
        Assert.False(result.IsEncrypted);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Пётр Петров", result.Rows[0].FullName);
        Assert.Equal("79004445566", result.Rows[0].PhoneNormalized);
        Assert.Contains(result.Rows, row => row.FullName == "Иван Иванов" && row.PhoneNormalized == "79001112233");
    }

    [Fact]
    public void Open_ReturnsError_WhenTableMissing()
    {
        var emptyDbPath = Path.Combine(Path.GetTempPath(), "leadflow-empty-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var connection = OpenConnection(emptyDbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Other(Id TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var result = _reader.Open(emptyDbPath, encryptionKey: null);

            Assert.False(result.Success);
            Assert.Contains("CandidateResponses", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(emptyDbPath))
            {
                File.Delete(emptyDbPath);
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static SqliteConnection OpenConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ConnectionString);

    private static void CreateSampleDatabase(string path)
    {
        using var connection = OpenConnection(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE CandidateResponses (
                Id TEXT NOT NULL PRIMARY KEY,
                AccountId TEXT NOT NULL,
                AccountName TEXT NOT NULL,
                Source TEXT NOT NULL,
                SourceResponseId TEXT NOT NULL,
                FullName TEXT NOT NULL,
                FirstName TEXT NOT NULL,
                LastName TEXT NOT NULL,
                MiddleName TEXT NOT NULL,
                Age INTEGER NULL,
                PhoneRaw TEXT NOT NULL,
                PhoneNormalized TEXT NOT NULL,
                City TEXT NOT NULL,
                Vacancy TEXT NOT NULL,
                SourceUrl TEXT NOT NULL,
                VacancyUrl TEXT NOT NULL,
                MessengerUrl TEXT NOT NULL,
                AvitoSubProfileId TEXT NOT NULL,
                Status TEXT NOT NULL,
                BitrixEntityType TEXT NOT NULL,
                BitrixEntityId TEXT NOT NULL,
                BitrixContactId TEXT NOT NULL,
                ErrorMessage TEXT NOT NULL,
                RawText TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ProcessedAt TEXT NULL
            );

            INSERT INTO CandidateResponses (
                Id, AccountId, AccountName, Source, SourceResponseId, FullName, FirstName, LastName, MiddleName,
                PhoneRaw, PhoneNormalized, City, Vacancy, SourceUrl, VacancyUrl, MessengerUrl, AvitoSubProfileId,
                Status, BitrixEntityType, BitrixEntityId, BitrixContactId, ErrorMessage, RawText, CreatedAt
            ) VALUES
            (@id1, @accountId, 'Аккаунт 1', 'Avito', 'resp-1', 'Иван Иванов', 'Иван', 'Иванов', '',
             '+7 900 111-22-33', '79001112233', 'Москва', 'Курьер', '', '', '', '',
             'Sent', 'Deal', '1001', '2001', '', '', '2026-06-01T10:00:00Z'),
            (@id2, @accountId, 'Аккаунт 1', 'Avito', 'resp-2', 'Пётр Петров', 'Пётр', 'Петров', '',
             '+7 900 444-55-66', '79004445566', 'Москва', 'Водитель', '', '', '', '',
             'Duplicate', 'Deal', '', '', 'Дубль', '', '2026-06-02T10:00:00Z');
            """;
        var accountId = Guid.NewGuid();
        command.Parameters.AddWithValue("@id1", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@id2", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@accountId", accountId.ToString());
        command.ExecuteNonQuery();

        SqliteConnection.ClearAllPools();
    }
}