using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.AdsPower;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Browser;

namespace LeadFlow.Tests.Support;

internal sealed class FakeSettingsService(AppSettings settings) : ISettingsService
{
    public AppSettings Settings { get; set; } = settings;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public string GetSettingsPath() => string.Empty;
}

internal sealed class FakeDuplicateService : IDuplicateService
{
    public Func<CandidateResponse, AppSettings, DuplicateCheckResult> Impl { get; set; } =
        (_, _) => new DuplicateCheckResult();

    public int CallCount { get; private set; }

    public Task<DuplicateCheckResult> CheckAsync(
        CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        CallCount++;
        var result = Impl(response, settings);
        result.PhoneRaw = response.PhoneRaw;
        result.PhoneNormalized = response.PhoneNormalized;
        return Task.FromResult(result);
    }
}

internal sealed class FakeAvitoResponseSource : IAvitoResponseSource
{
    public Func<AvitoAccount, AppSettings, IReadOnlyList<CandidateResponse>> Impl { get; set; } =
        (_, _) => Array.Empty<CandidateResponse>();

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(
        AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Impl(account, settings));
    }
}

internal sealed class StubBrowserSessionService : IBrowserSessionService
{
    public Task<BrowserAccountSession> CreateSessionAsync(AvitoAccount account, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Браузер не должен использоваться в этих тестах");
}

internal sealed class FakeBrowserSessionService : IBrowserSessionService
{
    public Task<BrowserAccountSession> CreateSessionAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        var session = new BrowserAccountSession
        {
            Account = account,
            ProfilePath = Path.Combine(Path.GetTempPath(), "LeadFlow_TestProfile_" + Guid.NewGuid().ToString("N"))
        };
        return Task.FromResult(session);
    }
}

internal sealed class StubWebPageAutomationService : IWebPageAutomationService
{
    public Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Навигация не должна вызываться в этих тестах");

    public Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Скрипты не должны выполняться в этих тестах");
}

internal sealed class NoOpBackgroundWebViewHost : IBackgroundWebViewHost
{
    public Task AttachAsync(BrowserAccountSession session, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NoOpBackgroundWebViewHostFactory : IBackgroundWebViewHostFactory
{
    public Task<IBackgroundWebViewHost> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IBackgroundWebViewHost>(new NoOpBackgroundWebViewHost());
    }
}

internal sealed class StubAdsPowerAvitoAutomationService : IAdsPowerAvitoAutomationService
{
    public Task<string> ExtractCandidatesJsonAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("AdsPower CDP не должен вызываться в этих тестах.");

    public Task<string> LoadProfileItemsHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("AdsPower CDP не должен вызываться в этих тестах.");

    public Task<string> LoadProfileSwitchHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("AdsPower CDP не должен вызываться в этих тестах.");

    public Task<bool> SwitchActiveProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string subProfileId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("AdsPower CDP не должен вызываться в этих тестах.");
}
