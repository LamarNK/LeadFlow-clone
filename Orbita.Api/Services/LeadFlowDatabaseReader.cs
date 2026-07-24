using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Orbita.Api.Services;

public sealed class LeadFlowDatabaseReader
{
    private const string CandidateResponsesTable = "CandidateResponses";

    static LeadFlowDatabaseReader()
    {
        Batteries_V2.Init();
    }

    public LeadFlowDatabaseOpenResult Open(string dbPath, string? encryptionKey)
    {
        var isPlain = IsUnencryptedSqliteFile(dbPath);
        if (!isPlain && string.IsNullOrWhiteSpace(encryptionKey))
        {
            return LeadFlowDatabaseOpenResult.Failed(
                "База зашифрована. Укажите ключ шифрования — его выводит утилита ReadLeadFlowDatabaseKey на офисном ПК.");
        }

        try
        {
            using var connection = OpenConnection(dbPath, isPlain ? null : encryptionKey);
            connection.Open();
            if (!TableExists(connection, CandidateResponsesTable))
            {
                return LeadFlowDatabaseOpenResult.Failed("В файле нет таблицы CandidateResponses.");
            }

            var columns = GetTableColumns(connection, CandidateResponsesTable);
            var rows = ReadCandidateResponses(connection, columns);
            return LeadFlowDatabaseOpenResult.Succeeded(rows, !isPlain);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            return LeadFlowDatabaseOpenResult.Failed(
                "Не удалось открыть базу. Проверьте ключ шифрования или загрузите незашифрованный leadflow.db.");
        }
        catch (Exception ex)
        {
            return LeadFlowDatabaseOpenResult.Failed($"Не удалось прочитать базу: {ex.Message}");
        }
    }

    private static SqliteConnection OpenConnection(string dbPath, string? encryptionKey)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        if (!string.IsNullOrWhiteSpace(encryptionKey))
        {
            builder.Password = encryptionKey.Trim();
        }

        return new SqliteConnection(builder.ConnectionString);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static IReadOnlyList<LeadFlowCandidateRecord> ReadCandidateResponses(
        SqliteConnection connection,
        IReadOnlySet<string> columns)
    {
        var selectColumns = LeadFlowCandidateRecord.SqlColumns
            .Where(columns.Contains)
            .ToList();
        if (selectColumns.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selectColumns)} FROM {CandidateResponsesTable} ORDER BY CreatedAt DESC;";
        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        var rows = new List<LeadFlowCandidateRecord>();
        while (reader.Read())
        {
            rows.Add(LeadFlowCandidateRecord.Read(reader, selectColumns));
        }

        return rows;
    }

    private static bool IsUnencryptedSqliteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length
               && header.SequenceEqual("SQLite format 3\0"u8);
    }
}

public sealed class LeadFlowDatabaseOpenResult
{
    private LeadFlowDatabaseOpenResult(
        bool success,
        IReadOnlyList<LeadFlowCandidateRecord>? rows,
        bool isEncrypted,
        string? error)
    {
        Success = success;
        Rows = rows ?? [];
        IsEncrypted = isEncrypted;
        Error = error;
    }

    public bool Success { get; }
    public IReadOnlyList<LeadFlowCandidateRecord> Rows { get; }
    public bool IsEncrypted { get; }
    public string? Error { get; }

    public static LeadFlowDatabaseOpenResult Succeeded(IReadOnlyList<LeadFlowCandidateRecord> rows, bool isEncrypted) =>
        new(true, rows, isEncrypted, null);

    public static LeadFlowDatabaseOpenResult Failed(string error) =>
        new(false, null, false, error);
}

public sealed class LeadFlowCandidateRecord
{
    public static readonly string[] SqlColumns =
    [
        "Id",
        "AccountId",
        "AccountName",
        "Source",
        "SourceResponseId",
        "FullName",
        "FirstName",
        "LastName",
        "MiddleName",
        "Age",
        "PhoneRaw",
        "PhoneNormalized",
        "City",
        "Vacancy",
        "SourceUrl",
        "VacancyUrl",
        "MessengerUrl",
        "ChatMessagesJson",
        "AvitoSubProfileId",
        "AvitoSubProfileName",
        "Status",
        "BitrixEntityType",
        "BitrixEntityId",
        "BitrixContactId",
        "ErrorMessage",
        "RawText",
        "CreatedAt",
        "CollectedAt",
        "ProcessedAt"
    ];

    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceResponseId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string ChatMessagesJson { get; set; } = string.Empty;
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string AvitoSubProfileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BitrixEntityType { get; set; } = string.Empty;
    public string BitrixEntityId { get; set; } = string.Empty;
    public string BitrixContactId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }

    public bool IsValidForImport =>
        !string.IsNullOrWhiteSpace(SourceResponseId)
        && !string.IsNullOrWhiteSpace(PhoneNormalized);

    public static LeadFlowCandidateRecord Read(SqliteDataReader reader, IReadOnlyList<string> columns)
    {
        var record = new LeadFlowCandidateRecord();
        for (var i = 0; i < columns.Count; i++)
        {
            if (reader.IsDBNull(i))
            {
                continue;
            }

            switch (columns[i])
            {
                case "Id":
                    record.Id = ReadGuid(reader, i);
                    break;
                case "AccountId":
                    record.AccountId = ReadGuid(reader, i);
                    break;
                case "Age":
                    record.Age = reader.GetInt32(i);
                    break;
                case "CreatedAt":
                    record.CreatedAt = ReadDateTime(reader, i);
                    break;
                case "CollectedAt":
                    record.CollectedAt = ReadDateTime(reader, i);
                    break;
                case "ProcessedAt":
                    record.ProcessedAt = ReadDateTime(reader, i);
                    break;
                default:
                    SetStringProperty(record, columns[i], reader.GetString(i));
                    break;
            }
        }

        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        if (record.CollectedAt == default)
        {
            record.CollectedAt = record.CreatedAt;
        }

        return record;
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid guid => guid,
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => Guid.Empty
        };
    }

    private static DateTime ReadDateTime(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dt => ToUtc(dt),
            string text when DateTime.TryParse(text, out var parsed) => ToUtc(parsed),
            long ticks => new DateTime(ticks, DateTimeKind.Utc),
            double oaDate => ToUtc(DateTime.FromOADate(oaDate)),
            _ => DateTime.UtcNow
        };
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void SetStringProperty(LeadFlowCandidateRecord record, string column, string value)
    {
        switch (column)
        {
            case "AccountName": record.AccountName = value; break;
            case "Source": record.Source = value; break;
            case "SourceResponseId": record.SourceResponseId = value; break;
            case "FullName": record.FullName = value; break;
            case "FirstName": record.FirstName = value; break;
            case "LastName": record.LastName = value; break;
            case "MiddleName": record.MiddleName = value; break;
            case "PhoneRaw": record.PhoneRaw = value; break;
            case "PhoneNormalized": record.PhoneNormalized = value; break;
            case "City": record.City = value; break;
            case "Vacancy": record.Vacancy = value; break;
            case "SourceUrl": record.SourceUrl = value; break;
            case "VacancyUrl": record.VacancyUrl = value; break;
            case "MessengerUrl": record.MessengerUrl = value; break;
            case "ChatMessagesJson": record.ChatMessagesJson = value; break;
            case "AvitoSubProfileId": record.AvitoSubProfileId = value; break;
            case "AvitoSubProfileName": record.AvitoSubProfileName = value; break;
            case "Status": record.Status = value; break;
            case "BitrixEntityType": record.BitrixEntityType = value; break;
            case "BitrixEntityId": record.BitrixEntityId = value; break;
            case "BitrixContactId": record.BitrixContactId = value; break;
            case "ErrorMessage": record.ErrorMessage = value; break;
            case "RawText": record.RawText = value; break;
        }
    }
}