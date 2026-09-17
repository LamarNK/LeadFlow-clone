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
    public void ForAccountPass_CapsAreFlatRegardlessOfSubProfiles()
    {
        // Плоские потолки: локальная жеребьёвка субпрофиля (6–10) остаётся стартом,
        // адаптивный подъём может дорасти до потолка аккаунта даже на одном субпрофиле.
        var budget = AvitoAccountPassBudget.ForAccountPass();

        Assert.Equal(MonitoringTiming.MaxPhoneRevealsPerAccountPass, budget.PhoneRevealClicksCap);
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
        Assert.Equal(10, budget.PhoneRevealClicksRemaining);
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
        Assert.Equal(budget.PhoneRevealClicksCap, budget.PhoneRevealClicksRemaining);
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

        _ = budget.ReservePhoneRevealClicks(1);
        Assert.Equal(2, persisted);

        budget.RefundPhoneRevealClicks(1);
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

        _ = budget.ReservePhoneRevealClicks(5);
        Assert.Equal(5, account.MonitoringPassPhoneRevealClicksSpent);

        _ = budget.TryReserveAutoReply();
        Assert.Equal(1, account.MonitoringPassAutoRepliesSpent);

        _ = budget.TryRegisterSessionRestart();
        Assert.Equal(1, account.MonitoringPassSessionRestarts);
    }

    [Fact]
    public void ReservePhoneRevealClicks_GrantsOnlyRemainingBudget()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        var cap = budget.PhoneRevealClicksCap;
        Assert.Equal(10, budget.ReservePhoneRevealClicks(10));
        Assert.Equal(cap - 10, budget.PhoneRevealClicksRemaining);

        // Запрос больше остатка: выдаём только остаток, не больше.
        var remaining = budget.PhoneRevealClicksRemaining;
        Assert.Equal(remaining, budget.ReservePhoneRevealClicks(cap));
        Assert.Equal(0, budget.PhoneRevealClicksRemaining);
        Assert.Equal(0, budget.ReservePhoneRevealClicks(3));
    }

    [Fact]
    public void RefundPhoneRevealClicks_RestoresBudgetAfterConfirmedNoClick()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        _ = budget.ReservePhoneRevealClicks(5);
        budget.RefundPhoneRevealClicks(2);

        Assert.Equal(3, budget.PhoneRevealClicksSpent);
        Assert.Equal(budget.PhoneRevealClicksCap - 3, budget.PhoneRevealClicksRemaining);
    }

    [Fact]
    public void RefundPhoneRevealClicks_NeverGoesBelowZero()
    {
        var budget = AvitoAccountPassBudget.ForAccountPass();

        budget.RefundPhoneRevealClicks(7);

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
        _ = budget.ReservePhoneRevealClicks(2);
        _ = budget.TryReserveAutoReply();

        var description = budget.Describe();

        Assert.Contains(
            $"phoneRevealClicks=2/{MonitoringTiming.MaxPhoneRevealsPerAccountPass}",
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

public sealed class PhoneRevealBudgetControllerCeilingTests
{
    [Fact]
    public void InitialBudget_IsClampedToAccountCeiling()
    {
        // Локальный бюджет 50 (поднят phone-watch), но на проход аккаунта
        // осталось 12 — кликаем не больше остатка.
        var controller = new PhoneRevealBudgetController(50, hardCeiling: 12);

        Assert.Equal(12, controller.EffectiveBudget);
        Assert.True(controller.ShouldStop(12, 0));
    }

    [Fact]
    public void AdaptiveRaise_CannotExceedAccountCeiling()
    {
        var controller = new PhoneRevealBudgetController(10, hardCeiling: 15);

        // Хвост 130 обычно поднимает бюджет до 40, но потолок аккаунта — 15.
        Assert.False(controller.ShouldStop(10, 130));
        Assert.Equal(15, controller.EffectiveBudget);
        Assert.True(controller.BudgetRaised);
        Assert.True(controller.ShouldStop(15, 130));
    }

    [Fact]
    public void SingleSubProfile_CanAdaptivelyReachFullAccountCeiling()
    {
        // Регрессия (ревью): потолок аккаунта плоский — даже один субпрофиль
        // с большим хвостом замаскированных карточек адаптивно дорастает до 40,
        // как это было до появления бюджета прохода.
        var controller = new PhoneRevealBudgetController(
            10,
            hardCeiling: MonitoringTiming.MaxPhoneRevealsPerAccountPass);

        Assert.False(controller.ShouldStop(10, 130));
        Assert.Equal(40, controller.EffectiveBudget);
        Assert.False(controller.ShouldStop(39, 130));
        Assert.True(controller.ShouldStop(40, 130));
    }

    [Fact]
    public void WithoutCeiling_BehaviorUnchanged()
    {
        var controller = new PhoneRevealBudgetController(10);

        Assert.False(controller.ShouldStop(10, 130));
        Assert.Equal(40, controller.EffectiveBudget);
    }

    [Fact]
    public void ZeroCeiling_StopsImmediately()
    {
        // Бюджет прохода исчерпан на предыдущих субпрофилях — не кликаем вовсе.
        var controller = new PhoneRevealBudgetController(10, hardCeiling: 0);

        Assert.Equal(0, controller.EffectiveBudget);
        Assert.True(controller.ShouldStop(0, 130));
    }
}
