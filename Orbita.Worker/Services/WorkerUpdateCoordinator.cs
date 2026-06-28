using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerUpdateCoordinator(
    OrbitaApiClient apiClient,
    WorkerUpdateStore updateStore,
    IHostApplicationLifetime lifetime) : BackgroundService
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

            var exitCode = await RunInstallerAsync(msiPath, ct).ConfigureAwait(false);
            if (exitCode == 0)
            {
                updateStore.SaveResult(new WorkerUpdateResultDto(
                    targetVersion,
                    true,
                    "Обновление установлено.",
                    DateTime.UtcNow));
                lifetime.StopApplication();
                return;
            }

            SaveFailure(targetVersion, $"msiexec завершился с кодом {exitCode}.");
        }
        finally
        {
            TryDeleteFile(msiPath);
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

    private static async Task<int> RunInstallerAsync(string msiPath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/qn /i \"{msiPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return -1;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
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