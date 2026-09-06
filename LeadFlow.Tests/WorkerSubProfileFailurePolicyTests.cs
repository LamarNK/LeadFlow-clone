using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerSubProfileFailurePolicyTests
{
    [Fact]
    public void CdpJavaScriptProbeTimeout_IsHandledInsideCurrentSubProfile()
    {
        var timeout = AdsPowerCdpGuard.Timeout(
            "JavaScript-проверка страницы",
            TimeSpan.FromSeconds(5));

        Assert.True(WorkerMonitoringService.ShouldHandleAsSubProfileAutomationFailure(timeout));
    }

    [Fact]
    public void CdpJavaScriptProbeTimeout_IsDeferredOnlyOnce()
    {
        var timeout = AdsPowerCdpGuard.Timeout(
            "JavaScript-проверка страницы",
            TimeSpan.FromSeconds(15));

        Assert.True(WorkerMonitoringService.ShouldDeferSubProfileRetry(timeout, deferredRetry: false));
        Assert.False(WorkerMonitoringService.ShouldDeferSubProfileRetry(timeout, deferredRetry: true));
    }

    [Fact]
    public void OrdinaryTimeout_RemainsOutsideSubProfileRecovery()
    {
        var timeout = new TimeoutException("Внешняя операция не завершилась.");

        Assert.False(WorkerMonitoringService.ShouldHandleAsSubProfileAutomationFailure(timeout));
        Assert.False(WorkerMonitoringService.ShouldDeferSubProfileRetry(timeout, deferredRetry: false));
    }
}
