using System.Reflection;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerCandidateSnapshotPipelineTests
{
    [Fact]
    public async Task IntermediateAndFinalSnapshots_PublishInitialAndPhoneChangeOnlyOnceBeforeExtractionCompletes()
    {
        using var _ = HumanDelay.SuppressDelaysForTests();
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Snapshot account",
            IsEnabled = true,
            Status = AvitoAccountStatus.Authorized,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = "profile-1",
            AdsPowerApiBaseUrl = "http://127.0.0.1:50325",
            SubProfilesRefreshedAt = DateTime.UtcNow
        };
        account.SetSubProfiles([new AvitoSubProfile { Id = "sub-1", Name = "Main" }]);

        var session = new SnapshotSession();
        var automation = new SnapshotAutomationService(session);
        var duplicateRepository = new FakeDuplicateRepository();
        var responseSource = new AvitoResponseSource(
            duplicateRepository,
            automation,
            new PhoneNormalizer());
        var sink = new RecordingCandidateSink(() => session.FinalExtractionReturned);
        var service = new WorkerMonitoringService(
            new SnapshotConfigProvider(),
            new FakeMonitoringRepository(),
            sink,
            new NullWorkerEventSink(),
            new NullWorkerTelemetrySink(),
            new NullWorkerDiagnosticsUploader(),
            new PhoneNormalizer(),
            duplicateRepository,
            new CandidateParser(),
            responseSource,
            new AvitoDemoResponseSource(),
            new AvitoParserService(),
            automation,
            NullWorkerActivityReporter.Instance,
            new NullWorkerPendingUpdateCoordinator(),
            NullBrowserMonitorSource.Instance,
            phoneObservationStore: new MemoryPhoneObservationStore());
        var settings = new AppSettings
        {
            DemoModeEnabled = false,
            DuplicateScope = DuplicateScope.GlobalAcrossAllAccounts
        };

        await InvokeProcessAccountAsync(service, account, settings);

        Assert.False(session.EnableMiniChatActions);
        Assert.Equal(2, sink.Published.Count);
        Assert.All(sink.PublishedBeforeFinalExtraction, Assert.True);
        Assert.All(sink.Published, candidate => Assert.True(string.IsNullOrWhiteSpace(candidate.ChatMessagesJson)));
        Assert.Equal("79000000000", new PhoneNormalizer().Normalize(sink.Published[0].PhoneRaw));
        Assert.Equal("79000000001", new PhoneNormalizer().Normalize(sink.Published[1].PhoneRaw));
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, sink.Published[1].PhoneMetricKind);
    }

    private static async Task InvokeProcessAccountAsync(
        WorkerMonitoringService service,
        AvitoAccount account,
        AppSettings settings)
    {
        var method = typeof(WorkerMonitoringService).GetMethod(
            "ProcessAccountAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(AvitoAccount), typeof(AppSettings), typeof(Guid), typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            [account, settings, Guid.NewGuid(), CancellationToken.None]));
        await task;
    }

    private static string CandidateJson(string phone) => $$"""
        {"hasCaptcha":false,"hasLogin":false,"candidates":[{
          "fullName":"Иванов Иван Иванович",
          "phone":"{{phone}}",
          "sourceResponseId":"src-1",
          "city":"Москва",
          "vacancy":"Оператор"
        }]}
        """;

    private sealed class SnapshotSession : IAdsPowerAccountSession
    {
        public string AdsPowerUserId => "profile-1";
        public bool FinalExtractionReturned { get; private set; }
        public bool? EnableMiniChatActions { get; private set; }

        public Task<SubProfileSwitchResult> SwitchSubProfileAsync(
            string subProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SubProfileSwitchResult.Succeeded);

        public Task<bool> VerifyActiveSubProfileAsync(
            string subProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async Task<string> ExtractCandidatesJsonAsync(
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            CancellationToken cancellationToken = default,
            AvitoAccountPassBudget? passBudget = null,
            Func<string, CancellationToken, Task>? onCandidatesSnapshotAsync = null)
        {
            EnableMiniChatActions = messengerEnrichmentHints?.EnableMiniChatActions;
            Assert.NotNull(onCandidatesSnapshotAsync);
            await onCandidatesSnapshotAsync(CandidateJson("+7 900 000-00-00"), cancellationToken);
            await onCandidatesSnapshotAsync(CandidateJson("+7 900 000-00-00"), cancellationToken);
            await onCandidatesSnapshotAsync(CandidateJson("+7 900 000-00-01"), cancellationToken);
            FinalExtractionReturned = true;
            return CandidateJson("+7 900 000-00-00");
        }

        public Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AvitoMoneySidebar?> TryReadMoneySidebarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AvitoMoneySidebar?>(null);

        public Task<AvitoAdvanceTopUpResult> RunAdvanceTopUpAsync(
            decimal amount,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task<(bool Allowed, string? Error)>>? beforePayClickAsync = null,
            Func<string, CancellationToken, Task>? reportProgressAsync = null) =>
            Task.FromResult(AvitoAdvanceTopUpResult.Failed("not supported"));

        public Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public string? CurrentPageUrl => AvitoCandidatesPageUrls.JobResponsesCrm;

        public Task<AvitoPageState?> GetPageStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AvitoPageState?>(null);

        public Task<byte[]?> CapturePageScreenshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<byte[]?> CapturePageJpegScreenshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<BrowserMonitorScreencastCapture> CreateMonitorScreencastCaptureAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotAutomationService(SnapshotSession session) : IAdsPowerAvitoAutomationService
    {
        public Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAdsPowerAccountSession>(session);

        public Task CloseBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExtractCandidatesJsonAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default,
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            Func<string, CancellationToken, Task>? onCandidatesSnapshotAsync = null) =>
            throw new NotSupportedException();

        public Task<string> LoadProfileItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> LoadBlockedItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> LoadProfileSwitchHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> CaptureProfileSwitchHtmlInSessionAsync(
            PuppeteerSharp.IPage page,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<bool> SwitchActiveProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string subProfileId,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            Task.FromResult(true);

        public Task OpenUrlInRunningProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string url,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            Task.CompletedTask;
    }

    private sealed class SnapshotConfigProvider : IWorkerConfigProvider
    {
        public Task<WorkerMonitoringConfig> GetConfigAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerMonitoringConfig
            {
                WorkerId = Guid.NewGuid(),
                PhoneUnchangedHours = ResponsePhoneWatchRules.DefaultUnchangedHours
            });
    }

    private sealed class RecordingCandidateSink(Func<bool> finalExtractionReturned) : INewCandidateSink
    {
        public List<CandidateResponse> Published { get; } = [];
        public List<bool> PublishedBeforeFinalExtraction { get; } = [];

        public Task<CandidatePublishResult> PublishAsync(
            CandidateResponse candidate,
            CancellationToken cancellationToken)
        {
            Published.Add(candidate);
            PublishedBeforeFinalExtraction.Add(!finalExtractionReturned());
            return Task.FromResult(CandidatePublishResult.Pending());
        }
    }

    private sealed class MemoryPhoneObservationStore : IResponsePhoneObservationStore
    {
        private readonly Dictionary<string, ResponsePhoneObservation> _items = new(StringComparer.Ordinal);

        public Task<ResponsePhoneObservation?> GetAsync(
            string avitoSubProfileId,
            string fullNameKey,
            CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(Key(avitoSubProfileId, fullNameKey), out var observation);
            return Task.FromResult(observation);
        }

        public Task UpsertAsync(
            ResponsePhoneObservation observation,
            CancellationToken cancellationToken = default)
        {
            _items[Key(observation.AvitoSubProfileId, observation.FullNameKey)] = observation;
            return Task.CompletedTask;
        }

        public Task<bool> IsOpenWatchAsync(
            string avitoSubProfileId,
            string fullNameKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _items.TryGetValue(Key(avitoSubProfileId, fullNameKey), out var observation)
                && ResponsePhoneObservationWatch.IsOpen(observation));

        private static string Key(string subProfileId, string fullNameKey) =>
            $"{subProfileId}\u001f{fullNameKey}";
    }
}
