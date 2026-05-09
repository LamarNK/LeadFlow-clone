using System.Reflection;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Bitrix;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringServiceTests
{
    [Fact]
    public async Task ProcessAccount_RespectsMaxResponsesPerCycle()
    {
        var settings = NewSettings();
        var source = new FakeAvitoResponseSource
        {
            Impl = (account, _) => Enumerable.Range(0, 15)
                .Select(i => NewIncomingResponse(account, $"src-{i}"))
                .Cast<CandidateResponse>()
                .ToList()
        };
        var bitrix = new FakeBitrixClient
        {
            CreateLeadImpl = (_, _) => new BitrixCreateLeadResponse { IsSuccess = true, EntityId = "777" }
        };
        var harness = new MonitoringHarness(source, bitrix, settings);
        var processed = new List<CandidateResponse>();
        harness.Service.ResponseProcessed += (_, response) => processed.Add(response);

        var account = NewAccount();
        await harness.Service.ProcessAccountAsync(account, settings, CancellationToken.None);

        Assert.Equal(MonitoringTiming.MaxResponsesPerAccountPerCycle, processed.Count);
        Assert.Equal(MonitoringTiming.MaxResponsesPerAccountPerCycle, bitrix.CreateLeadCallCount);
        Assert.Contains(harness.Statuses, s =>
            s.Item2.Contains("15", StringComparison.Ordinal) &&
            s.Item2.Contains($"до {MonitoringTiming.MaxResponsesPerAccountPerCycle}", StringComparison.Ordinal));
        Assert.Contains(harness.Statuses, s =>
            s.Item2.Contains("обрабатываем отклик", StringComparison.Ordinal) &&
            s.Item2.Contains($"/{MonitoringTiming.MaxResponsesPerAccountPerCycle}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAccount_AccountRequiresLogin_SkipsSourceAndBitrix()
    {
        var settings = NewSettings();
        var source = new FakeAvitoResponseSource();
        var bitrix = new FakeBitrixClient();
        var harness = new MonitoringHarness(source, bitrix, settings);

        var account = NewAccount();
        account.Status = AvitoAccountStatus.RequiresLogin;

        await harness.Service.ProcessAccountAsync(account, settings, CancellationToken.None);

        Assert.Equal(0, source.CallCount);
        Assert.Equal(0, bitrix.CreateLeadCallCount);
        Assert.Contains(harness.Statuses, s => s.Item1 == MonitoringStatus.RequiresAuthorization);
    }

    [Fact]
    public async Task ProcessResponse_LocalDuplicate_DoesNotCallBitrix_AndMarksDuplicate()
    {
        var settings = NewSettings();
        var bitrix = new FakeBitrixClient();
        var duplicate = new FakeDuplicateService
        {
            Impl = (_, _) => new DuplicateCheckResult { IsLocalDuplicate = true }
        };
        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), bitrix, settings, duplicate);
        CandidateResponse? processed = null;
        harness.Service.ResponseProcessed += (_, response) => processed = response;

        var response = NewIncomingResponse(NewAccount(), "src-1");
        await harness.Service.ProcessResponseAsync(response, settings, CancellationToken.None);

        Assert.Equal(0, bitrix.CreateLeadCallCount);
        Assert.NotNull(processed);
        Assert.Equal(ResponseStatus.Duplicate, processed!.Status);
        Assert.NotNull(processed.ProcessedAt);
    }

    [Fact]
    public async Task ProcessResponse_BitrixUnavailable_DefersSend_AndMarksActionRequired()
    {
        var settings = NewSettings();
        var bitrix = new FakeBitrixClient();
        var duplicate = new FakeDuplicateService
        {
            Impl = (_, _) => new DuplicateCheckResult
            {
                IsBitrixCheckUnavailable = true,
                BitrixCheckUnavailableReason = "HTTP 500"
            }
        };
        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), bitrix, settings, duplicate);
        CandidateResponse? processed = null;
        harness.Service.ResponseProcessed += (_, response) => processed = response;

        var response = NewIncomingResponse(NewAccount(), "src-1");
        await harness.Service.ProcessResponseAsync(response, settings, CancellationToken.None);

        Assert.Equal(0, bitrix.CreateLeadCallCount);
        Assert.NotNull(processed);
        Assert.Equal(ResponseStatus.ActionRequired, processed!.Status);
        Assert.Contains("HTTP 500", processed.ErrorMessage);
        Assert.Contains(harness.Repository.Logs, l => l.Message == "Отправка в Bitrix24 отложена");
    }

    [Fact]
    public async Task ProcessResponse_BitrixSuccess_SetsSentAndEntityIds()
    {
        var settings = NewSettings();
        var bitrix = new FakeBitrixClient
        {
            CreateLeadImpl = (_, _) => new BitrixCreateLeadResponse
            {
                IsSuccess = true,
                EntityId = "DEAL-42",
                ContactId = "CONT-7"
            }
        };
        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), bitrix, settings);
        CandidateResponse? processed = null;
        harness.Service.ResponseProcessed += (_, response) => processed = response;

        var response = NewIncomingResponse(NewAccount(), "src-ok");
        await harness.Service.ProcessResponseAsync(response, settings, CancellationToken.None);

        Assert.Equal(1, bitrix.CreateLeadCallCount);
        Assert.NotNull(processed);
        Assert.Equal(ResponseStatus.Sent, processed!.Status);
        Assert.Equal("DEAL-42", processed.BitrixEntityId);
        Assert.Equal("CONT-7", processed.BitrixContactId);
        Assert.NotNull(processed.ProcessedAt);
        Assert.Contains(harness.Repository.Logs, l => l.Message == "Сделка создана в Bitrix24" && l.Details == "DEAL-42");
        var saveSentIdx = harness.Repository.OperationTrace.FindIndex(t =>
            t.StartsWith("Save:Sent:", StringComparison.Ordinal));
        var logDealIdx = harness.Repository.OperationTrace.FindIndex(t =>
            t.Contains("Сделка создана в Bitrix24", StringComparison.Ordinal));
        Assert.True(saveSentIdx >= 0);
        Assert.True(logDealIdx >= 0);
        Assert.True(saveSentIdx < logDealIdx);
    }

    [Fact]
    public async Task ProcessResponse_BitrixFailsWithoutContact_SetsError()
    {
        var settings = NewSettings();
        var bitrix = new FakeBitrixClient
        {
            CreateLeadImpl = (_, _) => new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                EntityId = string.Empty,
                ContactId = string.Empty,
                Error = "CRM offline"
            }
        };
        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), bitrix, settings);
        CandidateResponse? processed = null;
        harness.Service.ResponseProcessed += (_, r) => processed = r;

        var response = NewIncomingResponse(NewAccount(), "src-fail");
        await harness.Service.ProcessResponseAsync(response, settings, CancellationToken.None);

        Assert.NotNull(processed);
        Assert.Equal(ResponseStatus.Error, processed!.Status);
        Assert.Equal("CRM offline", processed.ErrorMessage);
        Assert.Contains(harness.Repository.Logs, l => l.Level == "Error" && l.Message == "Ошибка Bitrix24");
        Assert.Equal(MonitoringStatus.Error, harness.Service.CurrentStatus);
    }

    [Fact]
    public async Task ProcessResponse_BitrixFailsWithOrphanContact_SetsActionRequired()
    {
        var settings = NewSettings();
        var bitrix = new FakeBitrixClient
        {
            CreateLeadImpl = (_, _) => new BitrixCreateLeadResponse
            {
                IsSuccess = false,
                ContactId = "99",
                Error = "deal rejected"
            }
        };
        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), bitrix, settings);
        CandidateResponse? processed = null;
        harness.Service.ResponseProcessed += (_, r) => processed = r;

        var response = NewIncomingResponse(NewAccount(), "src-orphan");
        await harness.Service.ProcessResponseAsync(response, settings, CancellationToken.None);

        Assert.NotNull(processed);
        Assert.Equal(ResponseStatus.ActionRequired, processed!.Status);
        Assert.Equal("99", processed.BitrixContactId);
        Assert.Equal("deal rejected", processed.ErrorMessage);
        Assert.Contains(
            harness.Statuses,
            s => s.Item2.Contains("контакт Bitrix24 создан", StringComparison.Ordinal) &&
                 s.Item2.Contains("сделка не создана", StringComparison.Ordinal));
        Assert.Contains(harness.Repository.Logs, l => l.Message == "Контакт в Bitrix24 без сделки — требуется действие");
    }

    [Fact]
    public async Task TryRecover_ExceedsMaxFailures_RaisesAutoStopped_AndReturnsFalse()
    {
        var settings = NewSettings();
        var harness = new MonitoringHarness(
            new FakeAvitoResponseSource(),
            new FakeBitrixClient(),
            settings);

        // MaxLoopRecoveryFailuresBeforeStop = 10, поэтому имитируем 10 уже произошедших сбоев — следующий должен стать фатальным.
        var counterField = typeof(MonitoringService).GetField(
            "_consecutiveMonitoringLoopFailures",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(counterField);
        counterField!.SetValue(harness.Service, 10);

        string? autoMessage = null;
        harness.Service.MonitoringAutoStopped += (_, message) => autoMessage = message;

        var shouldRetry = await harness.Service.TryRecoverMonitoringLoopAsync(
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.False(shouldRetry);
        Assert.False(harness.Service.IsActive);
        Assert.NotNull(autoMessage);
        Assert.Contains("boom", autoMessage!);
        Assert.Equal(MonitoringStatus.Error, harness.Service.CurrentStatus);
    }

    [Fact]
    public void SetNextCheckTime_RaisesEvent_OnlyWhenValueChanges()
    {
        var settings = NewSettings();
        var harness = new MonitoringHarness(
            new FakeAvitoResponseSource(),
            new FakeBitrixClient(),
            settings);
        var changes = 0;
        harness.Service.NextCycleCheckTimeChanged += (_, _) => changes++;

        var instant = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        harness.Service.SetNextCheckTime(instant);
        harness.Service.SetNextCheckTime(instant);
        harness.Service.SetNextCheckTime(null);

        Assert.Equal(2, changes);
        Assert.Null(harness.Service.NextCycleCheckAtUtc);
    }

    private static AppSettings NewSettings() => new()
    {
        DemoModeEnabled = false,
        MonitoringSafety = new MonitoringSafetyOptions(),
        Bitrix = new BitrixSettings
        {
            WebhookUrl = string.Empty,
            CheckDuplicatesInBitrix = false,
            LeadSource = "Авито"
        }
    };

    private static AvitoAccount NewAccount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "TestAcc",
        IsEnabled = true,
        Status = AvitoAccountStatus.Authorized
    };

    private static CandidateResponse NewIncomingResponse(AvitoAccount account, string sourceId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        AccountName = account.DisplayName,
        Source = "Avito",
        SourceResponseId = sourceId,
        FullName = "Иванов Иван Иванович",
        PhoneRaw = "+7 900 000-00-00",
        City = "Москва",
        Vacancy = "Продавец",
        VacancyUrl = "https://www.avito.ru/item/1",
        CreatedAt = DateTime.UtcNow
    };

    private sealed class MonitoringHarness
    {
        public MonitoringHarness(
            FakeAvitoResponseSource source,
            FakeBitrixClient bitrix,
            AppSettings settings,
            FakeDuplicateService? duplicate = null)
        {
            Source = source;
            Bitrix = bitrix;
            Duplicate = duplicate ?? new FakeDuplicateService();
            Repository = new FakeMonitoringRepository();

            Service = new MonitoringService(
                new FakeSettingsService(settings),
                Repository,
                new PhoneNormalizer(),
                new CandidateParser(),
                Duplicate,
                bitrix,
                source,
                new AvitoDemoResponseSource(),
                new AvitoParserService(),
                new StubBrowserSessionService(),
                new NoOpBackgroundWebViewHostFactory(),
                new StubWebPageAutomationService(),
                new StubAdsPowerAvitoAutomationService());

            Service.StatusChanged += (_, status) => Statuses.Add((status, Service.CurrentStatusMessage));
            Service.StatusMessageChanged += (_, message) => Statuses.Add((Service.CurrentStatus, message));
        }

        public FakeAvitoResponseSource Source { get; }
        public FakeBitrixClient Bitrix { get; }
        public FakeDuplicateService Duplicate { get; }
        public FakeMonitoringRepository Repository { get; }
        public MonitoringService Service { get; }
        public List<(MonitoringStatus, string)> Statuses { get; } = new();
    }
}
