using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Worker;

public sealed class WorkerUpdateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string StoreDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitaWorker");

    private static string StorePath => Path.Combine(StoreDirectory, "update-result.json");

    private static string PendingInstallPath => Path.Combine(StoreDirectory, "pending-install.txt");

    private static string PendingMsiPath => Path.Combine(StoreDirectory, "pending-msi.json");

    private WorkerUpdateResultDto? _pendingHeartbeatResult;

    public sealed record PendingMsiState(string Version, string MsiPath);

    public void SaveResult(WorkerUpdateResultDto result)
    {
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(result, JsonOptions));
        _pendingHeartbeatResult = result;
    }

    public void SavePendingInstall(string targetVersion)
    {
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(PendingInstallPath, targetVersion);
    }

    public void SaveDownloadedMsi(string version, string msiPath)
    {
        Directory.CreateDirectory(StoreDirectory);
        var payload = new PendingMsiState(version, msiPath);
        File.WriteAllText(PendingMsiPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public PendingMsiState? TryGetPendingMsi()
    {
        if (!File.Exists(PendingMsiPath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<PendingMsiState>(File.ReadAllText(PendingMsiPath), JsonOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.Version) || string.IsNullOrWhiteSpace(state.MsiPath))
            {
                return null;
            }

            if (!File.Exists(state.MsiPath))
            {
                ClearPendingMsi();
                return null;
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    public void ClearPendingMsi()
    {
        TryDeleteFile(PendingMsiPath);
    }

    public WorkerUpdateResultDto? TryConsumePendingHeartbeatResult()
    {
        var finalizedInstall = TryFinalizePendingInstall();
        if (finalizedInstall is not null)
        {
            return finalizedInstall;
        }

        _pendingHeartbeatResult ??= LoadLastResult();

        var pending = _pendingHeartbeatResult;
        _pendingHeartbeatResult = null;
        if (pending is not null)
        {
            TryDeleteStoreFile();
        }

        return pending;
    }

    private static WorkerUpdateResultDto? TryFinalizePendingInstall()
    {
        if (!File.Exists(PendingInstallPath))
        {
            return null;
        }

        string targetVersion;
        try
        {
            targetVersion = File.ReadAllText(PendingInstallPath).Trim();
            File.Delete(PendingInstallPath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return null;
        }

        var currentVersion = ApplicationVersionProvider.GetVersion();
        var succeeded = string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        return new WorkerUpdateResultDto(
            targetVersion,
            succeeded,
            succeeded ? "Обновление установлено." : "Обновление не применилось.",
            DateTime.UtcNow);
    }

    public WorkerUpdateResultDto? LoadLastResult()
    {
        if (!File.Exists(StorePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerUpdateResultDto>(File.ReadAllText(StorePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteStoreFile() => TryDeleteFile(StorePath);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}