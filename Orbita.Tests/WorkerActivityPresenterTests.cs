using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerActivityPresenterTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid AccountId2 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void Present_OfflineWorker_ReturnsOfflineLabel()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Account,
                "проверка",
                AccountId,
                "acc-1",
                null,
                null,
                null,
                Now.AddSeconds(-5)),
            isOnline: false,
            nowUtc: Now);

        Assert.Equal("Оффлайн", result.Label);
        Assert.Equal("offline", result.Tone);
        Assert.False(result.IsLive);
    }

    [Fact]
    public void Present_RecentActivity_IsLiveWithAccountLabel()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                AccountId,
                "user_01",
                "sp-1",
                "Основной",
                null,
                Now.AddSeconds(-20)),
            isOnline: true,
            nowUtc: Now);

        Assert.Equal("«user_01» · «Основной» · сбор откликов", result.Label);
        Assert.Equal("live", result.Tone);
        Assert.True(result.IsLive);
    }

    [Fact]
    public void Present_StaleActivity_AppendsAgeSuffix()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Cycle,
                "старт цикла",
                null,
                null,
                null,
                null,
                null,
                Now.AddMinutes(-6)),
            isOnline: true,
            nowUtc: Now);

        Assert.Contains("назад", result.Label);
        Assert.Equal("muted", result.Tone);
        Assert.False(result.IsLive);
    }

    [Fact]
    public void PresentForAccount_MatchesAccountAndLiveActivity()
    {
        var result = WorkerActivityPresenter.PresentForAccount(
            new WorkerActivityDto(
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                AccountId,
                "user_01",
                "sp-1",
                "Основной",
                null,
                Now.AddSeconds(-10)),
            workerIsOnline: true,
            AccountId,
            nowUtc: Now);

        Assert.True(result.IsProcessingNow);
        Assert.Equal("«Основной» · сбор откликов", result.Label);
        Assert.Equal("sp-1", result.SubProfileId);
    }

    [Fact]
    public void Present_IdlePhase_ShowsNoAccountsMessage()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Idle,
                "Нет активных аккаунтов",
                null,
                null,
                null,
                null,
                null,
                Now.AddSeconds(-5)),
            isOnline: true,
            nowUtc: Now);

        Assert.Equal("Нет активных аккаунтов", result.Label);
        Assert.DoesNotContain("цикл", result.Label);
    }

    [Fact]
    public void Present_WaitingPhase_PastNextCycle_DoesNotShowCountdown()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Waiting,
                "ожидание",
                null,
                null,
                null,
                null,
                Now.AddMinutes(-10),
                Now.AddMinutes(-40)),
            isOnline: true,
            nowUtc: Now);

        Assert.Equal("Пауза · ожидание цикла · 40 мин назад", result.Label);
    }

    [Fact]
    public void Present_WaitingPhase_ShowsUpdateMessage_WhenPendingInstall()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Waiting,
                "Пауза · установка обновления 1.0.0.33",
                null,
                null,
                null,
                null,
                Now.AddMinutes(12),
                Now.AddMinutes(-2)),
            isOnline: true,
            nowUtc: Now);

        Assert.Contains("установка обновления", result.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("следующий цикл", result.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Present_WaitingPhase_ShowsNextCycleMinutes()
    {
        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Waiting,
                "ожидание",
                null,
                null,
                null,
                null,
                Now.AddMinutes(7),
                Now.AddSeconds(-5)),
            isOnline: true,
            nowUtc: Now);

        Assert.StartsWith("Пауза · следующий цикл", result.Label);
        Assert.Equal("muted", result.Tone);
    }

    [Fact]
    public void Present_MultipleActiveAccounts_ReturnsParallelSummary()
    {
        var activeAccounts = new[]
        {
            new WorkerActiveAccountDto(
                AccountId,
                "user_01",
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                "sp-1",
                "Основной",
                Now.AddSeconds(-15)),
            new WorkerActiveAccountDto(
                AccountId2,
                "user_02",
                WorkerActivityPhases.Account,
                "проверка баланса",
                null,
                null,
                Now.AddSeconds(-10))
        };

        var result = WorkerActivityPresenter.Present(
            new WorkerActivityDto(
                WorkerActivityPhases.Parallel,
                "2 аккаунта в работе",
                null,
                null,
                null,
                null,
                null,
                Now.AddSeconds(-5),
                activeAccounts),
            isOnline: true,
            activeAccounts: activeAccounts,
            nowUtc: Now);

        Assert.Equal("2 аккаунта в работе", result.Label);
        Assert.Equal(WorkerActivityPhases.Parallel, result.Phase);
        Assert.True(result.IsLive);
        Assert.Equal(2, result.ActiveAccounts.Count);
    }

    [Fact]
    public void PresentActiveAccounts_ReturnsLiveAccountPills()
    {
        var activeAccounts = new[]
        {
            new WorkerActiveAccountDto(
                AccountId,
                "user_01",
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                "sp-1",
                "Основной",
                Now.AddSeconds(-15)),
            new WorkerActiveAccountDto(
                AccountId2,
                "user_02",
                WorkerActivityPhases.Account,
                "проверка баланса",
                null,
                null,
                Now.AddSeconds(-10))
        };

        var result = WorkerActivityPresenter.PresentActiveAccounts(activeAccounts, isOnline: true, nowUtc: Now);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Label == "«user_01» · «Основной» · сбор откликов");
        Assert.Contains(result, x => x.Label == "«user_02» · проверка баланса");
        Assert.All(result, x => Assert.True(x.IsLive));
    }

    [Fact]
    public void PresentForAccount_PrefersActiveAccountsOverLegacyActivity()
    {
        var activeAccounts = new[]
        {
            new WorkerActiveAccountDto(
                AccountId2,
                "user_02",
                WorkerActivityPhases.Account,
                "проверка баланса",
                null,
                null,
                Now.AddSeconds(-10))
        };

        var result = WorkerActivityPresenter.PresentForAccount(
            new WorkerActivityDto(
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                AccountId,
                "user_01",
                "sp-1",
                "Основной",
                null,
                Now.AddSeconds(-10)),
            workerIsOnline: true,
            AccountId2,
            activeAccounts: activeAccounts,
            nowUtc: Now);

        Assert.True(result.IsProcessingNow);
        Assert.Equal("проверка баланса", result.Label);
    }
}