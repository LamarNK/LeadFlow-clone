using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CrmCallAiProcessingService(
    OrbitaDbContext db,
    CrmCallRecordingStorageService recordingStorage,
    ICerioAiClient cerio,
    IOptions<CerioAiOptions> options,
    TimeProvider timeProvider,
    ILogger<CrmCallAiProcessingService> logger,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CerioAiOptions _options = options.Value;

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Token)) return 0;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var processFromUtc = _options.ProcessRecordingsFromUtc?.ToUniversalTime();
        var batchSize = Math.Clamp(_options.BatchSize, 1, 20);
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 25);
        var zeroDurationInsights = await db.CrmCallAiInsights
            .Include(x => x.Call)
            .Where(x => x.Call.DurationSeconds <= 0
                        && x.Status != CrmCallAiStatuses.Completed
                        && x.Status != CrmCallAiStatuses.Skipped)
            .ToListAsync(ct);
        foreach (var insight in zeroDurationInsights)
        {
            insight.Status = CrmCallAiStatuses.Skipped;
            insight.NextAttemptAtUtc = null;
            insight.LastErrorCode = "zero_duration";
            insight.LastErrorMessage = "Нулевая длительность записи: расшифровка не выполняется.";
            insight.UpdatedAtUtc = now;
        }
        if (zeroDurationInsights.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var insight in zeroDurationInsights) Notify(insight);
        }

        var existingQuery = db.CrmCallAiInsights
            .Include(x => x.Call)
            .Where(x => x.Call.RecordingStoragePath != null
                        && x.Call.CardId != null
                        && x.Call.DurationSeconds > 0
                        && x.Status != CrmCallAiStatuses.Completed
                        && x.Status != CrmCallAiStatuses.Skipped
                        && x.Attempts < maxAttempts
                        && x.NextAttemptAtUtc != null
                        && x.NextAttemptAtUtc <= now);
        if (processFromUtc.HasValue)
        {
            existingQuery = existingQuery.Where(x => x.Call.StartedAtUtc >= processFromUtc.Value);
        }
        var existing = await existingQuery
            .OrderByDescending(x => x.Call.StartedAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        if (existing.Count < batchSize)
        {
            var missingCallsQuery = db.CrmCalls
                .Where(call => call.RecordingStoragePath != null
                               && call.CardId != null
                               && call.DurationSeconds > 0
                               && !db.CrmCallAiInsights.Any(x => x.CallId == call.Id));
            if (processFromUtc.HasValue)
            {
                missingCallsQuery = missingCallsQuery.Where(call => call.StartedAtUtc >= processFromUtc.Value);
            }
            var missingCalls = await missingCallsQuery
                .OrderByDescending(call => call.StartedAtUtc)
                .Take(batchSize - existing.Count)
                .ToListAsync(ct);
            foreach (var call in missingCalls)
            {
                var insight = new CrmCallAiInsightEntity
                {
                    CallId = call.Id,
                    Call = call,
                    Status = CrmCallAiStatuses.Pending,
                    PromptVersion = NormalizePromptVersion(_options.PromptVersion),
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.CrmCallAiInsights.Add(insight);
                existing.Add(insight);
            }
            if (missingCalls.Count > 0) await db.SaveChangesAsync(ct);
        }

        var completed = 0;
        foreach (var insight in existing)
        {
            if (await ProcessOneAsync(insight, maxAttempts, ct)) completed++;
        }
        return completed;
    }

    public async Task<bool> RetryAsync(Guid callId, CancellationToken ct = default)
    {
        var insight = await db.CrmCallAiInsights.FirstOrDefaultAsync(x => x.CallId == callId, ct);
        if (insight is null) return false;
        insight.Status = string.IsNullOrWhiteSpace(insight.TranscriptText)
            ? CrmCallAiStatuses.Pending
            : CrmCallAiStatuses.Partial;
        insight.Attempts = 0;
        insight.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        insight.LastErrorCode = null;
        insight.LastErrorMessage = null;
        insight.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> ProcessOneAsync(
        CrmCallAiInsightEntity insight,
        int maxAttempts,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        insight.Attempts++;
        insight.NextAttemptAtUtc = now.AddMinutes(15);
        insight.LastErrorCode = null;
        insight.LastErrorMessage = null;
        insight.UpdatedAtUtc = now;

        try
        {
            if (string.IsNullOrWhiteSpace(insight.TranscriptText))
            {
                insight.Status = CrmCallAiStatuses.Transcribing;
                await db.SaveChangesAsync(ct);
                await using var audio = recordingStorage.OpenRead(insight.Call.RecordingStoragePath!)
                                        ?? throw new CerioAiException(
                                            "recording_missing",
                                            "Локальный файл записи не найден.",
                                            true);
                var transcription = await cerio.TranscribeAsync(
                    audio,
                    insight.Call.RecordingFileName ?? $"call-{insight.CallId:N}.bin",
                    insight.Call.RecordingContentType ?? "application/octet-stream",
                    ct);
                insight.TranscriptText = transcription.Text;
                insight.SegmentsJson = JsonSerializer.Serialize(transcription.Segments, JsonOptions);
                insight.TranscribedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            }

            insight.Status = CrmCallAiStatuses.Analyzing;
            insight.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            var analysisResult = await cerio.AnalyzeAsync(insight.TranscriptText!, ct);
            insight.AnalysisRawText = analysisResult.RawAnswer;
            insight.PromptVersion = NormalizePromptVersion(_options.PromptVersion);
            insight.NextAttemptAtUtc = null;
            insight.AnalyzedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            insight.UpdatedAtUtc = insight.AnalyzedAtUtc.Value;
            if (analysisResult.Analysis is null)
            {
                insight.Status = CrmCallAiStatuses.Partial;
                insight.LastErrorCode = "analysis_unstructured";
                insight.LastErrorMessage = "Анализ получен, но не распознан как структурированный отчёт.";
            }
            else
            {
                insight.AnalysisJson = JsonSerializer.Serialize(analysisResult.Analysis, JsonOptions);
                insight.Score = analysisResult.Analysis.Score;
                insight.Status = CrmCallAiStatuses.Completed;
            }
            await db.SaveChangesAsync(ct);
            Notify(insight);
            return insight.Status == CrmCallAiStatuses.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CerioAiException ex)
        {
            ApplyFailure(insight, ex.Code, ex.Message, ex.Retryable, maxAttempts);
        }
        catch (Exception ex)
        {
            ApplyFailure(insight, "processing", "Не удалось обработать запись звонка.", true, maxAttempts);
            logger.LogWarning("Call AI processing failed for {CallId} ({ExceptionType}).", insight.CallId, ex.GetType().Name);
        }

        await db.SaveChangesAsync(ct);
        Notify(insight);
        return false;
    }

    private void ApplyFailure(
        CrmCallAiInsightEntity insight,
        string code,
        string message,
        bool retryable,
        int maxAttempts)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        insight.Status = string.IsNullOrWhiteSpace(insight.TranscriptText)
            ? CrmCallAiStatuses.Failed
            : CrmCallAiStatuses.Partial;
        insight.LastErrorCode = code;
        insight.LastErrorMessage = message.Length <= 500 ? message : message[..500];
        insight.UpdatedAtUtc = now;
        insight.NextAttemptAtUtc = retryable && insight.Attempts < maxAttempts
            ? now.Add(GetRetryDelay(insight.Attempts))
            : null;
    }

    private void Notify(CrmCallAiInsightEntity insight)
    {
        if (insight.Call.CardId is not null)
        {
            panelRealtime?.Notify([PanelChangeKind.Crm], insight.Call.OfficeId);
        }
    }

    private static TimeSpan GetRetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        <= 3 => TimeSpan.FromMinutes(5),
        <= 5 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(3)
    };

    private static string NormalizePromptVersion(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? CerioAiClient.DefaultPromptVersion
            : normalized.Length <= 32 ? normalized : normalized[..32];
    }
}

public sealed class CrmCallAiProcessingHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<CerioAiOptions> options,
    ILogger<CrmCallAiProcessingHostedService> logger) : BackgroundService
{
    private readonly CerioAiOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 10, 3600));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CrmCallAiProcessingService>()
                    .ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Call AI background processing failed ({ExceptionType}).", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
