using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class LeadFlowImportService(
    OrbitaDbContext db,
    LeadFlowDatabaseReader databaseReader,
    PhoneNormalizer phoneNormalizer,
    IOptions<LeadFlowImportOptions> options)
{
    private const string SessionManifestFileName = "session.json";
    private const string DatabaseFileName = "leadflow.db";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LeadFlowImportOptions _options = options.Value;

    public async Task<(LeadFlowImportPreviewDto? Result, string? Error)> PreviewAsync(
        Stream dbStream,
        string fileName,
        Guid officeId,
        string? encryptionKey,
        CancellationToken ct = default)
    {
        await CleanupExpiredSessionsAsync(ct);

        var office = await db.Offices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == officeId && x.IsEnabled, ct);
        if (office is null)
        {
            return (null, "Офис не найден или отключён.");
        }

        if (dbStream.Length > _options.MaxUploadBytes)
        {
            return (null, $"Файл слишком большой. Максимум {_options.MaxUploadBytes / (1024 * 1024)} МБ.");
        }

        if (!LooksLikeLeadFlowDatabase(fileName))
        {
            return (null, "Ожидается файл leadflow.db из LeadFlow.");
        }

        var sessionId = Guid.NewGuid();
        var sessionDir = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);
        var dbPath = Path.Combine(sessionDir, DatabaseFileName);

        try
        {
            await using (var fileStream = File.Create(dbPath))
            {
                await dbStream.CopyToAsync(fileStream, ct);
            }

            var openResult = databaseReader.Open(dbPath, encryptionKey);
            if (!openResult.Success)
            {
                Directory.Delete(sessionDir, recursive: true);
                return (null, openResult.Error);
            }

            var existingKeys = await LoadExistingKeysAsync(ct);
            var existingIds = await LoadExistingIdsAsync(ct);
            var analyzed = AnalyzeRows(
                openResult.Rows,
                new HashSet<string>(existingKeys, StringComparer.Ordinal),
                new HashSet<Guid>(existingIds));
            var previewItems = analyzed
                .Where(x => x.CanImport)
                .Take(_options.PreviewItemLimit)
                .Select(MapPreviewItem)
                .ToList();

            var session = new LeadFlowImportSession
            {
                SessionId = sessionId,
                OfficeId = officeId,
                DbPath = dbPath,
                EncryptionKey = string.IsNullOrWhiteSpace(encryptionKey) ? null : encryptionKey.Trim(),
                CreatedAtUtc = DateTime.UtcNow
            };
            await File.WriteAllTextAsync(
                Path.Combine(sessionDir, SessionManifestFileName),
                JsonSerializer.Serialize(session, JsonOptions),
                ct);

            return (new LeadFlowImportPreviewDto(
                sessionId,
                officeId,
                office.Name,
                analyzed.Count,
                analyzed.Count(x => x.CanImport),
                analyzed.Count(x => x.ImportState == LeadFlowImportStates.AlreadyExists),
                analyzed.Count(x => x.ImportState == LeadFlowImportStates.Invalid),
                openResult.IsEncrypted,
                previewItems.Count,
                previewItems), null);
        }
        catch
        {
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }

            throw;
        }
    }

    public async Task<(LeadFlowImportExecuteResultDto? Result, string? Error)> ExecuteAsync(
        Guid sessionId,
        IReadOnlyList<Guid>? selectedIds,
        CancellationToken ct = default)
    {
        var session = await LoadSessionAsync(sessionId, ct);
        if (session is null)
        {
            return (null, "Сессия импорта не найдена или истекла. Загрузите базу снова.");
        }

        var office = await db.Offices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == session.OfficeId && x.IsEnabled, ct);
        if (office is null)
        {
            return (null, "Офис не найден или отключён.");
        }

        var openResult = databaseReader.Open(session.DbPath, session.EncryptionKey);
        if (!openResult.Success)
        {
            return (null, openResult.Error);
        }

        var existingKeys = await LoadExistingKeysAsync(ct);
        var existingIds = await LoadExistingIdsAsync(ct);
        var analyzed = AnalyzeRows(
            openResult.Rows,
            new HashSet<string>(existingKeys, StringComparer.Ordinal),
            new HashSet<Guid>(existingIds));
        var selectedSet = selectedIds is { Count: > 0 }
            ? selectedIds.ToHashSet()
            : null;

        var toImport = analyzed
            .Where(x => x.CanImport)
            .Where(x => selectedSet is null || selectedSet.Contains(x.Record.Id))
            .Select(x => x.Record)
            .ToList();

        if (toImport.Count == 0)
        {
            return (new LeadFlowImportExecuteResultDto(0, 0, 0, 0), null);
        }

        var worker = await ResolveImportWorkerAsync(session.OfficeId, ct);
        var imported = 0;
        var failed = 0;
        var skipped = 0;

        var importKeys = new HashSet<string>(existingKeys, StringComparer.Ordinal);
        var importIds = new HashSet<Guid>(existingIds);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var record in toImport)
            {
                var key = BuildKey(record.AccountId, record.SourceResponseId);
                if (importKeys.Contains(key) || importIds.Contains(record.Id))
                {
                    skipped++;
                    continue;
                }

                var entity = MapEntity(record, session.OfficeId, worker);
                db.CandidateResponses.Add(entity);
                await db.Database.ExecuteSqlRawAsync("SAVEPOINT leadflow_import_row", ct);
                try
                {
                    await db.SaveChangesAsync(ct);
                    await db.Database.ExecuteSqlRawAsync("RELEASE SAVEPOINT leadflow_import_row", ct);
                    importKeys.Add(key);
                    importIds.Add(record.Id);
                    imported++;
                }
                catch (DbUpdateException ex)
                {
                    await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT leadflow_import_row", ct);
                    db.Entry(entity).State = EntityState.Detached;
                    if (imported == 0 && failed == 0)
                    {
                        throw new InvalidOperationException(
                            $"Не удалось импортировать первый отклик: {ex.InnerException?.Message ?? ex.Message}",
                            ex);
                    }

                    failed++;
                }
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            DeleteSessionDirectory(sessionId);
        }

        if (imported > 0)
        {
            await CleanupImportWorkerAsync(session.OfficeId, ct);
        }

        var requested = selectedSet?.Count ?? analyzed.Count(x => x.CanImport);
        return (new LeadFlowImportExecuteResultDto(requested, imported, skipped, failed), null);
    }

    private async Task<WorkerEntity> ResolveImportWorkerAsync(Guid officeId, CancellationToken ct)
    {
        var officeWorker = await db.Workers
            .Where(x => x.OfficeId == officeId && x.MachineName != LeadFlowImportWorker.MachineName)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (officeWorker is not null)
        {
            return officeWorker;
        }

        return await GetOrCreateImportWorkerAsync(officeId, ct);
    }

    private async Task CleanupImportWorkerAsync(Guid officeId, CancellationToken ct)
    {
        var importWorker = await db.Workers
            .FirstOrDefaultAsync(
                x => x.OfficeId == officeId && x.MachineName == LeadFlowImportWorker.MachineName,
                ct);
        if (importWorker is null)
        {
            return;
        }

        await db.CandidateResponses
            .Where(x => x.WorkerId == importWorker.Id && x.WorkerName == string.Empty)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.WorkerName, importWorker.DisplayName),
                ct);
        await db.CandidateResponses
            .Where(x => x.WorkerId == importWorker.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.WorkerId, (Guid?)null), ct);
        await db.WorkerLogEntries.Where(x => x.WorkerId == importWorker.Id).ExecuteDeleteAsync(ct);
        await db.WorkerEvents.Where(x => x.WorkerId == importWorker.Id).ExecuteDeleteAsync(ct);
        await db.WorkerAccounts.Where(x => x.WorkerId == importWorker.Id).ExecuteDeleteAsync(ct);
        await db.WorkerSnapshots.Where(x => x.WorkerId == importWorker.Id).ExecuteDeleteAsync(ct);
        await db.WorkerDiagnosticAttachments.Where(x => x.WorkerId == importWorker.Id).ExecuteDeleteAsync(ct);
        db.Workers.Remove(importWorker);
        await db.SaveChangesAsync(ct);
    }

    private async Task<WorkerEntity> GetOrCreateImportWorkerAsync(Guid officeId, CancellationToken ct)
    {
        var existing = await db.Workers
            .FirstOrDefaultAsync(
                x => x.OfficeId == officeId && x.MachineName == LeadFlowImportWorker.MachineName,
                ct);
        if (existing is not null)
        {
            return existing;
        }

        var worker = new WorkerEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            DisplayName = LeadFlowImportWorker.DisplayName,
            MachineName = LeadFlowImportWorker.MachineName,
            ApiKeyHash = ApiKeyService.HashApiKey(ApiKeyService.GenerateApiKey()),
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = string.Empty,
            IsEnabled = false,
            IsMonitoringActive = false,
            MonitoringStatus = "Stopped",
            ActivityActiveAccountsJson = "[]"
        };
        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
        return worker;
    }

    private bool IsValidForImport(LeadFlowCandidateRecord record) =>
        !string.IsNullOrWhiteSpace(record.SourceResponseId)
        && !string.IsNullOrWhiteSpace(ResolveImportedPhoneNormalized(record));

    private string ResolveImportedPhoneNormalized(LeadFlowCandidateRecord record)
    {
        var source = !string.IsNullOrWhiteSpace(record.PhoneRaw)
            ? record.PhoneRaw
            : record.PhoneNormalized;
        return phoneNormalizer.Normalize(source);
    }

    private CandidateResponseEntity MapEntity(
        LeadFlowCandidateRecord record,
        Guid officeId,
        WorkerEntity worker)
    {
        var status = string.IsNullOrWhiteSpace(record.Status)
            ? ResponseStatuses.Sent
            : record.Status;
        var isDuplicate = status == ResponseStatuses.Duplicate;

        return new CandidateResponseEntity
        {
            Id = record.Id,
            OfficeId = officeId,
            WorkerId = worker.Id,
            WorkerName = worker.DisplayName,
            AccountId = record.AccountId,
            AccountName = record.AccountName,
            Source = string.IsNullOrWhiteSpace(record.Source) ? "Avito" : record.Source,
            SourceResponseId = record.SourceResponseId.Trim(),
            FullName = record.FullName,
            FirstName = record.FirstName,
            LastName = record.LastName,
            MiddleName = record.MiddleName,
            Age = record.Age,
            PhoneRaw = record.PhoneRaw,
            PhoneNormalized = ResolveImportedPhoneNormalized(record),
            City = record.City,
            Vacancy = record.Vacancy,
            SourceUrl = record.SourceUrl,
            VacancyUrl = record.VacancyUrl,
            MessengerUrl = record.MessengerUrl,
            ChatMessagesJson = record.ChatMessagesJson,
            AvitoSubProfileId = record.AvitoSubProfileId,
            AvitoSubProfileName = record.AvitoSubProfileName,
            Status = status,
            IsLocalDuplicate = isDuplicate,
            IsBitrixDuplicate = isDuplicate,
            DuplicateSummary = isDuplicate ? "Импортировано как дубль из LeadFlow." : string.Empty,
            BitrixEntityType = record.BitrixEntityType,
            BitrixEntityId = record.BitrixEntityId,
            BitrixContactId = record.BitrixContactId,
            ErrorMessage = record.ErrorMessage,
            RawText = record.RawText,
            CreatedAt = EnsureUtc(record.CreatedAt),
            ProcessedAt = record.ProcessedAt is null ? null : EnsureUtc(record.ProcessedAt.Value)
        };
    }

    private static DateTime EnsureUtc(DateTime value) =>
        value == default
            ? DateTime.UtcNow
            : value.Kind == DateTimeKind.Utc
                ? value
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<HashSet<string>> LoadExistingKeysAsync(CancellationToken ct)
    {
        var keys = await db.CandidateResponses.AsNoTracking()
            .Where(x => x.SourceResponseId != string.Empty && x.SourceResponseId.Trim() != string.Empty)
            .Select(x => new { x.AccountId, x.SourceResponseId })
            .ToListAsync(ct);

        return keys
            .Select(x => BuildKey(x.AccountId, x.SourceResponseId))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<HashSet<Guid>> LoadExistingIdsAsync(CancellationToken ct)
    {
        var ids = await db.CandidateResponses.AsNoTracking()
            .Select(x => x.Id)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    private List<AnalyzedLeadFlowRow> AnalyzeRows(
        IReadOnlyList<LeadFlowCandidateRecord> rows,
        HashSet<string> knownKeys,
        HashSet<Guid> knownIds)
    {
        var analyzed = new List<AnalyzedLeadFlowRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!IsValidForImport(row))
            {
                analyzed.Add(new AnalyzedLeadFlowRow(row, false, LeadFlowImportStates.Invalid, null));
                continue;
            }

            var key = BuildKey(row.AccountId, row.SourceResponseId.Trim());
            if (knownKeys.Contains(key) || knownIds.Contains(row.Id))
            {
                analyzed.Add(new AnalyzedLeadFlowRow(row, false, LeadFlowImportStates.AlreadyExists, null));
                continue;
            }

            knownKeys.Add(key);
            knownIds.Add(row.Id);
            analyzed.Add(new AnalyzedLeadFlowRow(row, true, LeadFlowImportStates.New, null));
        }

        return analyzed;
    }

    private static LeadFlowImportPreviewItemDto MapPreviewItem(AnalyzedLeadFlowRow row) =>
        new(
            row.Record.Id,
            row.Record.FullName,
            row.Record.PhoneRaw,
            row.Record.AccountName,
            row.Record.Vacancy,
            row.Record.Status,
            row.Record.CreatedAt,
            row.CanImport,
            row.ImportState,
            row.ExistingStatus);

    private static string BuildKey(Guid accountId, string sourceResponseId) =>
        $"{accountId:N}|{sourceResponseId.Trim()}";

    private static bool LooksLikeLeadFlowDatabase(string fileName) =>
        fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("leadflow", StringComparison.OrdinalIgnoreCase);

    private string GetSessionDirectory(Guid sessionId) =>
        Path.Combine(Path.GetFullPath(_options.DataPath), sessionId.ToString("N"));

    private async Task<LeadFlowImportSession?> LoadSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var manifestPath = Path.Combine(GetSessionDirectory(sessionId), SessionManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var session = JsonSerializer.Deserialize<LeadFlowImportSession>(
            await File.ReadAllTextAsync(manifestPath, ct),
            JsonOptions);
        if (session is null || !File.Exists(session.DbPath))
        {
            return null;
        }

        if (session.CreatedAtUtc.AddHours(_options.SessionRetentionHours) < DateTime.UtcNow)
        {
            DeleteSessionDirectory(sessionId);
            return null;
        }

        return session;
    }

    private async Task CleanupExpiredSessionsAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(_options.DataPath);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(dir, SessionManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            LeadFlowImportSession? session;
            try
            {
                session = JsonSerializer.Deserialize<LeadFlowImportSession>(
                    await File.ReadAllTextAsync(manifestPath, ct),
                    JsonOptions);
            }
            catch
            {
                continue;
            }

            if (session is null || session.CreatedAtUtc.AddHours(_options.SessionRetentionHours) < DateTime.UtcNow)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private void DeleteSessionDirectory(Guid sessionId)
    {
        var dir = GetSessionDirectory(sessionId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class LeadFlowImportSession
    {
        public Guid SessionId { get; set; }
        public Guid OfficeId { get; set; }
        public string DbPath { get; set; } = string.Empty;
        public string? EncryptionKey { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    private sealed record AnalyzedLeadFlowRow(
        LeadFlowCandidateRecord Record,
        bool CanImport,
        string ImportState,
        string? ExistingStatus);
}

internal static class LeadFlowImportStates
{
    public const string New = "new";
    public const string AlreadyExists = "exists";
    public const string Invalid = "invalid";
}