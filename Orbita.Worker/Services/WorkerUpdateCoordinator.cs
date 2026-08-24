using System.Security.Cryptography;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerUpdateCoordinator(
    OrbitaApiClient apiClient,
    WorkerUpdateStore updateStore,
    WorkerUpdateOfferSource offerSource,
    WorkerUpdateGate updateGate,
    WorkerShutdownService shutdownService,
    WorkerRuntimeState runtimeState) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OfferPollInterval = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private string? _downloadingVersion;
    private string? _lastAppliedOfferVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        PruneObsoletePendingOnStartup();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryProcessOfferAsync(stoppingToken).ConfigureAwait(false);
                await TryApplyWhenReadyAsync().ConfigureAwait(false);
                await TryFallbackCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore transient update errors; retry on next interval
            }

            await Task.Delay(OfferPollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task TryProcessOfferAsync(CancellationToken ct)
    {
        var offer = offerSource.Current;
        if (offer is null)
        {
            return;
        }

        var currentVersion = ApplicationVersionProvider.GetVersion();
        if (!AppVersionHelper.IsNewer(offer.Version, currentVersion))
        {
            if (updateStore.TryPruneObsoletePendingMsi(out var prunedVersion))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker update: MSI {prunedVersion} уже не нужен (текущая версия {currentVersion}), ожидание снято.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(TryProcessOfferAsync));
            }

            return;
        }

        var pending = updateStore.TryGetPendingMsi();
        if (pending is not null && string.Equals(pending.Version, offer.Version, StringComparison.OrdinalIgnoreCase))
        {
            runtimeState.Detail = $"Обновление {offer.Version} скачано, ожидание паузы";
            return;
        }

        if (string.Equals(_downloadingVersion, offer.Version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await TryDownloadAsync(offer, ct).ConfigureAwait(false);
    }

    internal async Task TryFallbackCheckAsync(CancellationToken ct)
    {
        if (offerSource.Current is not null)
        {
            return;
        }

        var lastCheckUtc = _lastFallbackCheckUtc;
        if (DateTime.UtcNow - lastCheckUtc < CheckInterval)
        {
            return;
        }

        _lastFallbackCheckUtc = DateTime.UtcNow;
        var currentVersion = ApplicationVersionProvider.GetVersion();
        var check = await apiClient.CheckForUpdateAsync(currentVersion, ct).ConfigureAwait(false);
        if (check is null || !check.HasUpdate || string.IsNullOrWhiteSpace(check.DownloadPath))
        {
            return;
        }

        var offer = new WorkerUpdateOfferDto(
            check.LatestVersion ?? currentVersion,
            check.DownloadPath,
            check.Sha256 ?? string.Empty,
            check.FileSize,
            check.ReleaseNotes);
        await TryDownloadAsync(offer, ct).ConfigureAwait(false);
    }

    private DateTime _lastFallbackCheckUtc = DateTime.MinValue;

    internal bool TryApplyWhenReady()
    {
        var pending = updateStore.TryGetPendingMsi();
        if (pending is null)
        {
            return false;
        }

        if (updateStore.IsSilentInstallBlocked(pending.Version))
        {
            runtimeState.Detail = updateStore.PeekLastResult()?.Message
                ?? $"Обновление {pending.Version}: требуется ручная установка MSI";
            return false;
        }

        if (!updateGate.IsSafeToApply)
        {
            var (phase, monitoringActive) = updateGate.GetSnapshot();
            WorkerUpdateDeferLogger.LogDeferredInstall(
                nameof(TryApplyWhenReady),
                pending.Version,
                phase,
                monitoringActive);
            return false;
        }

        runtimeState.Status = "Обновление";
        runtimeState.Detail = $"Установка {pending.Version}";
        if (!shutdownService.RequestInstall(pending.MsiPath))
        {
            if (!shutdownService.IsShutdownInProgress)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker update: не удалось запустить установку {pending.Version} (MSI: {pending.MsiPath}).",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(TryApplyWhenReady));
            }

            return shutdownService.IsShutdownInProgress;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Worker update: запуск установки {pending.Version}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TryApplyWhenReady));
        _lastAppliedOfferVersion = pending.Version;
        return true;
    }

    private Task TryApplyWhenReadyAsync() => Task.Run(TryApplyWhenReady);

    private async Task TryDownloadAsync(WorkerUpdateOfferDto offer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(offer.Version) || string.IsNullOrWhiteSpace(offer.DownloadPath))
        {
            return;
        }

        if (!AppVersionHelper.IsNewer(offer.Version, ApplicationVersionProvider.GetVersion()))
        {
            return;
        }

        if (!await _downloadLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _downloadingVersion = offer.Version;
            var tempDir = Path.Combine(Path.GetTempPath(), "orbita-worker-update");
            Directory.CreateDirectory(tempDir);
            var msiPath = Path.Combine(tempDir, $"Orbita.Worker.Setup-{offer.Version}.msi");

            var (downloaded, downloadError) = await apiClient.DownloadUpdateAsync(
                    offer.DownloadPath,
                    msiPath,
                    offer.FileSize > 0 ? offer.FileSize : null,
                    ct)
                .ConfigureAwait(false);
            if (!downloaded)
            {
                SaveFailure(offer.Version, downloadError ?? "Не удалось скачать обновление.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(offer.Sha256)
                && !await VerifySha256Async(msiPath, offer.Sha256, ct).ConfigureAwait(false))
            {
                SaveFailure(offer.Version, "Контрольная сумма MSI не совпала.");
                TryDeleteFile(msiPath);
                return;
            }

            updateStore.SaveDownloadedMsi(offer.Version, msiPath);
            runtimeState.Detail = $"Обновление {offer.Version} скачано, ожидание паузы";
        }
        finally
        {
            _downloadingVersion = null;
            _downloadLock.Release();
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

    private void PruneObsoletePendingOnStartup()
    {
        if (!updateStore.TryPruneObsoletePendingMsi(out var prunedVersion))
        {
            return;
        }

        var currentVersion = ApplicationVersionProvider.GetVersion();
        _ = GlobalLogger.Instance.LogAsync(
            $"Worker update: сброшен устаревший MSI {prunedVersion} (текущая версия {currentVersion}).",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(PruneObsoletePendingOnStartup));
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