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

    private WorkerUpdateResultDto? _pendingHeartbeatResult;

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

    private static void TryDeleteStoreFile()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}