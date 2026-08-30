using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using static Orbita.Api.Endpoints.EndpointRequestContext;

namespace Orbita.Api.Endpoints;

public static class WorkerEndpoints
{
    public static void Map(WebApplication app)
    {
        var workers = app.MapGroup("/api/v1/workers");
        workers.MapPost("/register", async (WorkerRegisterRequest request, TelemetryService telemetry, IConfiguration config, CancellationToken ct) =>
        {
            var secret = config["RegistrationSecret"];
            var result = await telemetry.RegisterAsync(request, secret, ct);
            if (result is null)
            {
                await GlobalLogger.Instance.LogAsync(
                    $"Worker registration rejected: invalid registration secret ({request.MachineName}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.worker.registration_rejected");
                return Results.Unauthorized();
            }

            await GlobalLogger.Instance.LogAsync(
                $"Worker registered ({result.WorkerId}, {request.DisplayName}, {request.MachineName}).",
                DeskLinkAuditLogLevel.Info);
            return Results.Ok(result);
        });

        workers.MapPost("/heartbeat", async (WorkerHeartbeatRequest request, TelemetryService telemetry, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
            {
                await GlobalLogger.Instance.LogAsync(
                    $"Worker heartbeat forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.worker.worker_id_mismatch");
                return Results.Forbid();
            }

            var clientIp = ClientIpResolver.Resolve(http);
            return await telemetry.HeartbeatAsync(request, clientIp, ct) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapPost("/activity", async (WorkerActivityRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
            {
                await GlobalLogger.Instance.LogAsync(
                    $"Worker activity forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.worker.worker_id_mismatch");
                return Results.Forbid();
            }

            return await telemetry.SaveActivityAsync(request, ct) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapPost("/telemetry/snapshot", async (WorkerSnapshotRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
            {
                await GlobalLogger.Instance.LogAsync(
                    $"Worker snapshot forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.worker.worker_id_mismatch");
                return Results.Forbid();
            }

            return await telemetry.SaveSnapshotAsync(request, ct) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapGet("/config", async (WorkerConfigService configService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var config = await configService.GetConfigForWorkerAsync(workerId, OfficeScope.GlobalAdmin, consumePendingCommand: true, ct);
            return config is null ? Results.NotFound() : Results.Ok(config);
        }).RequireAuthorization("Worker");

        workers.MapPost("/captcha-sessions/status", async (
            UpdateCaptchaSessionStatusRequest request,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var (success, error) = await captchaSessions.UpdateStatusFromWorkerAsync(workerId, request, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        }).RequireAuthorization("Worker");

        workers.MapGet("/captcha-sessions/{id:guid}", async (
            Guid id,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var session = await captchaSessions.GetForWorkerAsync(workerId, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization("Worker");

        workers.MapPost("/accounts/sync", async (
            WorkerAccountSyncRequest request,
            WorkerConfigService configService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            return await configService.SyncAccountsAsync(workerId, request, ct) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapPost("/provider-checks", async (
            ReportWorkerBrowserProviderCheckRequest request,
            WorkerConfigService configService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var (success, error) = await configService.ReportProviderCheckAsync(workerId, request, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        }).RequireAuthorization("Worker");

        workers.MapPost("/candidates", async (
            WorkerCandidateBatchRequest request,
            CandidateIngestionService ingestion,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            return Results.Ok(await ingestion.IngestBatchAsync(workerId, request, ct));
        }).RequireAuthorization("Worker");

        workers.MapGet("/monitoring-stats", async (
            WorkerMonitoringStatsService statsService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var stats = await statsService.GetForWorkerAsync(workerId, ct);
            return stats is null ? Results.NotFound() : Results.Ok(stats);
        }).RequireAuthorization("Worker");

        workers.MapPost("/candidates/lookup", async (
            WorkerCandidateLookupRequest request,
            CandidateLookupService lookup,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var result = await lookup.LookupAsync(workerId, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization("Worker");

        workers.MapGet("/pending-chat-messages", async (
            Guid accountId,
            WorkerOutboundChatService outboundChat,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId) || accountId == Guid.Empty)
            {
                return Results.Forbid();
            }

            var pending = await outboundChat.GetPendingAsync(workerId, accountId, ct);
            return Results.Ok(pending);
        }).RequireAuthorization("Worker");

        workers.MapPost("/outbound-chat/ack", async (
            WorkerOutboundChatAckRequest request,
            WorkerOutboundChatService outboundChat,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var ok = await outboundChat.AckSentAsync(workerId, request.SentIds, ct);
            return ok ? Results.NoContent() : Results.Forbid();
        }).RequireAuthorization("Worker");

        workers.MapPost("/outbound-chat/claim", async (
            WorkerOutboundChatClaimRequest request,
            WorkerOutboundChatService outboundChat,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            return await outboundChat.ClaimForDeliveryAsync(workerId, request.MessageId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapGet("/updates/check", async (
            string? currentVersion,
            WorkerReleaseService releases,
            CancellationToken ct) =>
            Results.Ok(await releases.CheckUpdateAsync(currentVersion, ct)))
            .RequireAuthorization("Worker");

        workers.MapGet("/updates/download", async (
            string? version,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var (stream, fileName, error) = await releases.OpenPackageAsync(version, ct);
            if (stream is null || fileName is null)
            {
                return Results.NotFound(new { error = error ?? "Релиз не найден." });
            }

            return Results.File(stream, "application/octet-stream", fileName);
        }).RequireAuthorization("Worker");

        workers.MapPost("/telemetry/events", async (WorkerEventBatchRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
            {
                await GlobalLogger.Instance.LogAsync(
                    $"Worker events forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.worker.worker_id_mismatch");
                return Results.Forbid();
            }

            return await telemetry.SaveEventsAsync(request, ct) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("Worker");

        workers.MapPost("/telemetry/monitoring-runs", async (
            MonitoringRunBatchRequest request,
            MonitoringRunIngestService ingest,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var (accepted, error) = await ingest.IngestBatchAsync(workerId, request, ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(new { accepted });
        }).RequireAuthorization("Worker");

        workers.MapPost("/diagnostics/upload", async (
            HttpRequest request,
            WorkerDiagnosticsService diagnostics,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл не передан." });
            }

            Guid? accountId = null;
            if (Guid.TryParse(form["accountId"], out var parsedAccountId))
            {
                accountId = parsedAccountId;
            }

            var kind = form["kind"].ToString();
            var pageUrl = form["pageUrl"].ToString();

            await using var stream = file.OpenReadStream();
            var (attachmentId, error) = await diagnostics.SaveUploadAsync(
                workerId,
                accountId,
                kind,
                string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl,
                stream,
                file.Length,
                ct);

            return attachmentId is null
                ? Results.BadRequest(new { error = error ?? "Не удалось сохранить вложение." })
                : Results.Ok(new WorkerDiagnosticUploadResponse(attachmentId.Value));
        }).RequireAuthorization("Worker")
        .DisableAntiforgery();

        workers.MapPost("/logs/batch", async (
            WorkerLogsBatchRequest request,
            WorkerLogArchiveService logs,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryGetWorkerId(user, out var workerId))
            {
                return Results.Forbid();
            }

            var entries = request.Entries ?? [];
            var (accepted, error) = await logs.IngestBatchAsync(workerId, entries, ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(new { accepted });
        }).RequireAuthorization("Worker");

    }
}
