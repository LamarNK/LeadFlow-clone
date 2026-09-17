using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class PhoneRevealBudgetControllerTests
{
    [Fact]
    public void ShouldStop_RaisesBudgetOnBacklog_InsteadOfStopping()
    {
        var controller = new PhoneRevealBudgetController(10);

        // 10 кликов потрачено, хвост 130: контроллер обязан сначала поднять бюджет
        // (регрессия: раньше цикл выходил по базовому лимиту до подъёма).
        Assert.False(controller.ShouldStop(clicksTotal: 10, maskedPending: 130));
        Assert.Equal(40, controller.EffectiveBudget);
        Assert.True(controller.BudgetRaised);
    }

    [Fact]
    public void ShouldStop_StopsAtRaisedBudget()
    {
        var controller = new PhoneRevealBudgetController(10);
        Assert.False(controller.ShouldStop(10, 130));

        Assert.True(controller.ShouldStop(40, 130));
        Assert.Equal(40, controller.EffectiveBudget);
    }

    [Fact]
    public void ShouldStop_StopsWhenBacklogSmall()
    {
        var controller = new PhoneRevealBudgetController(10);

        // Хвост 12 ≤ бюджет+3: подъёма нет, стоп сразу.
        Assert.True(controller.ShouldStop(10, 12));
        Assert.Equal(10, controller.EffectiveBudget);
    }

    [Fact]
    public void ShouldStop_RaisesOnlyOncePerPass()
    {
        var controller = new PhoneRevealBudgetController(10);
        Assert.False(controller.ShouldStop(10, 130));
        Assert.Equal(40, controller.EffectiveBudget);

        // Второй вызов с тем же хвостом не растит бюджет повторно.
        Assert.True(controller.ShouldStop(40, 130));
        Assert.True(controller.ShouldStop(45, 130));
        Assert.Equal(40, controller.EffectiveBudget);
    }

    [Fact]
    public void ShouldStop_DoesNotReduceWatchDrivenBudget()
    {
        // Бюджет 50 поднят числом phone-watch: адаптивный подъём не должен урезать его до 40.
        var controller = new PhoneRevealBudgetController(50);

        Assert.True(controller.ShouldStop(50, 130));
        Assert.Equal(50, controller.EffectiveBudget);
    }

    [Fact]
    public void ShouldStop_AllowsClicksBelowBudget()
    {
        var controller = new PhoneRevealBudgetController(8);

        Assert.False(controller.ShouldStop(0, 0));
        Assert.False(controller.ShouldStop(7, 0));
    }
}
