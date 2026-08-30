using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Multilogin;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public sealed record BrowserProviderProbeResult(
    bool Success,
    string Message,
    int? ProfileCount = null,
    int? GroupCount = null,
    string? ResolvedExecutablePath = null,
    IReadOnlyList<AdsPowerGroupDto>? Groups = null);

public sealed class BrowserProviderConnectionProbe(
    IAdsPowerApiClient adsPowerApi,
    IMultiloginApiClient multiloginApi)
{
    public async Task<BrowserProviderProbeResult> CheckAdsPowerAsync(
        AdsPowerConnectionOptions options,
        string? groupId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var profiles = await adsPowerApi
                .ListProfilesAsync(options, cancellationToken, groupId)
                .ConfigureAwait(false);
            IReadOnlyList<AdsPowerGroupDto>? groups = null;
            try
            {
                groups = (await adsPowerApi.ListGroupsAsync(options, cancellationToken).ConfigureAwait(false))
                    .Select(g => new AdsPowerGroupDto(g.GroupId, g.GroupName))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Группы справочные: профили уже подтверждают Local API.
            }

            var message = groups is { Count: var groupsFound }
                ? $"Local API доступен · {profiles.Count} профилей, {groupsFound} групп"
                : $"Local API доступен · {profiles.Count} профилей";
            return new BrowserProviderProbeResult(
                true,
                BrowserProviderProbeSanitizer.Sanitize(message, options.ApiKey),
                profiles.Count,
                groups?.Count,
                Groups: groups);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BrowserProviderProbeResult(
                false,
                BrowserProviderProbeSanitizer.FromException(ex, options.ApiKey));
        }
    }

    public async Task<BrowserProviderProbeResult> CheckMultiloginAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var token = options.AutomationToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new BrowserProviderProbeResult(false, "Укажите API Token Multilogin.");
        }

        try
        {
            await multiloginApi.ProbeLauncherAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BrowserProviderProbeResult(
                false,
                BrowserProviderProbeSanitizer.FromException(ex, token));
        }

        try
        {
            var catalog = await multiloginApi.SearchProfilesAsync(options, cancellationToken).ConfigureAwait(false);
            var message = $"Токен и launcher работают · {catalog.Profiles.Count} профилей";
            return new BrowserProviderProbeResult(
                true,
                BrowserProviderProbeSanitizer.Sanitize(message, token),
                catalog.Profiles.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BrowserProviderProbeResult(
                false,
                BrowserProviderProbeSanitizer.FromException(ex, token));
        }
    }

    public BrowserProviderProbeResult CheckLocalChrome(string? configuredPath)
    {
        try
        {
            var resolved = LocalChromePaths.ResolveExecutable(configuredPath);
            var message = string.IsNullOrWhiteSpace(configuredPath)
                ? $"Chrome будет найден автоматически · {resolved}"
                : $"Найден браузер · {resolved}";
            return new BrowserProviderProbeResult(
                true,
                BrowserProviderProbeSanitizer.Sanitize(message),
                ResolvedExecutablePath: resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BrowserProviderProbeResult(false, BrowserProviderProbeSanitizer.FromException(ex));
        }
    }
}
