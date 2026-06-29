using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerDiagnosticsService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IOptions<WorkerDiagnosticsOptions> options)
{
    private readonly WorkerDiagnosticsOptions _options = options.Value;

    public string RootPath => Path.GetFullPath(_options.DataPath);

    public async Task<(Guid? AttachmentId, string? Error)> SaveUploadAsync(
        Guid workerId,
        Guid? accountId,
        string kind,
        string? pageUrl,
        Stream content,
        long contentLength,
        CancellationToken ct)
    {
        if (contentLength <= 0 || contentLength > _options.MaxUploadBytes)
        {
            return (null, "Размер файла вне допустимого диапазона.");
        }

        var workerExists = await db.Workers.AnyAsync(x => x.Id == workerId, ct);
        if (!workerExists)
        {
            return (null, "Воркер не найден.");
        }

        EnsureRoot();
        var attachmentId = Guid.NewGuid();
        var relativePath = Path.Combine(workerId.ToString("N"), $"{attachmentId:N}.png");
        var fullPath = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var output = File.Create(fullPath))
        {
            await content.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        db.WorkerDiagnosticAttachments.Add(new WorkerDiagnosticAttachmentEntity
        {
            Id = attachmentId,
            WorkerId = workerId,
            AccountId = accountId,
            Kind = string.IsNullOrWhiteSpace(kind) ? "captcha" : kind.Trim(),
            PageUrl = string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl.Trim(),
            RelativePath = relativePath.Replace('\\', '/'),
            SizeBytes = contentLength,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = await PruneExpiredAttachmentsAsync(ct).ConfigureAwait(false);
        return (attachmentId, null);
    }

    public async Task<(Stream? Stream, string? ContentType, string? Error)> OpenForPanelAsync(
        Guid attachmentId,
        OfficeScope scope,
        CancellationToken ct)
    {
        var attachment = await db.WorkerDiagnosticAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (attachment is null)
        {
            return (null, null, "Вложение не найдено.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, attachment.WorkerId, ct))
        {
            return (null, null, "Нет доступа.");
        }

        var fullPath = GetFullPath(attachment.RelativePath);
        if (!File.Exists(fullPath))
        {
            return (null, null, "Файл не найден.");
        }

        return (File.OpenRead(fullPath), "image/png", null);
    }

    public async Task<bool> DeleteAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.WorkerDiagnosticAttachments
            .FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (attachment is null)
        {
            return false;
        }

        DeleteFileIfExists(attachment.RelativePath);
        db.WorkerDiagnosticAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public static Guid? TryParseAttachmentId(string? details) =>
        WorkerEventDetailsParser.TryParseAttachmentId(details);

    public async Task<int> PruneExpiredAttachmentsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        var stale = await db.WorkerDiagnosticAttachments
            .Where(x => x.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        foreach (var item in stale)
        {
            DeleteFileIfExists(item.RelativePath);
            db.WorkerDiagnosticAttachments.Remove(item);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return stale.Count;
    }

    private void EnsureRoot() => Directory.CreateDirectory(RootPath);

    private string GetFullPath(string relativePath) =>
        Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void DeleteFileIfExists(string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // ignore file delete errors; DB row will still be removed
        }
    }
}