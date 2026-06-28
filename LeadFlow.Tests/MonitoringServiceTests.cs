using System.Diagnostics;
using System.Reflection;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Services;
using LeadFlow.Services.Bitrix;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringServiceTests
{
    [Fact]
    public async Task StartAsync_ParallelMode_RespectsMaxConcurrentAccounts()
    {
        var settings = NewSettings();
        settings.MonitoringSafety.MaxConcurrentAccounts = 3;

        var concurrent = 0;
        var peakConcurrent = 0;
        var sync = new object();

        var source = new FakeAvitoResponseSource
        {
            AsyncImpl = async (_, _, ct) =>
            {
                lock (sync)
                {
                    concurrent++;
                    peakConcurrent = Math.Max(peakConcurrent, concurrent);
                }

                try
                {
                    await Task.Delay(2000, ct);
                    return Array.Empty<CandidateResponse>();
                }
                finally
                {
                    lock (sync)
                    {
                        concurrent--;
                    }
                }
            }
        };

        var accounts = Enumerable.Range(0, 5)
            .Select(i =>
            {
                var account = NewAccount();
                account.DisplayName = $"Acc-{i}";
                return account;
            })
            .ToList();

        var harness = new MonitoringHarness(source, new FakeBitrixClient(), settings);
        harness.Repository.AccountsImpl = () => accounts;

        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => source.CallCount >= 5, TimeSpan.FromSeconds(20));
            Assert.True(peakConcurrent <= 3);
            Assert.True(peakConcurrent >= 2);
        }
        finally
        {
            await harness.Service.StopAsync();
        }
    }

    [Fact]
    public async Task ProcessAccount_RespectsMaxResponsesPerCycle()
    {
        var settings = NewSettings();
        var source = new FakeAvitoResponseSource
        {
            Impl = (account, _) => Enumerable.Range(0, 15)
                .Select(i => NewIncomingResponse(account, $"src-{i}", $"+7900000{i:04}"))
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
    public async Task StartAsync_SyncsPersistedAccountState_BeforeFirstCycleSave()
    {
        var settings = NewSettings();
        var account = NewAccount();
        account.ProfileProvider = AvitoProfileProvider.AdsPower;
        account.AdsPowerProfileId = "ads-power-user";
        account.AdsPowerApiBaseUrl = "http://127.0.0.1:50325";
        account.Status = AvitoAccountStatus.Authorized;
        account.ActiveAdsCount = 0;
        account.BlockedCount = 0;
        account.DraftsCount = 0;
        account.ActiveAdsSnapshotJson = "[]";
        account.BlockedAdsSnapshotJson = "[]";
        settings.Avito.Accounts.Add(account);

        var persisted = NewAccount();
        persisted.Id = account.Id;
        persisted.DisplayName = account.DisplayName;
        persisted.ProfileProvider = AvitoProfileProvider.AdsPower;
        persisted.AdsPowerProfileId = account.AdsPowerProfileId;
        persisted.AdsPowerApiBaseUrl = account.AdsPowerApiBaseUrl;
        persisted.Status = AvitoAccountStatus.Authorized;
        persisted.ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
        [
            NewPersistedAd(account.Id, "9001"),
            NewPersistedAd(account.Id, "9002")
        ]);
        persisted.BlockedAdsSnapshotJson = "[]";
        persisted.ActiveAdsCount = 2;
        persisted.BlockedCount = 0;
        persisted.DraftsCount = 0;
        persisted.AdsStatsUpdatedAt = DateTime.UtcNow;

        var harness = new MonitoringHarness(new FakeAvitoResponseSource(), new FakeBitrixClient(), settings);
        harness.Repository.AccountsImpl = () => [persisted];

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await WaitForAsync(
                () => harness.Repository.SavedAccounts.Count > 0,
                TimeSpan.FromSeconds(5));

            var firstSaved = harness.Repository.SavedAccounts[0];
            Assert.Equal(account.Id, firstSaved.Id);
            Assert.Equal(2, firstSaved.ActiveAdsCount);
            Assert.Contains("\"Id\":\"9001\"", firstSaved.ActiveAdsSnapshotJson, StringComparison.Ordinal);
            Assert.Contains("\"Id\":\"9002\"", firstSaved.ActiveAdsSnapshotJson, StringComparison.Ordinal);
        }
        finally
        {
            await harness.Service.StopAsync();
        }
    }

    [Fact]
    public async Task ProcessAccount_AdsPowerSubProfiles_DoesNotClearPersistedAds_AfterFirstSubProfile()
    {
        var settings = NewSettings();
        var firstSubProfileResponsesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstSubProfileResponsesToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCallCount = 0;
        var source = new FakeAvitoResponseSource
        {
            AsyncImpl = async (_, _, _) =>
            {
                sourceCallCount++;
                if (sourceCallCount == 1)
                {
                    firstSubProfileResponsesStarted.TrySetResult();
                    await allowFirstSubProfileResponsesToFinish.Task;
                }

                return Array.Empty<CandidateResponse>();
            }
        };

        var adsPower = new SequenceAdsPowerAvitoAutomationService(new Dictionary<string, string>
        {
            ["sub-1"] = BuildAdsTabHtml(activeCount: 0),
            ["sub-2"] = BuildAdsTabHtml(activeCount: 1, adId: "2002")
        });

        var harness = new MonitoringHarness(
            source,
            new FakeBitrixClient(),
            settings,
            adsPowerService: adsPower);

        var account = NewAccount();
        account.ProfileProvider = AvitoProfileProvider.AdsPower;
        account.AdsPowerProfileId = "ads-power-user";
        account.AdsPowerApiBaseUrl = "http://127.0.0.1:50325";
        account.SetSubProfiles(
        [
            new AvitoSubProfile { Id = "sub-1", Name = "Первый" },
            new AvitoSubProfile { Id = "sub-2", Name = "Второй" }
        ]);
        account.ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
        [
            NewPersistedAd(account.Id, "1001"),
            NewPersistedAd(account.Id, "1002")
        ]);
        account.ActiveAdsCount = 2;
        account.AdsStatsUpdatedAt = DateTime.UtcNow.AddMinutes(-MonitoringTiming.ActiveAdsRefreshIntervalMinutes - 1);
        harness.Service.RestorePersistedAdSnapshots([account]);

        using var cts = new CancellationTokenSource();
        var processTask = harness.Service.ProcessAccountAsync(account, settings, cts.Token);

        await firstSubProfileResponsesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["1001", "1002"], harness.Service.GetActiveAdsSnapshot().Select(static ad => ad.Id).OrderBy(static id => id));
        Assert.Equal(2, account.ActiveAdsCount);

        cts.Cancel();
        allowFirstSubProfileResponsesToFinish.SetResult();
        await processTask;

        Assert.Equal(["1001", "1002"], harness.Service.GetActiveAdsSnapshot().Select(static ad => ad.Id).OrderBy(static id => id));
        Assert.Equal(2, account.ActiveAdsCount);
        Assert.Equal(["sub-1"], adsPower.SwitchCalls);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task ProcessAccount_AdsPowerSubProfiles_AppliesPartialStats_WhenOldSnapshotEmpty()
    {
        var settings = NewSettings();
        var adsPower = new SequenceAdsPowerAvitoAutomationService(new Dictionary<string, string>
        {
            ["sub-1"] = BuildAdsTabHtml(activeCount: 4, adId: "3001"),
            ["sub-2"] = string.Empty
        });

        var harness = new MonitoringHarness(
            new FakeAvitoResponseSource(),
            new FakeBitrixClient(),
            settings,
            adsPowerService: adsPower);

        var account = NewAccount();
        account.ProfileProvider = AvitoProfileProvider.AdsPower;
        account.AdsPowerProfileId = "ads-power-user";
        account.AdsPowerApiBaseUrl = "http://127.0.0.1:50325";
        account.SetSubProfiles(
        [
            new AvitoSubProfile { Id = "sub-1", Name = "Первый" },
            new AvitoSubProfile { Id = "sub-2", Name = "Второй" }
        ]);
        account.ActiveAdsSnapshotJson = "[]";
        account.ActiveAdsCount = 0;
        account.AdsStatsUpdatedAt = DateTime.UtcNow.AddMinutes(-MonitoringTiming.ActiveAdsRefreshIntervalMinutes - 1);
        harness.Service.RestorePersistedAdSnapshots([account]);

        await harness.Service.ProcessAccountAsync(account, settings, CancellationToken.None);

        Assert.Equal(["3001"], harness.Service.GetActiveAdsSnapshot().Select(static ad => ad.Id).OrderBy(static id => id));
        Assert.Equal(1, account.ActiveAdsCount);
        Assert.Null(account.AdsStatsUpdatedAt);
        Assert.Equal(2, adsPower.SwitchCalls.Count);
        Assert.Contains(
            harness.Repository.SavedAccounts,
            saved => saved.Id == account.Id && saved.ActiveAdsCount == 1);
    }

    [Fact]
    public async Task ProcessAccount_AdsPowerSubProfiles_ProgressivelyAppliesStats_WhenOldSnapshotEmpty()
    {
        var settings = NewSettings();
        var firstSubProfileResponsesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstSubProfileResponsesToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCallCount = 0;
        var source = new FakeAvitoResponseSource
        {
            AsyncImpl = async (_, _, _) =>
            {
                sourceCallCount++;
                if (sourceCallCount == 1)
                {
                    firstSubProfileResponsesStarted.TrySetResult();
                    await allowFirstSubProfileResponsesToFinish.Task;
                }

                return Array.Empty<CandidateResponse>();
            }
        };

        var adsPower = new SequenceAdsPowerAvitoAutomationService(new Dictionary<string, string>
        {
            ["sub-1"] = BuildAdsTabHtml(activeCount: 4, adId: "4001"),
            ["sub-2"] = BuildAdsTabHtml(activeCount: 6, adId: "4002")
        });

        var harness = new MonitoringHarness(
            source,
            new FakeBitrixClient(),
            settings,
            adsPowerService: adsPower);

        var account = NewAccount();
        account.ProfileProvider = AvitoProfileProvider.AdsPower;
        account.AdsPowerProfileId = "ads-power-user";
        account.AdsPowerApiBaseUrl = "http://127.0.0.1:50325";
        account.SetSubProfiles(
        [
            new AvitoSubProfile { Id = "sub-1", Name = "Первый" },
            new AvitoSubProfile { Id = "sub-2", Name = "Второй" }
        ]);
        account.ActiveAdsSnapshotJson = "[]";
        account.ActiveAdsCount = 0;
        account.AdsStatsUpdatedAt = null;
        harness.Service.RestorePersistedAdSnapshots([account]);

        using var cts = new CancellationTokenSource();
        var processTask = harness.Service.ProcessAccountAsync(account, settings, cts.Token);

        await firstSubProfileResponsesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        allowFirstSubProfileResponsesToFinish.SetResult();

        await WaitForAsync(
            () => harness.Service.GetActiveAdsSnapshot().Any(static ad => ad.Id == "4001"),
            TimeSpan.FromSeconds(10));

        Assert.Equal(["4001"], harness.Service.GetActiveAdsSnapshot().Select(static ad => ad.Id).OrderBy(static id => id));
        Assert.Equal(1, account.ActiveAdsCount);
        Assert.Null(account.AdsStatsUpdatedAt);
        Assert.Contains(
            harness.Repository.SavedAccounts,
            saved => saved.Id == account.Id && saved.ActiveAdsCount == 1);

        cts.Cancel();
        try
        {
            await processTask;
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо: отмена во время паузы перед вторым суб-профилем.
        }

        Assert.Equal(["4001"], harness.Service.GetActiveAdsSnapshot().Select(static ad => ad.Id).OrderBy(static id => id));
        Assert.Equal(1, account.ActiveAdsCount);
        Assert.Null(account.AdsStatsUpdatedAt);
        Assert.Equal(["sub-1"], adsPower.SwitchCalls);
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

    private static CandidateResponse NewIncomingResponse(
        AvitoAccount account,
        string sourceId,
        string? phoneRaw = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        AccountName = account.DisplayName,
        Source = "Avito",
        SourceResponseId = sourceId,
        FullName = "Иванов Иван Иванович",
        PhoneRaw = phoneRaw ?? "+7 900 000-00-00",
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
            FakeDuplicateService? duplicate = null,
            IAdsPowerAvitoAutomationService? adsPowerService = null)
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
                adsPowerService ?? new StubAdsPowerAvitoAutomationService());

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

    private static AvitoAdStatus NewPersistedAd(Guid accountId, string id) => new()
    {
        AccountId = accountId,
        Id = id,
        Title = $"Persisted {id}",
        Url = $"https://www.avito.ru/perm/vakansii/persisted_{id}"
    };

    private static string BuildAdsTabHtml(int activeCount, string? adId = null)
    {
        var cardHtml = string.IsNullOrWhiteSpace(adId)
            ? string.Empty
            : $$"""
              <div data-marker="item-snippet/{{adId}}">
                <a data-marker="view-link" href="//www.avito.ru/perm/vakansii/test_{{adId}}">
                  <span class="styles-title-UJzSB">Тест {{adId}}</span>
                </a>
              </div>
              """;

        return $$"""
               <div role="tablist" data-marker="profile-items-tab">
                 <button data-marker="profile-items-tab/tab(active)">
                   <span><span>Активные</span><span class="styles-module-counter-prLgf styles-module-counter_size-l-drhmu">{{activeCount}}</span></span>
                 </button>
                 <button data-marker="profile-items-tab/tab(rejected)">
                   <span><span>С ошибками</span><span class="styles-module-counter-prLgf styles-module-counter_disabled-bJRBA">0</span></span>
                 </button>
                 <button data-marker="profile-items-tab/tab(drafts)">
                   <span><span>Черновики</span><span class="styles-module-counter-prLgf styles-module-counter_disabled-bJRBA">0</span></span>
                 </button>
               </div>
               {{cardHtml}}
               """;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(predicate(), $"Condition was not met within {timeout.TotalSeconds:F1}s.");
    }

    private sealed class SequenceAdsPowerAvitoAutomationService(
        IReadOnlyDictionary<string, string> profileItemsHtmlBySubProfile) : IAdsPowerAvitoAutomationService
    {
        private readonly IReadOnlyDictionary<string, string> _profileItemsHtmlBySubProfile = profileItemsHtmlBySubProfile;
        private string? _currentSubProfileId;

        public List<string> SwitchCalls { get; } = [];

        public Task<string> ExtractCandidatesJsonAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default,
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null) =>
            throw new InvalidOperationException("В этих тестах JSON-кандидатов через AdsPower не используется.");

        public Task<string> LoadProfileItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default)
        {
            if (_currentSubProfileId is null)
            {
                throw new InvalidOperationException("Суб-профиль ещё не выбран.");
            }

            return Task.FromResult(_profileItemsHtmlBySubProfile[_currentSubProfileId]);
        }

        public Task<string> LoadBlockedItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> LoadProfileSwitchHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("В этих тестах HTML переключателя суб-профилей не используется.");

        public Task<string> CaptureProfileSwitchHtmlInSessionAsync(
            PuppeteerSharp.IPage page,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("В этих тестах HTML переключателя суб-профилей не используется.");

        public Task<bool> SwitchActiveProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string subProfileId,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false)
        {
            _currentSubProfileId = subProfileId;
            SwitchCalls.Add(subProfileId);
            return Task.FromResult(true);
        }

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
            throw new InvalidOperationException("Открытие URL в AdsPower не требуется для этого теста.");

        public Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAdsPowerAccountSession>(new SequenceSession(this, adsPowerUserId));

        private sealed class SequenceSession(
            SequenceAdsPowerAvitoAutomationService owner,
            string adsPowerUserId) : IAdsPowerAccountSession
        {
            public string AdsPowerUserId { get; } = adsPowerUserId;

            public Task<bool> SwitchSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default)
            {
                owner._currentSubProfileId = subProfileId;
                owner.SwitchCalls.Add(subProfileId);
                return Task.FromResult(true);
            }

            public Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default) =>
                Task.FromResult(true);

            public Task<string> ExtractCandidatesJsonAsync(
                CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult("""{"hasCaptcha":false,"hasLogin":false,"candidates":[]}""");

            public Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default)
            {
                if (owner._currentSubProfileId is null)
                {
                    throw new InvalidOperationException("Суб-профиль ещё не выбран.");
                }

                return Task.FromResult(owner._profileItemsHtmlBySubProfile[owner._currentSubProfileId]);
            }

            public Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(string.Empty);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
