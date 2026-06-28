using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Services.Browser;
using System.Text.Json;
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

        var sut = CreateSut(repo, automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("src-1", list[0].SourceResponseId);
        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
        Assert.Equal(string.Empty, account.LastErrorMessage);
    }

    [Fact]
    public async Task GetNewResponsesAsync_HasCaptcha_ThrowsCaptchaDetectedException_AndMarksRequiresManualAction()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("""{"hasCaptcha":true,"hasLogin":false,"candidates":[]}""");

        var sut = CreateSut(repo, automation);

        // Поведение по запросу пользователя «отслеживать капчу»: источник бросает типизированное
        // исключение, чтобы мониторинг СРАЗУ вышел из обхода аккаунта (а не пытался идти дальше).
        var ex = await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(
            () => sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None));

        // Статус и сообщение проставляются ДО throw — даже если кто-то проигнорирует исключение,
        // в БД отложится корректное состояние.
        Assert.Equal(AvitoAccountStatus.RequiresManualAction, account.Status);
        Assert.NotEmpty(ex.Kind);
    }

    [Fact]
    public async Task GetNewResponsesAsync_HasLogin_SetsRequiresLogin()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction("""{"hasCaptcha":false,"hasLogin":true,"candidates":[]}""");

        var sut = CreateSut(repo, automation);

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

        var sut = CreateSut(repo, automation);

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

        var sut = CreateSut(repo, automation);

        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Empty(list);
        Assert.Contains("Пустой ответ скрипта", account.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetNewResponsesAsync_NewSourceResponseIdSamePhoneAsJournal_SkipsDuplicateRow()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var phoneNormalizer = new PhoneNormalizer();
        var norm = phoneNormalizer.Normalize("8 952 333-24-46");
        await repo.SaveCandidateAsync(
            new CandidateResponse
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                SourceResponseId = "https://www.avito.ru/profile/messenger/channel/old",
                FullName = "Филимонов Станислав Васильевич",
                PhoneRaw = "8 952 333-24-46",
                PhoneNormalized = norm,
                Status = ResponseStatus.Duplicate,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            },
            CancellationToken.None);

        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction(
            """{"hasCaptcha":false,"hasLogin":false,"candidates":[{"fullName":"Филимонов Станислав Васильевич","phone":"8 952 333-24-46","sourceResponseId":"https://www.avito.ru/profile/messenger/channel/new"}]}""");

        var sut = CreateSut(repo, automation);
        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Empty(list);
    }

    [Fact]
    public async Task GetNewResponsesAsync_TwoCardsSamePhoneSameFetch_ReturnsOne()
    {
        var db = new EfInMemoryDatabase();
        var repo = new AppRepository(db.Factory);
        var account = NewAccount();
        var automation = new AvitoCandidatesPageAutomationStub();
        automation.EnqueueExtraction(
            """{"hasCaptcha":false,"hasLogin":false,"candidates":[{"fullName":"Иван Иванов","phone":"+7 900 111-22-33","sourceResponseId":"id-a"},{"fullName":"Иван Иванов","phone":"89001112233","sourceResponseId":"id-b"}]}""");

        var sut = CreateSut(repo, automation);
        var list = await sut.GetNewResponsesAsync(account, NewSettings(), CancellationToken.None);

        Assert.Single(list);
    }

    private static AvitoResponseSource CreateSut(AppRepository repo, AvitoCandidatesPageAutomationStub automation)
    {
        var phone = new PhoneNormalizer();
        var adsPower = new FakeAdsPowerAvitoAutomationService();
        var source = new AvitoResponseSource(repo, adsPower, phone);
        var fetcher = new StubWebViewCandidatesFetcher(automation, source);
        return new AvitoResponseSource(repo, adsPower, phone, fetcher);
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

    private sealed class StubWebViewCandidatesFetcher(
        AvitoCandidatesPageAutomationStub automation,
        AvitoResponseSource source) : IAvitoWebViewCandidatesFetcher
    {
        public async Task<IReadOnlyList<CandidateResponse>> FetchNewResponsesAsync(
            AvitoAccount account,
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var raw = automation.DequeueExtraction();
                    return await source.ParseCandidatesFromRawAsync(account, settings, raw, cancellationToken);
                }
                catch (JsonException ex)
                {
                    account.LastErrorMessage = ex.Message;
                    if (attempt == maxAttempts)
                    {
                        return [];
                    }
                }
            }

            return [];
        }
    }

    private sealed class AvitoCandidatesPageAutomationStub : IWebPageAutomationService
    {
        private readonly Queue<string> _extraction = new();

        public void EnqueueExtraction(string payload) => _extraction.Enqueue(payload);

        public string DequeueExtraction() => _extraction.Count > 0 ? _extraction.Dequeue() : string.Empty;

        public Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken)
        {
            if (script.Contains("domStatusCount: statusButtons.length", StringComparison.Ordinal))
            {
                var next = _extraction.Count > 0 ? _extraction.Dequeue() : "";
                return Task.FromResult(next);
            }

            if (script.Contains("hasFirewallText && hasCaptchaWidget", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"blocked":false,"kind":"firewall","itemCount":0}""");
            }

            if (script.Contains("ready: document.readyState === \"complete\"", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"readyState":"complete","bodyLength":200,"itemCount":1,"statusCount":1,"blocked":false,"ready":true}""");
            }

            if (script.Contains("scrollBy", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"itemCount":1,"scrollTop":0,"scrollHeight":1000,"moved":false,"atEnd":true}""");
            }

            if (script.Contains("scrollTop = 0", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"ok":true}""");
            }

            if (script.Contains("withPhone", StringComparison.Ordinal) && script.Contains("items.length", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"ready":true,"items":1,"withPhone":1}""");
            }

            if (script.Contains("outerHTML", StringComparison.Ordinal))
            {
                return Task.FromResult(string.Empty);
            }

            return Task.FromResult("{}");
        }
    }

    private sealed class FakeAdsPowerAvitoAutomationService : IAdsPowerAvitoAutomationService
    {
        public Task<string> ExtractCandidatesJsonAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default,
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null) =>
            Task.FromResult("""{"hasCaptcha":false,"hasLogin":false,"candidates":[]}""");

        public Task<string> LoadProfileItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("<html><body></body></html>");

        public Task<string> LoadBlockedItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("<html><body></body></html>");

        public Task<string> LoadProfileSwitchHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("<html><body></body></html>");

        public Task<string> CaptureProfileSwitchHtmlInSessionAsync(
            PuppeteerSharp.IPage page,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("<html><body></body></html>");

        public Task<bool> SwitchActiveProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string subProfileId,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            Task.FromResult(true);

        public Task CloseBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task OpenUrlInRunningProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string url,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            Task.CompletedTask;

        public Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account session не используется в этих тестах.");
    }
}
