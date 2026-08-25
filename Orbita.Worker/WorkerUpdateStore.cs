using System.Text.Json;
using Orbita.Contracts;
using Orbita.Worker.Services;

namespace Orbita.Worker;

public sealed class WorkerUpdateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static string StoreDirectory =>
        Environment.GetEnvironmentVariable("ORBITA_WORKER_UPDATE_STORE_DIR") is { Length: > 0 } testDir
            ? testDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbitaWorker");

    internal static string UpdateLogsDirectory => Path.Combine(StoreDirectory, "update-logs");

    internal static string SelfUpdateStatusPath => Path.Combine(StoreDirectory, "self-update-status.txt");

    internal static string LastResultPath => Path.Combine(StoreDirectory, "update-result.last.json");

    private static string StorePath => Path.Combine(StoreDirectory, "update-result.json");

    private static string PendingInstallPath => Path.Combine(StoreDirectory, "pending-install.txt");

    private static string PendingMsiPath => Path.Combine(StoreDirectory, "pending-msi.json");

    private static string SilentBlockPath => Path.Combine(StoreDirectory, "silent-install-blocked.txt");

    private WorkerUpdateResultDto? _pendingHeartbeatResult;

    public sealed record PendingMsiState(string Version, string MsiPath);

    public static string GetUpdateWorkingDirectory() =>
        Path.Combine(Path.GetTempPath(), "orbita-worker-update");

    public static string BuildMsiLogPath(string version, string? timestampUtc = null)
    {
        var stamp = timestampUtc ?? DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var safeVersion = string.Join("_", version.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(UpdateLogsDirectory, $"msi-{safeVersion}-{stamp}.log");
    }

    public void SaveResult(WorkerUpdateResultDto result)
    {
        Directory.CreateDirectory(StoreDirectory);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(StorePath, json);
        File.WriteAllText(LastResultPath, json);
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
        ClearSilentInstallBlockIfDifferentVersion(version);
    }

    public void SaveSilentInstallBlocked(string version, string message)
    {
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(SilentBlockPath, version);
        SaveResult(new WorkerUpdateResultDto(version, false, message, DateTime.UtcNow));
    }

    public bool IsSilentInstallBlocked(string version)
    {
        if (!File.Exists(SilentBlockPath))
        {
            return false;
        }

        try
        {
            var blocked = File.ReadAllText(SilentBlockPath).Trim();
            return string.Equals(blocked, version, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal void ClearSilentInstallBlocked()
    {
        TryDeleteFile(SilentBlockPath);
    }

    public PendingMsiState? TryGetPendingMsi()
    {
        var state = TryReadPendingMsiState();
        if (state is null)
        {
            return null;
        }

        if (!File.Exists(state.MsiPath))
        {
            ClearPendingMsi();
            return null;
        }

        if (!AppVersionHelper.IsNewer(state.Version, ApplicationVersionProvider.GetVersion()))
        {
            DiscardPendingMsi(state);
            return null;
        }

        return state;
    }

    /// <summary>Удаляет скачанный MSI, если текущая версия воркера уже не ниже целевой.</summary>
    public bool TryPruneObsoletePendingMsi(out string? prunedVersion)
    {
        prunedVersion = null;
        var state = TryReadPendingMsiState();
        if (state is null)
        {
            return false;
        }

        if (AppVersionHelper.IsNewer(state.Version, ApplicationVersionProvider.GetVersion()))
        {
            return false;
        }

        prunedVersion = state.Version;
        DiscardPendingMsi(state);
        TryDeleteFile(SilentBlockPath);
        return true;
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

    public WorkerUpdateResultDto? PeekLastResult()
    {
        if (_pendingHeartbeatResult is not null)
        {
            return _pendingHeartbeatResult;
        }

        var stored = LoadLastResult() ?? LoadArchivedLastResult();
        if (stored is not null)
        {
            return stored;
        }

        return TryReadStatusResult(SelfUpdateStatusPath)
            ?? TryReadStatusResult(Path.Combine(UpdateLogsDirectory, "self-update-status.last.txt"));
    }

    private WorkerUpdateResultDto? TryFinalizePendingInstall()
    {
        var statusResult = TryConsumeSelfUpdateStatus();
        if (statusResult is not null)
        {
            if (statusResult.Success)
            {
                TryDeleteFile(PendingInstallPath);
                TryDeleteFile(PendingMsiPath);
                TryDeleteFile(SilentBlockPath);
            }
            else
            {
                TryDeleteFile(PendingInstallPath);
            }

            SaveResult(statusResult);
            return statusResult;
        }

        if (!File.Exists(PendingInstallPath))
        {
            return null;
        }

        string targetVersion;
        try
        {
            targetVersion = File.ReadAllText(PendingInstallPath).Trim();
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
        var succeeded = string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase)
            || !AppVersionHelper.IsNewer(targetVersion, currentVersion);
        if (!succeeded)
        {
            // msiexec may still be running; wait for the status file instead of
            // reporting a false failure and launching a second install.
            return null;
        }

        TryDeleteFile(PendingInstallPath);
        TryDeleteFile(PendingMsiPath);
        TryDeleteFile(SilentBlockPath);

        var result = new WorkerUpdateResultDto(
            targetVersion,
            true,
            "Обновление установлено.",
            DateTime.UtcNow);
        SaveResult(result);
        return result;
    }

    private WorkerUpdateResultDto? TryConsumeSelfUpdateStatus()
    {
        if (!File.Exists(SelfUpdateStatusPath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(SelfUpdateStatusPath);
            var status = WorkerUpdateBatchScript.ParseStatus(text);
            if (status is null)
            {
                return null;
            }

            var archivePath = Path.Combine(UpdateLogsDirectory, "self-update-status.last.txt");
            Directory.CreateDirectory(UpdateLogsDirectory);
            File.Copy(SelfUpdateStatusPath, archivePath, overwrite: true);
            File.Delete(SelfUpdateStatusPath);

            var message = status.Message ?? string.Empty;
            var exitCode = status.ExitCode;
            if (!status.Success && File.Exists(status.LogPath))
            {
                try
                {
                    var log = File.ReadAllText(status.LogPath);
                    if (log.Contains("Error 1730", StringComparison.OrdinalIgnoreCase)
                        || log.Contains("You must be an Administrator to remove this application", StringComparison.OrdinalIgnoreCase))
                    {
                        exitCode = 1730;
                        message = WorkerUpdateBatchScript.FormatResultMessage(
                            1730,
                            false,
                            status.LogPath,
                            status.ExePath,
                            status.MsiPath);
                    }
                }
                catch
                {
                    // keep the exit-code message
                }
            }

            if (!status.Success)
            {
                if (File.Exists(status.MsiPath))
                {
                    SaveDownloadedMsi(status.Version, status.MsiPath);
                }

                if (WorkerUpdateBatchScript.ShouldBlockSilentRetry(exitCode))
                {
                    Directory.CreateDirectory(StoreDirectory);
                    File.WriteAllText(SilentBlockPath, status.Version);
                }
            }

            return new WorkerUpdateResultDto(status.Version, status.Success, message, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private void ClearSilentInstallBlockIfDifferentVersion(string version)
    {
        if (!File.Exists(SilentBlockPath))
        {
            return;
        }

        try
        {
            var blocked = File.ReadAllText(SilentBlockPath).Trim();
            if (!string.Equals(blocked, version, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(SilentBlockPath);
            }
        }
        catch
        {
            TryDeleteFile(SilentBlockPath);
        }
    }

    private PendingMsiState? TryReadPendingMsiState()
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

            return state;
        }
        catch
        {
            return null;
        }
    }

    private void DiscardPendingMsi(PendingMsiState state)
    {
        ClearPendingMsi();
        TryDeleteFile(state.MsiPath);
    }

    public WorkerUpdateResultDto? LoadLastResult()
    {
        return TryReadResultFile(StorePath);
    }

    private WorkerUpdateResultDto? LoadArchivedLastResult() => TryReadResultFile(LastResultPath);

    private WorkerUpdateResultDto? TryReadStatusResult(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var status = WorkerUpdateBatchScript.ParseStatus(File.ReadAllText(path));
            return status is null
                ? null
                : new WorkerUpdateResultDto(status.Version, status.Success, status.Message, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static WorkerUpdateResultDto? TryReadResultFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerUpdateResultDto>(File.ReadAllText(path), JsonOptions);
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
