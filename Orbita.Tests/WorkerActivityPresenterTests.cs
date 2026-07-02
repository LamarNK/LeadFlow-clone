using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerActivityPresenterTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

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
            Now);

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
            Now);

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
            Now);

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
            Now);

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
            Now);

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
            Now);

        Assert.Equal("Пауза · ожидание цикла · 40 мин назад", result.Label);
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
            Now);

        Assert.StartsWith("Пауза · следующий цикл", result.Label);
        Assert.Equal("muted", result.Tone);
    }
}