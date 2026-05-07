using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public sealed class BrowserProfileArchiveService(IBrowserProfileService profileService) : IBrowserProfileArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task ExportAsync(AvitoAccount account, string zipFilePath, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Не блокируем UI: перечисление файлов и ZIP целиком на пуле потоков; Progress маршалится на UI.
        await Task.Yield();
        var info = profileService.GetProfile(account);
        var profilePath = info.ProfilePath;

        var manifest = new AvitoProfileArchiveManifest
        {
            ExportedAtUtc = DateTime.UtcNow,
            SourceDisplayName = account.DisplayName,
            Account = AvitoProfileAccountSnapshot.FromAccount(account),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await Task.Run(() => ExportZipWorker(profilePath, zipFilePath, manifestJson, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ExportZipWorker(
        string profilePath,
        string zipFilePath,
        string manifestJson,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(profilePath))
        {
            Directory.CreateDirectory(profilePath);
        }

        var files = Directory.EnumerateFiles(profilePath, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(profilePath, f);
                return !string.Equals(rel, AvitoProfileArchiveManifest.FileName, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var totalUnits = Math.Max(1, files.Count + 2);
        void ReportUnits(int completedUnits)
        {
            var pct = (int)Math.Round(100.0 * completedUnits / totalUnits);
            progress?.Report(Math.Clamp(pct, 0, 100));
        }

        ReportUnits(0);

        var tempZip = Path.Combine(Path.GetTempPath(), $"LeadFlow-export-{Guid.NewGuid():N}.zip");
        try
        {
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }

            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(AvitoProfileArchiveManifest.FileName, CompressionLevel.Optimal);
                using (var w = new StreamWriter(manifestEntry.Open()))
                {
                    w.Write(manifestJson);
                }

                ReportUnits(1);

                for (var i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = files[i];
                    var rel = Path.GetRelativePath(profilePath, file);
                    zip.CreateEntryFromFile(file, NormalizeZipPath(rel), CompressionLevel.Optimal);
                    ReportUnits(2 + i);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipFilePath))!);
            if (File.Exists(zipFilePath))
            {
                File.Delete(zipFilePath);
            }

            File.Move(tempZip, zipFilePath);
            ReportUnits(totalUnits);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try
                {
                    File.Delete(tempZip);
                }
                catch
                {
                }
            }
        }
    }

    public async Task ImportAsync(
        AvitoAccount targetAccount,
        string zipFilePath,
        bool applyManifestToAccount,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException("ZIP не найден.", zipFilePath);
        }

        await Task.Yield();
        var info = profileService.GetProfile(targetAccount);
        var targetPath = info.ProfilePath;

        var result = await Task.Run(
                () => ImportZipWorker(
                    zipFilePath,
                    targetPath,
                    applyManifestToAccount,
                    progress,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);

        targetAccount.BrowserProfilePath = targetPath;

        if (result.AccountSnapshot is { } snap)
        {
            snap.ApplyTo(targetAccount);
        }
    }

    public async Task<AvitoProfileArchiveManifest> ReadManifestAsync(string zipFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException("ZIP не найден.", zipFilePath);
        }

        await Task.Yield();
        return await Task.Run(() => ReadManifestWorker(zipFilePath), cancellationToken).ConfigureAwait(false);
    }

    private static AvitoProfileArchiveManifest ReadManifestWorker(string zipFilePath)
    {
        using var zip = ZipFile.OpenRead(zipFilePath);
        var manifestEntry = zip.GetEntry(AvitoProfileArchiveManifest.FileName)
            ?? zip.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, AvitoProfileArchiveManifest.FileName, StringComparison.OrdinalIgnoreCase));

        if (manifestEntry is null)
        {
            throw new InvalidDataException(
                $"В архиве нет {AvitoProfileArchiveManifest.FileName}. Это не архив профиля LeadFlow.");
        }

        using var ms = new MemoryStream();
        using (var s = manifestEntry.Open())
        {
            s.CopyTo(ms);
        }

        var manifest = JsonSerializer.Deserialize<AvitoProfileArchiveManifest>(ms.ToArray(), JsonOptions);
        if (manifest?.FormatVersion != 1)
        {
            throw new InvalidDataException("Неподдерживаемая версия архива профиля.");
        }

        return manifest;
    }

    private sealed record ImportWorkerResult(AvitoProfileAccountSnapshot? AccountSnapshot);

    private ImportWorkerResult ImportZipWorker(
        string zipFilePath,
        string targetPath,
        bool applyManifestToAccount,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var zip = ZipFile.OpenRead(zipFilePath);
        var manifestEntry = zip.GetEntry(AvitoProfileArchiveManifest.FileName)
            ?? zip.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, AvitoProfileArchiveManifest.FileName, StringComparison.OrdinalIgnoreCase));

        if (manifestEntry is null)
        {
            throw new InvalidDataException(
                $"В архиве нет {AvitoProfileArchiveManifest.FileName}. Это не архив профиля LeadFlow.");
        }

        using var ms = new MemoryStream();
        using (var s = manifestEntry.Open())
        {
            s.CopyTo(ms);
        }

        var manifest = JsonSerializer.Deserialize<AvitoProfileArchiveManifest>(ms.ToArray(), JsonOptions);
        if (manifest?.FormatVersion != 1)
        {
            throw new InvalidDataException("Неподдерживаемая версия архива профиля.");
        }

        var extractEntries = zip.Entries
            .Where(e =>
            {
                if (string.IsNullOrEmpty(e.Name))
                {
                    return false;
                }

                return !string.Equals(
                    e.FullName.Replace('\\', '/'),
                    AvitoProfileArchiveManifest.FileName,
                    StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var totalUnits = Math.Max(1, extractEntries.Count + 2);
        void ReportUnits(int completedUnits)
        {
            var pct = (int)Math.Round(100.0 * completedUnits / totalUnits);
            progress?.Report(Math.Clamp(pct, 0, 100));
        }

        ReportUnits(0);
        ReportUnits(1);

        if (Directory.Exists(targetPath))
        {
            profileService.DeleteProfile(targetPath);
        }

        Directory.CreateDirectory(targetPath);

        for (var i = 0; i < extractEntries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = extractEntries[i];
            var relative = entry.FullName.Replace('\\', '/');
            var destPath = GetSafeExtractPath(targetPath, relative);
            if (destPath is null)
            {
                throw new InvalidDataException($"Небезопасный путь в ZIP: {relative}");
            }

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            entry.ExtractToFile(destPath, overwrite: true);
            ReportUnits(2 + i);
        }

        ReportUnits(totalUnits);

        var snapshot = applyManifestToAccount ? manifest.Account : null;
        return new ImportWorkerResult(snapshot);
    }

    private static string? GetSafeExtractPath(string targetDir, string entryRelative)
    {
        var combined = Path.GetFullPath(Path.Combine(targetDir, entryRelative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                   + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            return null;
        }

        return combined;
    }

    private static string NormalizeZipPath(string relativePath) =>
        relativePath.Replace('\\', '/');
}
