using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerUpdateCoordinator(
    OrbitaApiClient apiClient,
    WorkerUpdateStore updateStore) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryCheckAndApplyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore transient update errors; retry on next interval
            }

            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryCheckAndApplyAsync(CancellationToken ct)
    {
        var currentVersion = ApplicationVersionProvider.GetVersion();
        var check = await apiClient.CheckForUpdateAsync(currentVersion, ct).ConfigureAwait(false);
        if (check is null || !check.HasUpdate || string.IsNullOrWhiteSpace(check.DownloadPath))
        {
            return;
        }

        var targetVersion = check.LatestVersion ?? currentVersion;
        var tempDir = Path.Combine(Path.GetTempPath(), "orbita-worker-update");
        Directory.CreateDirectory(tempDir);
        var msiPath = Path.Combine(tempDir, $"Orbita.Worker.Setup-{targetVersion}.msi");
        var applyUpdate = false;

        try
        {
            var (downloaded, downloadError) = await apiClient.DownloadUpdateAsync(check.DownloadPath, msiPath, ct)
                .ConfigureAwait(false);
            if (!downloaded)
            {
                SaveFailure(targetVersion, downloadError ?? "Не удалось скачать обновление.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(check.Sha256)
                && !await VerifySha256Async(msiPath, check.Sha256, ct).ConfigureAwait(false))
            {
                SaveFailure(targetVersion, "Контрольная сумма MSI не совпала.");
                return;
            }

            updateStore.SavePendingInstall(targetVersion);
            applyUpdate = true;
            WorkerRestartHelper.ScheduleInstallAndRestart(msiPath);
        }
        finally
        {
            if (!applyUpdate)
            {
                TryDeleteFile(msiPath);
            }
        }
    }

    private void SaveFailure(string version, string message) =>
        updateStore.SaveResult(new WorkerUpdateResultDto(version, false, message, DateTime.UtcNow));

    private static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

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