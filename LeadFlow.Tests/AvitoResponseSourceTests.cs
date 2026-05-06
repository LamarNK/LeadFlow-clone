using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Browser;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoResponseSourceTests
{
    [Fact]
    public async Task GetNewResponsesAsync_ValidJson_ReturnsCandidates_AndClearsError()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction(
            """{"hasCaptcha":false,"hasLogin":false,"candidates":[{"fullName":"Иван Иванов","phone":"+79001234567","sourceResponseId":"src-1"}]}""");

        var sut = new AvitoResponseSource(
            repo,
            new FakeBrowserSessionService(),
            new NoOpBackgroundWebViewHostFactory(),
            automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("src-1", list[0].SourceResponseId);
        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
        Assert.Equal(string.Empty, account.LastErrorMessage);
    }

    [Fact]
    public async Task GetNewResponsesAsync_HasCaptcha_SetsManualAction_AndReturnsEmpty()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("""{"hasCaptcha":true,"hasLogin":false,"candidates":[]}""");

        var sut = new AvitoResponseSource(
            repo,
            new FakeBrowserSessionService(),
            new NoOpBackgroundWebViewHostFactory(),
            automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Empty(list);
        Assert.Equal(AvitoAccountStatus.RequiresManualAction, account.Status);
    }

    [Fact]
    public async Task GetNewResponsesAsync_HasLogin_SetsRequiresLogin()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("""{"hasCaptcha":false,"hasLogin":true,"candidates":[]}""");

        var sut = new AvitoResponseSource(
            repo,
            new FakeBrowserSessionService(),
            new NoOpBackgroundWebViewHostFactory(),
            automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Empty(list);
        Assert.Equal(AvitoAccountStatus.RequiresLogin, account.Status);
    }

    [Fact]
    public async Task GetNewResponsesAsync_InvalidJsonThenValid_Retries_AndReturnsCandidates()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("{not json");
        automation.EnqueueExtraction("also bad");
        automation.EnqueueExtraction(
            """{"hasCaptcha":false,"hasLogin":false,"candidates":[{"fullName":"A","phone":"+7999","sourceResponseId":"retry-ok"}]}""");

        var sut = new AvitoResponseSource(
            repo,
            new FakeBrowserSessionService(),
            new NoOpBackgroundWebViewHostFactory(),
            automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("retry-ok", list[0].SourceResponseId);
    }

    [Fact]
    public async Task GetNewResponsesAsync_EmptyExtractionThreeTimes_SetsLastError()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("");
        automation.EnqueueExtraction("");
        automation.EnqueueExtraction("");

        var sut = new AvitoResponseSource(
            repo,
            new FakeBrowserSessionService(),
            new NoOpBackgroundWebViewHostFactory(),
            automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Empty(list);
        Assert.Contains("Пустой ответ скрипта", account.LastErrorMessage, StringComparison.Ordinal);
    }

    private static AvitoAccount NewAccount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "TestAcc",
        Status = AvitoAccountStatus.Authorized
    };

    private static AppSettings NewSettings() => new()
    {
        Bitrix = new BitrixSettings(),
        MonitoringSafety = new MonitoringSafetyOptions(),
        Avito = new AvitoSettings()
    };

    private sealed class AvitoCandidatesPageAutomationStub : IWebPageAutomationService
    {
        private readonly Queue<string> _extraction = new();

        public void EnqueueExtraction(string payload) => _extraction.Enqueue(payload);

        public Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken)
        {
            if (script.Contains("readyState", StringComparison.Ordinal) && script.Contains("bodyLength", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"readyState":"complete","bodyLength":200}""");
            }

            if (script.Contains("fnv1a32Hex", StringComparison.Ordinal))
            {
                var next = _extraction.Count > 0 ? _extraction.Dequeue() : "";
                return Task.FromResult(next);
            }

            return Task.FromResult("{}");
        }
    }
}
