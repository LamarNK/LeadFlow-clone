using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAccountPassBudgetTests
{
    private static AvitoAccount UnfinishedPassAccount(int phoneSpent = 0, int repliesSpent = 0, int restarts = 0)
    {
        var account = new AvitoAccount
        {
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddHours(-1),
            MonitoringPassFinishedAtUtc = null,
            MonitoringPassPhoneRevealClicksSpent = phoneSpent,
            MonitoringPassAutoRepliesSpent = repliesSpent,
            MonitoringPassSessionRestarts = restarts
        };
        return account;
    }

    [Fact]
    public void ForAccountPass_PhoneRevealCounterIsTelemetryOnly()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        budget.RecordPhoneRevealAttempts(10_000);

        Assert.Equal(10_000, budget.PhoneRevealClicksSpent);
        Assert.Equal(MonitoringTiming.MaxMessengerAutoRepliesPerAccountPass, budget.AutoRepliesCap);
        Assert.Equal(MonitoringTiming.MaxSessionRestartsPerAccountPass, budget.SessionRestartCap);
    }

    [Fact]
    public void ForAccountPass_UnfinishedPassRestoresSpentCounters()
    {
        // Возобновление незавершённого прохода (новая сессия / рестарт воркера):
        // счётчики восстанавливаются, полный бюджет повторно не выдаётся.
        var account = UnfinishedPassAccount(phoneSpent: 30, repliesSpent: 4, restarts: 2);

        var budget = AvitoAccountPassBudget.ForAccountPass(account);

        Assert.Equal(30, budget.PhoneRevealClicksSpent);
        Assert.Equal(4, budget.AutoRepliesSpent);
        Assert.Equal(2, budget.SessionRestarts);
    }

    [Fact]
    public void ForAccountPass_FinishedPassStartsClean()
    {
        var account = new AvitoAccount
        {
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddHours(-2),
            MonitoringPassFinishedAtUtc = DateTime.UtcNow.AddHours(-1),
            MonitoringPassPhoneRevealClicksSpent = 30,
            MonitoringPassAutoRepliesSpent = 4,
            MonitoringPassSessionRestarts = 2
        };

        var budget = AvitoAccountPassBudget.ForAccountPass(account);

        Assert.Equal(0, budget.PhoneRevealClicksSpent);
        Assert.Equal(0, budget.AutoRepliesSpent);
        Assert.Equal(0, budget.SessionRestarts);
        Assert.Equal(0, account.MonitoringPassPhoneRevealClicksSpent);
        Assert.Equal(0, account.MonitoringPassAutoRepliesSpent);
        Assert.Equal(0, account.MonitoringPassSessionRestarts);
    }

    [Fact]
    public void ForAccountPass_ProductionOrder_NewPassAfterFinishedDoesNotInheritSpent()
    {
        // Порядок production: FinishPass (счётчики расхода остались на аккаунте) →
        // BeginOrResumePass начал новый проход и воркер сбросил счётчики →
        // только затем создаётся бюджет. Если сброса нет, бюджет принял бы расход
        // завершённого прохода за расход нового — регрессия ревью.
        var account = new AvitoAccount
        {
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddHours(-2),
            MonitoringPassFinishedAtUtc = DateTime.UtcNow.AddHours(-1),
            MonitoringPassPhoneRevealClicksSpent = 40,
            MonitoringPassAutoRepliesSpent = 9,
            MonitoringPassSessionRestarts = 6
        };
        var passStarted = account.MonitoringPassStartedAtUtc;
        var passFinished = account.MonitoringPassFinishedAtUtc;
        var beganNewPass = MonitoringAccountResume.BeginOrResumePass(
            DateTime.UtcNow,
            ref passStarted,
            ref passFinished,
            account.MonitoringPassCompletedSubIds);
        account.MonitoringPassStartedAtUtc = passStarted;
        account.MonitoringPassFinishedAtUtc = passFinished;
        Assert.True(beganNewPass);

        // WorkerMonitoringService.BeginOrResumeAccountPass: новый проход — чистые счётчики.
        account.MonitoringPassPhoneRevealClicksSpent = 0;
        account.MonitoringPassAutoRepliesSpent = 0;
        account.MonitoringPassSessionRestarts = 0;

        var budget = AvitoAccountPassBudget.ForAccountPass(account);

        Assert.Equal(0, budget.PhoneRevealClicksSpent);
        Assert.Equal(0, budget.AutoRepliesSpent);
        Assert.Equal(0, budget.SessionRestarts);
    }

    [Fact]
    public void Mutations_PersistCallbackInvokedImmediately()
    {
        // Crash-window: каждая мутация бюджета сразу фиксируется в персистентности,
        // чтобы остановка процесса сразу после браузерного действия не возвращала
        // списанный резерв при возобновлении прохода.
        var persisted = 0;
        var budget = AvitoAccountPassBudget.ForAccountPass(
            UnfinishedPassAccount(phoneSpent: 3),
            persistChanged: _ => persisted++);

        // Восстановление состояния незавершённого прохода тоже фиксируется.
        Assert.Equal(1, persisted);

        budget.RecordPhoneRevealAttempts();
        Assert.Equal(2, persisted);

        budget.UnrecordPhoneRevealAttempts();
        Assert.Equal(3, persisted);

        Assert.True(budget.TryReserveAutoReply());
        Assert.Equal(4, persisted);

        Assert.True(budget.TryRegisterSessionRestart());
        Assert.Equal(5, persisted);
    }

    [Fact]
    public void Mutations_WriteThroughToAccount()
    {
        // Write-through: любой SaveAccountAsync в середине прохода фиксирует прогресс.
        var account = UnfinishedPassAccount();
        var budget = AvitoAccountPassBudget.ForAccountPass(account);

        budget.RecordPhoneRevealAttempts(5);
        Assert.Equal(5, account.MonitoringPassPhoneRevealClicksSpent);

        _ = budget.TryReserveAutoReply();
        Assert.Equal(1, account.MonitoringPassAutoRepliesSpent);

        _ = budget.TryRegisterSessionRestart();
        Assert.Equal(1, account.MonitoringPassSessionRestarts);
    }

    [Fact]
    public void RecordPhoneRevealAttempts_HasNoNumericLimit()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        budget.RecordPhoneRevealAttempts(10_000);
        budget.RecordPhoneRevealAttempts(3);

        Assert.Equal(10_003, budget.PhoneRevealClicksSpent);
    }

    [Fact]
    public void UnrecordPhoneRevealAttempts_RemovesConfirmedNoClick()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        budget.RecordPhoneRevealAttempts(5);
        budget.UnrecordPhoneRevealAttempts(2);

        Assert.Equal(3, budget.PhoneRevealClicksSpent);
    }

    [Fact]
    public void UnrecordPhoneRevealAttempts_NeverGoesBelowZero()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        budget.UnrecordPhoneRevealAttempts(7);

        Assert.Equal(0, budget.PhoneRevealClicksSpent);
    }

    [Fact]
    public void TryReserveAutoReply_AllowsUpToCapThenRefuses()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        for (var i = 0; i < budget.AutoRepliesCap; i++)
        {
            Assert.True(budget.TryReserveAutoReply());
        }

        Assert.False(budget.TryReserveAutoReply());
        Assert.Equal(0, budget.AutoRepliesRemaining);
    }

    [Fact]
    public void RefundAutoReply_RestoresOneUnit()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        Assert.True(budget.TryReserveAutoReply());
        Assert.True(budget.TryReserveAutoReply());
        budget.RefundAutoReply();

        Assert.Equal(1, budget.AutoRepliesSpent);
        Assert.Equal(budget.AutoRepliesCap - 1, budget.AutoRepliesRemaining);
    }

    [Fact]
    public void TryRegisterSessionRestart_AllowsUpToCapThenRefuses()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        for (var i = 0; i < budget.SessionRestartCap; i++)
        {
            Assert.True(budget.TryRegisterSessionRestart());
        }

        Assert.False(budget.TryRegisterSessionRestart());
        Assert.Equal(budget.SessionRestartCap, budget.SessionRestarts);
    }

    [Fact]
    public void Describe_IncludesAllCounters()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();
        budget.RecordPhoneRevealAttempts(2);
        _ = budget.TryReserveAutoReply();

        var description = budget.Describe();

        Assert.Contains(
            "phoneRevealClicks=2 (unlimited)",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            $"autoReplies=1/{MonitoringTiming.MaxMessengerAutoRepliesPerAccountPass}",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            $"sessionRestarts=0/{MonitoringTiming.MaxSessionRestartsPerAccountPass}",
            description,
            StringComparison.Ordinal);
    }
}
