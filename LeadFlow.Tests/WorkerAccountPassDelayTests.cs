using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerAccountPassDelayTests
{
    [Fact]
    public void CdpRetry_NightQuiet_StaysOneMinute()
    {
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);
        Assert.True(MonitoringNightQuiet.IsActive(night));

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 5,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void QuietPass_Night_RaisesToNightFloor()
    {
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: null,
            polled: true,
            newResponses: 0,
            quietStreak: 1,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);

        Assert.True(delay.TotalMinutes >= MonitoringTiming.NightQuietDelayMinMinutes);
        Assert.True(delay.TotalMinutes <= MonitoringTiming.NightQuietDelayMaxMinutes);
    }

    [Fact]
    public void CdpRetry_Daytime_StaysOneMinute()
    {
        var day = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(MonitoringNightQuiet.IsActive(day));

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 0,
            backlog: false,
            historicalHeat: 0,
            utcNow: day);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void LocalApiTimeout_IsShortRetry_NotErrorPause()
    {
        var timeout = new AdsPowerLocalApiTimeoutException(
            AdsPowerLocalApiCall.OperationUserList,
            AdsPowerLocalApiCall.PhaseHttpResponse,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(8));
        var generic = new TimeoutException("AdsPower: запуск сессии не завершился за 3 мин.");

        Assert.Equal(WorkerAdsPowerPassRetry.Delay, WorkerAdsPowerPassRetry.FromException(timeout));
        Assert.Null(WorkerAdsPowerPassRetry.FromException(generic));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(timeout));
        Assert.Equal("Error", WorkerAdsPowerPassRetry.EventType(generic));
    }

    [Fact]
    public void LocalChromeAlreadyRunning_IsShortRetry_NotErrorPause()
    {
        var busy = new InvalidOperationException(
            "Обычный браузер: запуск Chrome не удался — The browser is already running for C:\\Orbita\\ChromeProfiles\\acc. Use a different UserDataDir or stop the running browser first.");
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);

        Assert.Equal(WorkerAdsPowerPassRetry.Delay, WorkerAdsPowerPassRetry.FromException(busy));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(busy));
        Assert.True(WorkerAdsPowerPassRetry.IsLocalChromeProfileBusy(busy));

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: WorkerAdsPowerPassRetry.FromException(busy),
            polled: false,
            newResponses: 0,
            quietStreak: 5,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void AccountPersonalDelay_ShortRetry_DoesNotClaimBrowserClosed()
    {
        using var capture = GlobalLogCapture.Start();
        var account = new AvitoAccount
        {
            DisplayName = "Avito 55",
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"C:\Orbita\ChromeProfiles\acc"
        };

        WorkerMonitoringLogger.AccountPersonalDelay(
            account,
            delayMinutes: 1,
            collectedCount: 0,
            publishedCount: 0,
            polled: false,
            browserClosed: false,
            shortRetry: true);

        var blob = capture.CombinedBlob();
        Assert.Contains("повтор ~1 мин (профиль был занят)", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("браузер закрыт", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("проход ok", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalChromeProtocolMismatch_IsNotShortRetry()
    {
        var mismatch = new InvalidOperationException(
            "Protocol error (Runtime.callFunctionOn): method not found");
        Assert.Null(WorkerAdsPowerPassRetry.FromException(mismatch));
        Assert.Equal("Error", WorkerAdsPowerPassRetry.EventType(mismatch));
    }
}
