using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerReleaseService(IOptions<WorkerReleaseOptions> options)
{
    private const string PackageFileName = "update.msi";
    private const string ManifestFileName = "version.json";
    private const string LatestFileName = "latest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WorkerReleaseOptions _options = options.Value;

    public string RootPath => Path.GetFullPath(_options.DataPath);

    public async Task<WorkerReleaseListResponse> ListAsync(CancellationToken ct = default)
    {
        EnsureRoot();
        var versions = new List<WorkerReleaseInfoDto>();
        WorkerReleaseInfoDto? latest = null;

        if (File.Exists(GetLatestManifestPath()))
        {
            latest = await ReadManifestAsync(GetLatestManifestPath(), isLatest: true, ct);
            if (latest is not null)
            {
                versions.Add(latest);
            }
        }

        if (Directory.Exists(RootPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(RootPath))
            {
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var item = await ReadManifestAsync(manifestPath, isLatest: latest?.Version == null, ct);
                if (item is null || versions.Any(x => x.Version == item.Version))
                {
                    continue;
                }

                item = item with { IsLatest = latest?.Version == item.Version };
                versions.Add(item);
            }
        }

        versions = versions
            .OrderByDescending(x => Version.Parse(x.Version), Comparer<Version>.Default)
            .ToList();

        latest ??= versions.FirstOrDefault(x => x.IsLatest) ?? versions.FirstOrDefault();
        return new WorkerReleaseListResponse(latest, versions);
    }

    public async Task<WorkerReleaseLatestDto?> GetLatestAsync(CancellationToken ct = default)
    {
        var list = await ListAsync(ct);
        if (list.Latest is null)
        {
            return null;
        }

        return ToLatestDto(list.Latest);
    }

    public async Task<(WorkerReleaseInfoDto? Release, string? Error)> UploadAsync(
        Stream packageStream,
        string originalFileName,
        string? version,
        string? releaseNotes,
        CancellationToken ct = default)
    {
        EnsureRoot();

        var tempPath = Path.Combine(Path.GetTempPath(), $"orbita-worker-{Guid.NewGuid():N}.msi");
        await using (var tempStream = File.Create(tempPath))
        {
            await packageStream.CopyToAsync(tempStream, ct);
        }

        var fileSize = new FileInfo(tempPath).Length;
        if (fileSize > _options.MaxUploadBytes)
        {
            File.Delete(tempPath);
            return (null, "Файл слишком большой.");
        }

        try
        {
            if (!WorkerReleasePackageValidator.LooksLikeMsi(tempPath))
            {
                return (null, "Файл не похож на MSI.");
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                if (!AppVersionHelper.TryParseVersionFromFileName(originalFileName, out version))
                {
                    return (null, "Укажите версию или используйте имя Orbita.Worker.Setup-1.0.0.1.msi.");
                }
            }

            version = version.Trim();
            if (!AppVersionHelper.TryParseFourPart(version, out _))
            {
                return (null, "Версия должна быть в формате Major.Minor.Build.Revision.");
            }

            var releaseDir = GetVersionDirectory(version);
            Directory.CreateDirectory(releaseDir);
            var packagePath = Path.Combine(releaseDir, PackageFileName);
            var manifestPath = Path.Combine(releaseDir, ManifestFileName);

            File.Copy(tempPath, packagePath, overwrite: true);
            var sha256 = await ComputeSha256Async(packagePath, ct);
            var manifest = new StoredWorkerReleaseManifest
            {
                Version = version,
                ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes.Trim(),
                FileSize = new FileInfo(packagePath).Length,
                Sha256 = sha256,
                UploadedAtUtc = DateTime.UtcNow
            };

            await WriteManifestAsync(manifestPath, manifest, ct);
            await WriteManifestAsync(GetLatestManifestPath(), manifest, ct);

            var release = new WorkerReleaseInfoDto(
                manifest.Version,
                manifest.ReleaseNotes,
                manifest.FileSize,
                manifest.Sha256,
                true,
                manifest.UploadedAtUtc);

            return (release, null);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public async Task<(bool Success, string? Error)> SetLatestAsync(string version, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(GetVersionDirectory(version), ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return (false, "Версия не найдена.");
        }

        var manifest = await ReadStoredManifestAsync(manifestPath, ct);
        if (manifest is null)
        {
            return (false, "Не удалось прочитать version.json.");
        }

        await WriteManifestAsync(GetLatestManifestPath(), manifest, ct);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(string version, CancellationToken ct = default)
    {
        var releaseDir = GetVersionDirectory(version);
        if (!Directory.Exists(releaseDir))
        {
            return (false, "Версия не найдена.");
        }

        Directory.Delete(releaseDir, recursive: true);

        if (File.Exists(GetLatestManifestPath()))
        {
            var latest = await ReadStoredManifestAsync(GetLatestManifestPath(), ct);
            if (latest?.Version == version)
            {
                File.Delete(GetLatestManifestPath());
            }
        }

        return (true, null);
    }

    public async Task<WorkerUpdateCheckResponse> CheckUpdateAsync(string? currentVersion, CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(ct);
        if (latest is null)
        {
            return new WorkerUpdateCheckResponse(false, null, null, null, 0, null);
        }

        var hasUpdate = AppVersionHelper.IsNewer(latest.Version, currentVersion);
        return new WorkerUpdateCheckResponse(
            hasUpdate,
            latest.Version,
            $"/api/v1/workers/updates/download?version={Uri.EscapeDataString(latest.Version)}",
            latest.Sha256,
            latest.FileSize,
            latest.ReleaseNotes);
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenPackageAsync(string? version, CancellationToken ct = default)
    {
        StoredWorkerReleaseManifest? manifest;
        if (string.IsNullOrWhiteSpace(version))
        {
            manifest = await ReadStoredManifestAsync(GetLatestManifestPath(), ct);
        }
        else
        {
            manifest = await ReadStoredManifestAsync(Path.Combine(GetVersionDirectory(version.Trim()), ManifestFileName), ct);
        }

        if (manifest is null)
        {
            return (null, null, "Релиз не найден.");
        }

        var packagePath = Path.Combine(GetVersionDirectory(manifest.Version), PackageFileName);
        if (!File.Exists(packagePath))
        {
            return (null, null, "MSI не найден.");
        }

        Stream stream = File.OpenRead(packagePath);
        var fileName = $"Orbita.Worker.Setup-{manifest.Version}.msi";
        return (stream, fileName, null);
    }

    private WorkerReleaseLatestDto ToLatestDto(WorkerReleaseInfoDto latest) =>
        new(
            latest.Version,
            latest.ReleaseNotes,
            latest.FileSize,
            latest.Sha256,
            $"Orbita.Worker.Setup-{latest.Version}.msi",
            "/api/v1/public/worker-releases/latest/download",
            latest.UploadedAtUtc);

    private void EnsureRoot() => Directory.CreateDirectory(RootPath);

    private string GetVersionDirectory(string version) =>
        Path.Combine(RootPath, AppVersionHelper.GetReleaseDirectoryName(version));

    private string GetLatestManifestPath() => Path.Combine(RootPath, LatestFileName);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteManifestAsync(string path, StoredWorkerReleaseManifest manifest, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct);
    }

    private async Task<WorkerReleaseInfoDto?> ReadManifestAsync(string path, bool isLatest, CancellationToken ct)
    {
        var stored = await ReadStoredManifestAsync(path, ct);
        if (stored is null)
        {
            return null;
        }

        return new WorkerReleaseInfoDto(
            stored.Version,
            stored.ReleaseNotes,
            stored.FileSize,
            stored.Sha256,
            isLatest,
            stored.UploadedAtUtc);
    }

    private static async Task<StoredWorkerReleaseManifest?> ReadStoredManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredWorkerReleaseManifest>(stream, JsonOptions, ct);
    }

    private sealed class StoredWorkerReleaseManifest
    {
        public string Version { get; set; } = string.Empty;
        public string? ReleaseNotes { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTime UploadedAtUtc { get; set; }
    }
}