using Microsoft.Extensions.Options;
using Orbita.Api.Auth;
using Orbita.Api.Options;
using Orbita.Api.Services;

namespace Orbita.Api.Endpoints;

/// <summary>
/// Machine-to-machine worker-release publication for CI. This deliberately has a
/// separate secret from panel administration and exposes no release management operations.
/// </summary>
public static class WorkerReleasePublishEndpoints
{
    public const string TokenHeaderName = "X-Orbita-Worker-Release-Token";
    public const string RateLimitPolicyName = "worker-release-publish";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/deploy/worker-releases/latest", async (
            HttpRequest request,
            IOptions<WorkerReleaseOptions> options,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var authorizationError = GetAuthorizationError(request, options.Value.PublishToken);
            if (authorizationError is not null)
            {
                return authorizationError;
            }

            var latest = await releases.GetLatestAsync(ct);
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        }).RequireRateLimiting(RateLimitPolicyName);

        app.MapPost("/api/v1/deploy/worker-releases/upload", async (
            HttpRequest request,
            IOptions<WorkerReleaseOptions> options,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var authorizationError = GetAuthorizationError(request, options.Value.PublishToken);
            if (authorizationError is not null)
            {
                return authorizationError;
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("packageFile");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл packageFile не передан." });
            }

            await using var stream = file.OpenReadStream();
            var (release, error) = await releases.UploadAsync(
                stream,
                file.FileName,
                form["version"].FirstOrDefault(),
                form["releaseNotes"].FirstOrDefault(),
                ct);

            return error is null
                ? Results.Ok(release)
                : Results.BadRequest(new { error });
        }).DisableAntiforgery()
          .RequireRateLimiting(RateLimitPolicyName);
    }

    private static IResult? GetAuthorizationError(HttpRequest request, string expectedToken)
    {
        if (!WorkerReleasePublishTokenValidator.IsConfigured(expectedToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Публикация релизов не настроена.");
        }

        return WorkerReleasePublishTokenValidator.IsValid(
            request.Headers[TokenHeaderName].FirstOrDefault(),
            expectedToken)
            ? null
            : Results.Unauthorized();
    }
}
